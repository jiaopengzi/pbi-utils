// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\DesktopConfigurationAssets.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从官方 Desktop 文件绑定和独立完整模型基线取得内部配置资产能力, 不向网页提供文件路径授权.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader : IModelConfigurationAssetWorkspace
{
    /// <summary>仅在宿主内部保存真实文件身份, 不把文件路径作为模型写入权限.</summary>
    private sealed record ConfigurationBinding(DesktopFileTarget File, string Entry, string FileIdentity, string Root);
    /// <summary>只读核实当前 Desktop 的 PBIP/PBIR/PBIX 文件绑定, 从原模型取得配置资产归属; 缺少可信文件能力时返回 null.</summary>
    /// <param name="snapshot">宿主当前连接目标及完整权威基线, 不接受网页路径.</param>
    /// <param name="token">发现、官方文件状态及同步模型读取边界的取消令牌.</param>
    /// <returns>宿主内部的工程资产根和绑定复核, 不改变在线 model:// 身份.</returns>
    /// <exception cref="AppException">目标、进程、工程绑定或完整模型基线变化.</exception>
    public async Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Connection?.Kind != "desktop" || !snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal)) return null;
        var target = await DesktopTargetAsync(snapshot.ProjectPath, token);
        var binding = await ReadConfigurationBindingAsync(target, token);
        if (binding is null) return null;
        async Task Verify(CancellationToken ct)
        {
            await DesktopTargetAsync(snapshot.ProjectPath, ct);
            var current = await DesktopBridgeClient.VerifyAsync(binding.File, ct);
            if (!ConfigurationEntry(current, ct).Equals(binding.Entry, StringComparison.OrdinalIgnoreCase) || ProjectFileIdentity.Read(binding.Entry) != binding.FileIdentity)
                throw new AppException("DESKTOP_FILE_CHANGED", "Desktop 配置工程绑定已变化, 未操作其他文件", 3);
            var authority = await ReadDesktopAuthorityAsync(target, ct);
            if (authority.ProjectId != snapshot.ProjectId || authority.BaselineId != snapshot.BaselineId)
                throw new AppException("STALE_BASELINE", "Desktop 模型在准备配置资产期间已变化, 未覆盖", 3);
        }
        var context = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var server = ConnectDesktop(target);
            var database = DesktopDatabase(server);
            AssertDesktopBaseline(database, snapshot, target);
            return TemplateMeasureConfigurationStorage.ReadAssetContext(database, binding.Root, binding.Entry, Verify);
        }, token);
        await Verify(token);
        return context;
    }

    /// <summary>只核实官方文件身份而不读取生成对象归属, 供所有模型编辑检查同工程未知资产提交.</summary>
    private static async Task<ConfigurationBinding?> ReadConfigurationBindingAsync(Target target, CancellationToken token)
    {
        DesktopFileTarget file;
        try { file = await DesktopBridgeClient.InspectAsync(target.Endpoint!.ProcessId, token); }
        catch (AppException error) when (error.Code is "DESKTOP_FILE_REQUIRED" or "DESKTOP_BRIDGE_UNSUPPORTED" or "DESKTOP_BRIDGE_UNAVAILABLE"
            or "DESKTOP_BRIDGE_TIMEOUT" or "DESKTOP_BRIDGE_REJECTED")
        { return null; }
        var entry = ConfigurationEntry(file, token);
        return new(file, entry, ProjectFileIdentity.Read(entry), TemplateMeasureConfigurationStorage.ProjectRoot(entry));
    }

    /// <summary>真实权威读取后的可选清理尾部, 只消费当前官方根的completed责任; 文件能力缺失或清理繁忙不污染模型回读.</summary>
    private async Task<ProjectSnapshot> RetryCompletedConfigurationCleanupAsync(ProjectSnapshot snapshot, Target target, CancellationToken token)
    {
        Exception? cleanup;
        try
        {
            var transactionRoot = Path.Combine(RuntimeEnvironment.UserDataDirectory(), "transactions", "desktop", snapshot.ProjectId);
            if (!DesktopConfigurationAssetTransaction.HasCompletedCleanupCandidates(transactionRoot)) return snapshot;
            using var guard = DesktopConfigurationCommitGuard.AcquireUnverified(transactionRoot);
            var binding = await ReadConfigurationBindingAsync(target, token);
            if (binding is null) return snapshot;
            var current = await DesktopBridgeClient.VerifyAsync(binding.File, token);
            if (!ConfigurationEntry(current, token).Equals(binding.Entry, StringComparison.OrdinalIgnoreCase) || ProjectFileIdentity.Read(binding.Entry) != binding.FileIdentity)
                throw new AppException("DESKTOP_FILE_CHANGED", "Desktop 配置工程绑定已变化, 未操作其他文件", 3);
            cleanup = DesktopConfigurationAssetTransaction.RetryCompletedCleanup(transactionRoot, binding.Root, guard);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { cleanup = error; }
        if (cleanup is null) return snapshot;
        using var operation = BusinessLog.Begin(log, "DesktopConfigurationAsset.Cleanup");
        operation.Fail(cleanup);
        return snapshot.Warnings.Contains(DesktopConfigurationAssetTransaction.CleanupWarning, StringComparer.Ordinal) ? snapshot
            : snapshot with { Warnings = [.. snapshot.Warnings, DesktopConfigurationAssetTransaction.CleanupWarning] };
    }

    /// <summary>按实际报表绑定解析唯一 PBIP, PBIX 仅规范化官方文件身份, 不读取二进制或模型定义.</summary>
    private static string ConfigurationEntry(DesktopFileTarget file, CancellationToken token) =>
        Path.GetExtension(file.Path).Equals(".pbir", StringComparison.OrdinalIgnoreCase)
            ? DesktopPendingQueries.BoundProject(DesktopModelFileIdentity.Normalize(file.Path), token)
            : DesktopModelFileIdentity.Read(file.Path).EntryPath;
}
