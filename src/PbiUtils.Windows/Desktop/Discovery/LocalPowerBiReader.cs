// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Discovery\LocalPowerBiReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将当前会话的 Desktop 子引擎映射到回环端口, 使用 TOM 只读读取模型摘要.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Application.Connections;
using System.Diagnostics;
using Microsoft.AnalysisServices.Tabular;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Desktop.Discovery;

/// <summary>仅连接系统发现的本机 Power BI 子引擎, 不读工作区文件或执行模型写操作.</summary>
public sealed class LocalPowerBiReader : IPowerBiReader
{
    /// <summary>在线程池读取进程与 TCP 表, 不阻塞 UI; 发现失败使用稳定错误码.</summary>
    /// <param name="cancellationToken">取消进程发现.</param>
    /// <returns>当前会话的实例候选与已发现端口.</returns>
    public Task<IReadOnlyList<PowerBiEndpoint>> DiscoverAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<PowerBiEndpoint>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var processes = NativeDiscovery.Processes();
            var ports = NativeDiscovery.Ports();
            using var current = Process.GetCurrentProcess();
            List<PowerBiEndpoint> result = [];
            foreach (var desktop in processes.Where(p => p.Name.Equals("PBIDesktop.exe", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.GetProcessById(desktop.Id);
                    if (process.SessionId != current.SessionId) continue;
                    var title = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? "Power BI Desktop" : process.MainWindowTitle;
                    var engines = processes.Where(p => p.ParentId == desktop.Id && p.Name.Equals("msmdsrv.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var found = false;
                    foreach (var engine in engines)
                    {
                        using var child = Process.GetProcessById(engine.Id);
                        if (child.SessionId != current.SessionId || child.StartTime < process.StartTime) continue;
                        foreach (var port in ports.Where(p => p.ProcessId == engine.Id).Select(p => p.Port).Distinct())
                        {
                            result.Add(new(desktop.Id, title, engine.Id, child.StartTime.ToUniversalTime().Ticks, port));
                            found = true;
                        }
                    }
                    if (!found) result.Add(new(desktop.Id, title, null, null, null));
                }
                // 进程快照后关闭或无权读取的窗口不伪装成可连接实例.
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { result.Add(new(desktop.Id, "Power BI Desktop (无法访问)", null, null, null, "POWER_BI_ACCESS_DENIED")); }
            }
            return result.OrderBy(p => p.ProcessId).ThenBy(p => p.Port).ToArray();
        }
        catch (System.ComponentModel.Win32Exception) { throw new AppException("POWER_BI_DISCOVERY_FAILED", "无法读取本机进程或端口, 请确认当前用户权限后重试", 3); }
    }, cancellationToken);

    /// <summary>复核 PID 与启动时间后读取 TOM; 同步 SDK 在线程池运行, 连接和命令分别限时 3 秒和 5 秒.</summary>
    /// <param name="endpoint">已由系统发现的实例, 连接前再次校验.</param>
    /// <param name="cancellationToken">在同步 SDK 调用边界检查的取消令牌.</param>
    /// <returns>模型名称、兼容级别和对象数量; 失败抛出安全应用错误.</returns>
    public async Task<IReadOnlyList<PowerBiModel>> ReadAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.Port is not (> 0 and <= 65535) || endpoint.EngineProcessId is null)
            throw new AppException("POWER_BI_UNAVAILABLE", "实例没有可用的本地端口", 3);
        var current = await DiscoverAsync(cancellationToken);
        if (!current.Any(p => p.ProcessId == endpoint.ProcessId && p.EngineProcessId == endpoint.EngineProcessId && p.EngineStarted == endpoint.EngineStarted && p.Port == endpoint.Port))
            throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭或重新启动, 请刷新", 3);
        return await Task.Run<IReadOnlyList<PowerBiModel>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new Server();
            try
            {
                // 无自定义连接字符串入口, 不读取凭据; 本机集成身份验证不访问云服务.
                server.Connect($"Data Source=localhost:{endpoint.Port};Connect Timeout=3;Timeout=5;Application Name=pbi-utils");
                cancellationToken.ThrowIfCancellationRequested();
                List<PowerBiModel> models = [];
                foreach (Database database in server.Databases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var model = database.Model;
                    models.Add(new(ConnectedModelReader.DesktopModelName(endpoint.Title), database.CompatibilityLevel, model.Tables.Count, model.Tables.Sum(table => table.Measures.Count)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return models;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new AppException("POWER_BI_CONNECTION_FAILED", "无法读取模型, 实例可能正在加载、已关闭或权限不足; 请稍后刷新", 4);
            }
            // using 无论成功、失败还是取消均释放 TOM 连接; 不对共享引擎调用写入或终止命令.
        }, cancellationToken);
    }

    /// <summary>通过官方 Bridge 读取文件路径并在前后核对同一引擎, 不从标题或命令行推测.</summary>
    /// <param name="endpoint">当前发现的完整 Desktop 引擎身份.</param>
    /// <param name="cancellationToken">取消 Bridge 与进程复核, 不吞取消.</param>
    /// <returns>已核验本机路径, 或明确未保存/不可读取状态; 引擎变化拒绝当前发现结果.</returns>
    public async Task<PowerBiFileIdentity> ReadFileIdentityAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = await DesktopBridgeClient.InspectAsync(endpoint.ProcessId, cancellationToken);
            using var process = Process.GetProcessById(endpoint.ProcessId);
            if (file.Started != process.StartTime.ToUniversalTime()
                || !(await DiscoverAsync(cancellationToken)).Any(candidate => candidate.ProcessId == endpoint.ProcessId
                    && candidate.EngineProcessId == endpoint.EngineProcessId && candidate.EngineStarted == endpoint.EngineStarted && candidate.Port == endpoint.Port))
                throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭或重启, 请重新发现", 3);
            return new(DesktopModelFileIdentity.Normalize(file.Path), "verified");
        }
        catch (AppException error) when (error.Code == "DESKTOP_FILE_REQUIRED") { return new(null, "unsaved", error.Code); }
        catch (AppException error) when (error.Code != "POWER_BI_INSTANCE_CLOSED") { return new(null, "unavailable", error.Code); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(null, "unavailable", "DESKTOP_FILE_IDENTITY_UNAVAILABLE");
        }
    }

}
