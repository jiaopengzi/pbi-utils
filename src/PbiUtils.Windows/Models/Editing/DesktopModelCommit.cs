// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopModelCommit.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原连接对象暂存后的独立权威复核、一次提交及回读, 不复制模型.

using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Windows.Models.Editing;

/// <summary>独立只读连接取得的原模型身份、完整基线, 不作为操作模型.</summary>
public sealed record DesktopModelAuthority(string ProjectId, string BaselineId);

/// <summary>在原连接对象本地暂存后, 使用独立连接核验服务器, 不刷新或替换 dirty 对象.</summary>
/// <param name="original">专属 TOM 会话直接读取的原 Database, 具体操作已在此执行一次.</param>
/// <param name="verifyIdentity">重新发现原 Desktop 并核实进程启动身份.</param>
/// <param name="readAuthority">以独立只读连接读取服务器, 不读取写会话本地缓存.</param>
/// <param name="acquireCommitLock">原写会话在既有事务中取得数据库 CommitShared 锁, 持有到 Commit/Rollback, 失败不得降级.</param>
/// <param name="submit">在原会话执行一次 SaveChanges/Update 和事务 Commit.</param>
/// <param name="readback">提交后以独立连接读取真实完整模型.</param>
public sealed class DesktopModelCommit(TOM.Database original, Func<CancellationToken, Task> verifyIdentity,
    Func<CancellationToken, Task<DesktopModelAuthority>> readAuthority, Action acquireCommitLock, Action submit,
    Func<CancellationToken, Task<ProjectSnapshot>> readback)
{
    /// <summary>模型提交及完整回读事实, 不表示文件已保存.</summary>
    public const string SavedMessage = ModelSaveResult.ConnectedDesktopMessage;

    /// <summary>在原事务持有提交锁并完成最终独立复核, 再提交原对象一次; 提交后排空完整权威回读.</summary>
    /// <param name="baseline">原目标及编辑前完整定义, 不能以 dirty 对象替代.</param>
    /// <param name="committing">所有前置成功后进入不可重放边界的宿主回调.</param>
    /// <param name="token">提交前取消令牌, 同步 SDK 调用仅在边界观察取消.</param>
    /// <param name="allowCompatibilityUpgrade">仅本次独立批准的低级别至 1702 的 UDF 升级.</param>
    /// <returns>原引擎提交并完整回读后的结果, 文件持久化由用户负责.</returns>
    /// <exception cref="AppException">身份/基线变化或越界升级拒绝; 提交后不一致保持待核验.</exception>
    public async Task<ProjectSnapshot> SaveAsync(ProjectSnapshot baseline, Action? committing, CancellationToken token,
        bool allowCompatibilityUpgrade = false)
    {
        token.ThrowIfCancellationRequested();
        await verifyIdentity(token);
        if (original.CompatibilityLevel != baseline.Model.CompatibilityLevel
            && !(allowCompatibilityUpgrade && baseline.Model.CompatibilityLevel < 1702 && original.CompatibilityLevel == 1702))
            throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "本次兼容级别变更未获独立批准或超出 UDF 升级范围, 未修改模型", 3);
        var expected = ModelConfigurationReadback.Fingerprint(original);
        var authority = await readAuthority(token);
        AssertAuthority(authority);
        token.ThrowIfCancellationRequested();
        acquireCommitLock();
        token.ThrowIfCancellationRequested();
        // dirty 原会话不能 Refresh, 否则丢失已校验参数和 lineage. 再次核验必须来自独立连接.
        AssertAuthority(await readAuthority(token));
        await verifyIdentity(token);
        if (ModelConfigurationReadback.Fingerprint(original) != expected)
            throw new AppException("MODEL_DEFINITION_MISMATCH", "本轮原对象暂存定义在提交前已变化, 未提交", 4);
        await verifyIdentity(token);
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        submit();
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var current = await readback(completion.Token);
        if (current.ProjectPath != baseline.ProjectPath || current.ProjectId != baseline.ProjectId
            || current.Connection?.Kind != "desktop" || current.BaselineId != expected)
            throw new AppException("EXECUTION_NEEDS_VERIFICATION", "模型提交后完整定义或目标身份未能核实, 请重新读取核验; 不得重复提交", 4);
        return current with { HasPendingChanges = false, SaveResult = new(true, false, true, SavedMessage) };

        // 完整指纹包含分区、命名表达式、注解和 lineage, 相同展示形状不能通过并发复核.
        void AssertAuthority(DesktopModelAuthority value)
        {
            if (value.ProjectId != baseline.ProjectId || value.BaselineId != baseline.BaselineId)
                throw new AppException("STALE_BASELINE", "Desktop 模型在编辑期间发生变化, 未覆盖; 请重新读取", 3);
        }
    }
}
