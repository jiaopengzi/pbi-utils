// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\PowerBiService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 编排本机 Power BI 实例发现和只读模型摘要, 隔离平台与 TOM 依赖.

using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>平台发现的候选实例, 端口只能由系统发现, 不接受网页连接字符串.</summary>
public sealed record PowerBiEndpoint(int ProcessId, string Title, int? EngineProcessId, long? EngineStarted, int? Port, string? ErrorCode = null)
{
    /// <summary>由实际发现的进程、引擎启动时间和端口生成展示身份, 不作为连接授权.</summary>
    /// <value>完整有效身份的不透明稳定摘要; 未就绪、无权限或身份缺失时为 null. 标题变化不改变身份.</value>
    public string? InstanceId => ProcessId > 0 && EngineProcessId > 0 && EngineStarted > 0 && Port is > 0 and <= 65535 && ErrorCode is null
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{ProcessId}|{EngineProcessId}|{EngineStarted}|{Port}"))))
        : null;
}
/// <summary>不包含表达式、数据源或行数据的模型摘要.</summary>
public sealed record PowerBiModel(string Name, int CompatibilityLevel, int TableCount, int MeasureCount)
{
    /// <summary>扫描只读取在线模型元数据, 报表页面数量保持未提供.</summary>
    /// <value>与当前连接快照共用的模型展示摘要.</value>
    public ModelSummary Summary => ModelSummaryFactory.FromCounts(CompatibilityLevel, TableCount, MeasureCount);
}
/// <summary>单个实例的可展示状态, InstanceId 仅供与当前 Desktop 来源比对; 无有效身份时不标记已连接.</summary>
public sealed record PowerBiInstance(int ProcessId, string Title, int? Port, string Status, string? ErrorCode, string? Message, IReadOnlyList<PowerBiModel> Models, string? InstanceId = null,
    string? FilePath = null, string FileIdentityStatus = "unavailable", string? FileIdentityErrorCode = null);
/// <summary>由平台权威文件接口取得的可观察身份, unavailable/unsaved 不伪造路径.</summary>
public sealed record PowerBiFileIdentity(string? Path, string Status, string? ErrorCode = null);
/// <summary>一次实际扫描的结果, 空列表表示没有打开的实例.</summary>
public sealed record PowerBiSnapshot(string Timestamp, IReadOnlyList<PowerBiInstance> Instances);

/// <summary>本机发现与只读连接适配边界.</summary>
public interface IPowerBiReader
{
    /// <summary>发现当前登录会话的 Desktop 及其本地引擎; 失败抛出安全应用错误.</summary>
    /// <param name="cancellationToken">取消本次发现.</param>
    /// <returns>已发现的候选, 无实例时为空列表.</returns>
    Task<IReadOnlyList<PowerBiEndpoint>> DiscoverAsync(CancellationToken cancellationToken);
    /// <summary>只读获取已发现实例的模型摘要; 取消必须传播并释放连接.</summary>
    /// <param name="endpoint">平台发现的候选, 不能由网页指定.</param>
    /// <param name="cancellationToken">取消本次读取.</param>
    /// <returns>不含数据行或表达式的模型概要.</returns>
    Task<IReadOnlyList<PowerBiModel>> ReadAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken);
    /// <summary>读取绑定实例的权威文件路径; 平台不支持时明确 unavailable, 不猜测标题.</summary>
    /// <param name="endpoint">已发现并已读取的准确实例.</param>
    /// <param name="cancellationToken">取消文件状态读取.</param>
    /// <returns>已核验路径或明确无法提供的状态.</returns>
    Task<PowerBiFileIdentity> ReadFileIdentityAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
        => Task.FromResult(new PowerBiFileIdentity(null, "unavailable", "DESKTOP_FILE_IDENTITY_UNSUPPORTED"));
}

/// <summary>供入口复用的只读实例用例, 单个实例失败不隐藏其他实例.</summary>
/// <param name="reader">当前平台适配器.</param>
/// <param name="clock">UTC 扫描时间来源.</param>
public sealed class PowerBiService(IPowerBiReader reader, TimeProvider clock, ILogger<PowerBiService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<PowerBiService>.Instance;

    // 防止重复刷新或多个桥接请求无限堆积同步 TOM 工作.
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>扫描并读取模型概要; 并发刷新返回 BUSY, 取消向调用者传播.</summary>
    /// <param name="cancellationToken">调用者取消或桥接超时令牌.</param>
    /// <returns>带扫描时间和各实例状态的只读快照.</returns>
    public async Task<PowerBiSnapshot> ExecuteAsync(CancellationToken cancellationToken = default) => await BusinessLog.RunAsync<PowerBiSnapshot>(log, "PowerBi.Execute", async () =>
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new AppException("BUSY", "正在读取 Power BI, 请稍后刷新", 4);
        try
        {
            var endpoints = await reader.DiscoverAsync(cancellationToken);
            List<PowerBiInstance> instances = [];
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (endpoint.ErrorCode is { } code)
                {
                    instances.Add(new(endpoint.ProcessId, endpoint.Title, endpoint.Port, "unavailable", code, "无法访问此实例, 请使用相同 Windows 用户和权限运行后刷新", []));
                    continue;
                }
                try
                {
                    if (endpoint.Port is null)
                    {
                        var fileState = await reader.ReadFileIdentityAsync(endpoint, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        var verified = fileState.Status == "verified";
                        instances.Add(new(endpoint.ProcessId, endpoint.Title, null, verified ? "unavailable" : "starting",
                            verified ? DesktopModelAvailability.NoLocalModelCode : null,
                            verified ? DesktopModelAvailability.NoLocalModelMessage : "尚未发现本地模型端口, 请打开报表后刷新", [],
                            FilePath: fileState.Path, FileIdentityStatus: fileState.Status, FileIdentityErrorCode: fileState.ErrorCode));
                        continue;
                    }
                    var models = await reader.ReadAsync(endpoint, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = await reader.ReadFileIdentityAsync(endpoint, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (models.Count == 0)
                    {
                        instances.Add(new(endpoint.ProcessId, endpoint.Title, endpoint.Port, "unavailable", DesktopModelAvailability.NoLocalModelCode,
                            DesktopModelAvailability.NoLocalModelMessage, models, endpoint.InstanceId, file.Path, file.Status, file.ErrorCode));
                        continue;
                    }
                    instances.Add(new(endpoint.ProcessId, endpoint.Title, endpoint.Port, "connected", null, null, models, endpoint.InstanceId,
                        file.Path, file.Status, file.ErrorCode));
                }
                catch (AppException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    instances.Add(new(endpoint.ProcessId, endpoint.Title, endpoint.Port, "unavailable", ex.Code, ex.Message, []));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture), instances);
        }
        finally { gate.Release(); }
    });
}
