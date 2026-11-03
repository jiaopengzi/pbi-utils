// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopModelFileRuntime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 隔离 Desktop 本机进程发现、官方 Bridge 状态读取与安全文件启动.

using PbiUtils.Windows.Desktop.Discovery;
using PbiUtils.Windows.Desktop;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>只发现当前 Windows 会话的 Desktop, 不读取窗口标题推测文件路径.</summary>
public sealed class DesktopModelFileRuntime(ILogger? logger = null) : IDesktopModelFileRuntime
{
    private readonly ILogger log = logger ?? NullLogger.Instance;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DesktopModelFileState>> InspectAsync(CancellationToken token)
    {
        var reader = new LocalPowerBiReader();
        List<DesktopModelFileState> result = [];
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("PBIDesktop"))
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (process.SessionId != self.SessionId) continue;
                    var started = process.StartTime.ToUniversalTime();
                    try
                    {
                        var state = await DesktopBridgeClient.InspectAsync(process.Id, token);
                        if (state.Started != started) continue;
                        // Bridge 等待期间引擎可能刚完成启动, 不用其就绪前的端口快照裁决无模型.
                        var endpoints = await reader.DiscoverAsync(token);
                        var candidates = endpoints.Where(endpoint => endpoint.ProcessId == process.Id && endpoint.EngineProcessId is not null && endpoint.Port is > 0).ToArray();
                        var (ready, modelError) = candidates.Length == 1 ? await ReadModelStateAsync(reader, candidates[0], token)
                            : (Ready: false, ErrorCode: candidates.Length == 0 ? DesktopModelAvailability.NoLocalModelCode : null);
                        // Bridge 接受文件状态读取后才裁决无模型; 加载阶段的拒绝仍由下方失败分支保留等待.
                        if (modelError is not null && !state.HasUnsavedChanges && DesktopModelFileIdentity.HasRemoteBinding(state.Path))
                            modelError = DesktopModelAvailability.RemoteReportCode;
                        result.Add(new(state.ProcessId, state.Started, state.Path, ready, ModelErrorCode: modelError));
                    }
                    // 未命名窗口由官方状态确认没有文件, 不能视作身份读取失败.
                    catch (AppException error) when (error.Code == "DESKTOP_FILE_REQUIRED")
                    { result.Add(new(process.Id, started, null, false)); }
                    catch (AppException error)
                    { result.Add(new(process.Id, started, null, false, error.Code)); }
                }
                // 发现后正常关闭的窗口从快照排除; 无权限窗口保留为不可核验.
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
                catch (Win32Exception) { result.Add(new(process.Id, default, null, false, "DESKTOP_ACCESS_DENIED")); }
            }
        }
        return result;
    }

    /// <summary>监听端口不能证明模型已加载, 用既有只读摘要确认唯一数据库后才完成等待.</summary>
    /// <param name="reader">已有 Desktop 只读发现适配器.</param>
    /// <param name="endpoint">唯一且复核过的本机引擎入口.</param>
    /// <param name="token">在同步 SDK 边界检查的取消令牌.</param>
    /// <returns>唯一模型已就绪、明确零模型终态, 或暂时无法读取需要继续等待.</returns>
    private static async Task<(bool Ready, string? ErrorCode)> ReadModelStateAsync(LocalPowerBiReader reader, PowerBiEndpoint endpoint, CancellationToken token)
    {
        try
        {
            var models = await reader.ReadAsync(endpoint, token);
            return (models.Count == 1, models.Count == 0 ? DesktopModelAvailability.NoLocalModelCode : null);
        }
        catch (AppException error) when (error.Code is "POWER_BI_CONNECTION_FAILED" or "POWER_BI_INSTANCE_CLOSED" or "POWER_BI_UNAVAILABLE") { return (false, null); }
    }

    /// <inheritdoc/>
    public Task<string> ResolveExecutableAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        return DesktopInstallation.Resolve();
    }, token);

    /// <inheritdoc/>
    public Task LaunchAsync(string executable, string path, CancellationToken token)
    {
        using var operation = BusinessLog.Begin(log, "Desktop.FileLaunch");
        try
        {
            token.ThrowIfCancellationRequested();
            var target = DesktopInstallation.ResolveLaunchTarget(executable);
            if (UsesStoreActivation(target))
            {
                DesktopStoreActivator.Open(target.AppUserModelId!, QuoteStoreFileArgument(path));
                operation.Complete();
                return Task.CompletedTask;
            }
            var start = CreateStartInfo(target.Executable, path);
            using var process = Process.Start(start) ?? throw new AppException("DESKTOP_LAUNCH_FAILED", "无法启动 Power BI Desktop, 请尝试手工打开文件后连接", 3);
            operation.Complete();
        }
        catch (Exception error)
        {
            // 先记录原始安全类型和数字码, 用户仍只看到固定友好提示.
            BusinessLog.ConnectionFailure(log, "Desktop.FileLaunch", error);
            operation.Fail(error);
            if (error is Win32Exception or InvalidOperationException or IOException or COMException or InvalidCastException or ArgumentException)
                throw new AppException("DESKTOP_LAUNCH_FAILED", "无法启动 Power BI Desktop, 请尝试手工打开文件后连接", 3);
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>Store FullTrust 应用通过官方 Launch 契约激活, 只接受固定包家族的清单身份.</summary>
    /// <param name="target">本机安装解析和文件身份验证过的启动目标.</param>
    /// <returns>是否应调用 Store 激活接口; 无效包身份明确拒绝.</returns>
    public static bool UsesStoreActivation(DesktopLaunchTarget target)
    {
        if (target.AppUserModelId is null) return false;
        const string prefix = "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe!";
        if (target.AppUserModelId.StartsWith(prefix, StringComparison.Ordinal) && target.AppUserModelId.Length > prefix.Length && target.AppUserModelId.Length <= prefix.Length + 128
            && target.AppUserModelId.Skip(prefix.Length).All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')) return true;
        throw new AppException("DESKTOP_LAUNCH_FAILED", "无法启动 Power BI Desktop, 请尝试手工打开文件后连接", 3);
    }

    /// <summary>完整普通文件路径作为一个引用参数传给已核验的 FullTrust 应用, 不执行 Shell.</summary>
    /// <param name="path">原生授权的 PBIP/PBIX 完整路径.</param>
    /// <returns>保留空格和特殊字符的单个文件参数.</returns>
    public static string QuoteStoreFileArgument(string path)
    {
        var full = DesktopModelFileIdentity.Read(path).EntryPath;
        if (full.Contains('"')) throw new AppException("LOCAL_MODEL_FILE_UNSAFE", "请选择本机的普通 PBIP/PBIX 文件, 不支持网络路径或链接", 3);
        return "\"" + full + "\"";
    }

    /// <summary>构建无 Shell 的明确 EXE 与单个文件参数, 不执行文件关联命令或拼接字符串.</summary>
    /// <param name="executable">已核实的 Desktop 完整 EXE 路径.</param>
    /// <param name="path">本地文件完整路径.</param>
    /// <returns>仅包含一个文件参数的启动配置.</returns>
    public static ProcessStartInfo CreateStartInfo(string executable, string path)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add(DesktopModelFileIdentity.Normalize(path));
        return start;
    }
}
