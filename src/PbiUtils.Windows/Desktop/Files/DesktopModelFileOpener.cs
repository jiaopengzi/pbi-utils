// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopModelFileOpener.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过 Power BI Desktop 打开本地模型文件并精确复用已有文件实例.

using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>文件启动和当前会话发现的可测试边界, 不向共享业务层泄漏窗口类型.</summary>
public interface IDesktopModelFileRuntime
{
    /// <summary>读取当前会话窗口的权威文件身份与引擎就绪状态.</summary>
    /// <param name="token">停止发现的令牌.</param>
    /// <returns>全部已发现窗口, 包含无法核验文件身份的状态.</returns>
    Task<IReadOnlyList<DesktopModelFileState>> InspectAsync(CancellationToken token);

    /// <summary>解析并验证本机安装的官方 Desktop 可执行文件.</summary>
    /// <param name="token">停止安装发现的令牌.</param>
    /// <returns>真实完整 EXE 路径; 未安装时抛出友好应用错误.</returns>
    Task<string> ResolveExecutableAsync(CancellationToken token);

    /// <summary>将完整模型文件路径作为单个参数启动已验证的 Desktop, 不关闭任何窗口.</summary>
    /// <param name="executable">已核实的 Desktop EXE 路径.</param>
    /// <param name="path">待打开的 PBIP/PBIX 入口.</param>
    /// <param name="token">仅在启动前检查的取消令牌.</param>
    Task LaunchAsync(string executable, string path, CancellationToken token);
}

/// <summary>单个 Desktop 的文件状态; ErrorCode 表示身份无法核验, ModelErrorCode 表示已核验文件没有可用本地模型.</summary>
public sealed record DesktopModelFileState(int ProcessId, DateTime Started, string? Path, bool IsReady, string? ErrorCode = null, string? ModelErrorCode = null);

/// <summary>绑定确切文件与进程启动身份, 等待有界且取消不终止用户 Desktop.</summary>
public sealed class DesktopModelFileOpener : ILocalModelFileOpener
{
    private readonly IDesktopModelFileRuntime runtime;
    private readonly ILogger logger;
    private readonly TimeSpan timeout;
    private readonly TimeSpan pollInterval;

    /// <summary>创建生产适配器, 使用官方本机注册与 Bridge.</summary>
    /// <param name="logger">只记录安全业务阶段的日志.</param>
    public DesktopModelFileOpener(ILogger? logger = null) : this(new DesktopModelFileRuntime(logger), logger) { }

    /// <summary>注入可控运行边界以验证窗口复用、取消与超时, 参数不来自网页.</summary>
    /// <param name="runtime">实际文件启动和发现适配器.</param>
    /// <param name="logger">可选业务日志.</param>
    /// <param name="timeout">完整操作上限, 默认 120 秒.</param>
    /// <param name="pollInterval">发现间隔, 默认 500 毫秒.</param>
    public DesktopModelFileOpener(IDesktopModelFileRuntime runtime, ILogger? logger = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        this.runtime = runtime;
        this.logger = logger ?? NullLogger.Instance;
        this.timeout = timeout ?? TimeSpan.FromSeconds(120);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (this.timeout <= TimeSpan.Zero || this.pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <inheritdoc/>
    public Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token) => BusinessLog.RunAsync(logger, "localModel.openFile", () => OpenCoreAsync(path, token));

    /// <summary>复核入口绑定、PID、启动时间和引擎就绪状态, 文件切换或重启明确失败.</summary>
    /// <param name="target">本次打开的后端身份票据.</param>
    /// <param name="token">停止复核的令牌.</param>
    /// <returns>成功表示仍为所选文件的唯一就绪实例.</returns>
    public async Task VerifyAsync(LocalModelFileTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var identity = DesktopModelFileIdentity.Read(target.Path);
        if (!string.Equals(identity.ReportPath, target.ReportPath, StringComparison.OrdinalIgnoreCase))
            throw new AppException("LOCAL_MODEL_FILE_CHANGED", "所选工程入口已变化, 请重新选择", 3);
        var matches = Matches(identity, await runtime.InspectAsync(token));
        if (matches.Length != 1 || matches[0].ProcessId != target.ProcessId || matches[0].Started != target.Started || !matches[0].IsReady)
            throw new AppException("DESKTOP_FILE_CHANGED", "Desktop 已切换文件、重启或模型尚未就绪, 请重新连接", 3);
    }

    /// <summary>先核验已有窗口再启动, 无法排除重复文件的窗口会阻止新开.</summary>
    /// <param name="path">本机入口路径.</param>
    /// <param name="token">调用方取消令牌.</param>
    /// <returns>权威文件身份与引擎就绪后返回绑定票据.</returns>
    private async Task<LocalModelFileTarget> OpenCoreAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var identity = DesktopModelFileIdentity.Read(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var states = await runtime.InspectAsync(deadline.Token);
            var matches = Matches(identity, states);
            if (matches.Length == 0)
            {
                // 同一路径可能已在 Desktop 转为尚未保存的本地复合模型, 优先核验实际窗口.
                if (identity.IsRemoteReport) throw DesktopModelAvailability.Error(true);
                var executable = await runtime.ResolveExecutableAsync(deadline.Token);
                // 安装发现期间其他入口可能已经打开此文件, 启动前重新检查全部窗口.
                states = await runtime.InspectAsync(deadline.Token);
                matches = Matches(identity, states);
                if (DesktopModelFileIdentity.Read(path) != identity) throw new AppException("LOCAL_MODEL_FILE_CHANGED", "所选工程入口已变化, 请重新选择", 3);
                if (matches.Length == 0)
                {
                    if (states.Any(state => state.ErrorCode is not null))
                        throw new AppException("DESKTOP_FILE_IDENTITY_UNAVAILABLE", "无法核验已打开的 Desktop 文件; 请检查 Desktop Bridge、弹窗和权限后重试", 3);
                    await runtime.LaunchAsync(executable, identity.EntryPath, deadline.Token);
                }
            }
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (DesktopModelFileIdentity.Read(path) != identity) throw new AppException("LOCAL_MODEL_FILE_CHANGED", "所选工程入口已变化, 请重新选择", 3);
                if (matches is [{ ModelErrorCode: not null } unavailable])
                    throw DesktopModelAvailability.Error(unavailable.ModelErrorCode == DesktopModelAvailability.RemoteReportCode);
                if (matches is [{ IsReady: true } state]) return new(identity.EntryPath, state.ProcessId, state.Started, identity.ReportPath);
                await Task.Delay(pollInterval, deadline.Token);
                matches = Matches(identity, await runtime.InspectAsync(deadline.Token));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new AppException("DESKTOP_OPEN_TIMEOUT", "Desktop 尚未完成打开模型; 请处理其弹窗并等待加载后重试连接", 3); }
    }

    /// <summary>拒绝重复完整路径匹配, 不用标题、同名文件或启动 PID 推测文件身份.</summary>
    /// <param name="identity">安全入口和绑定.</param>
    /// <param name="states">当前会话窗口快照.</param>
    /// <returns>零个或一个精确匹配; 多窗口抛出歧义错误.</returns>
    private static DesktopModelFileState[] Matches(DesktopModelFileIdentity identity, IReadOnlyList<DesktopModelFileState> states)
    {
        var matches = states.Where(state => state.Path is not null && state.ErrorCode is null && identity.Matches(state.Path)).ToArray();
        if (matches.Length > 1) throw new AppException("DESKTOP_FILE_AMBIGUOUS", "同一文件已在多个 Desktop 窗口打开, 请保留一个窗口后连接", 3);
        return matches;
    }
}
