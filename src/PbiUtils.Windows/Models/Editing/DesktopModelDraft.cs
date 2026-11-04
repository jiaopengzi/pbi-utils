// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopModelDraft.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独占原 Desktop TOM 会话并在原对象校验暂存, 失败结束事务与会话, 不创建模型副本.

using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Windows.Models.Editing;

/// <summary>拥有绑定原引擎的专属会话和原 Database, 暂存不向服务器发送 SaveChanges 或 Update.</summary>
/// <param name="baseline">原目标及编辑前完整权威基线.</param>
/// <param name="original">该连接直接读取的原 Database, 不接受网页模型.</param>
/// <param name="connection">由本上下文独占释放的原 TOM 连接.</param>
/// <param name="beginTransaction">首次本地修改前启动本会话事务, TOM 禁止 dirty 后再 Begin.</param>
/// <param name="rollbackTransaction">失败、取消或放弃时结束本次事务, 不还原外部客户端内容.</param>
/// <param name="save">原对象提交入口; Commit 确认后必须调用 transactionCommitted.</param>
public sealed class DesktopModelDraft(ProjectSnapshot baseline, TOM.Database original, IDisposable connection, Action beginTransaction,
    Action rollbackTransaction, Func<IReadOnlyList<ModelEditRequest>, Action?, Action, CancellationToken, Task<ProjectSnapshot>> save)
    : IModelDraft, IUdfLintDraft, ICalendarProgramDraft
{
    private readonly List<ModelEditRequest> edits = [];
    private ProjectSnapshot? snapshot;
    private bool transactionActive;
    private bool disposed;
    private Task? disposal;
    private readonly Lock lifecycle = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private bool connectionDisposed;
    private Exception? cleanupPrimary;
    public ProjectSnapshot Snapshot => snapshot ?? baseline;

    /// <summary>只读分析同一原对象, 不开始写事务或创建另一操作模型.</summary>
    public Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token) => RunOperationAsync(() => Task.Run(() =>
    {
        return UdfModelLint.Analyze(original, baseline, token);
    }, token));

    /// <summary>在同一原对象只读规划日期映射, 不修改对象、模型或文件.</summary>
    public Task<ScaffoldCalendarProgram> PreviewCalendarAsync(ScaffoldCalendarOptions options, string locale, CancellationToken token) => RunOperationAsync(() => Task.Run(() =>
    {
        return CalendarProgramPlanner.Preview(original, options, locale, token);
    }, token));

    /// <summary>首次修改前建立事务, 在原对象校验并暂存一次; 失败和取消丢弃整个专属会话.</summary>
    /// <param name="request">宿主已核验的精确操作或冻结配置.</param>
    /// <param name="token">同步 SDK 边界的取消令牌, 不宣称能中断执行中的 SDK.</param>
    /// <exception cref="AppException">操作或 SDK 验证失败时不提交服务器, 会话不再复用.</exception>
    public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => RunOperationAsync(() => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (edits.Count >= 128) throw new AppException("EDIT_LIMIT", "本轮修改已达上限", 3);
        if (!transactionActive)
        {
            try { beginTransaction(); transactionActive = true; }
            catch (Exception error) { error.Data["DesktopTransactionStartUnconfirmed"] = true; throw; }
        }
        token.ThrowIfCancellationRequested();
        var before = ModelConfigurationReadback.Fingerprint(original);
        ModelMutation.Apply(original, new(baseline.ProjectPath, "original", request.Operation, request.ObjectName, request.Property,
            request.Value, request.Table, Configuration: request.Configuration));
        var after = ModelConfigurationReadback.Fingerprint(original);
        token.ThrowIfCancellationRequested();
        if (request.Operation == "configuration.apply" && before == after
            && request.Configuration!.Assets.All(asset => asset.BeforeHash == asset.AfterHash)) return;
        edits.Add(request);
        snapshot = baseline with
        {
            Model = ModelSnapshotMapper.Map(original, baseline.Model.Name),
            BaselineId = after,
            HasPendingChanges = true,
            SaveResult = baseline.SaveResult?.ErrorCode is null ? null : baseline.SaveResult
        };
    }, CancellationToken.None));

    /// <summary>提交同一原对象; Commit 确认后解除事务所有权, 回读失败也不得回滚已提交修改.</summary>
    public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => RunOperationAsync(() =>
    {
        if (!transactionActive || edits.Count == 0) throw new AppException("NO_PENDING_CHANGES", "没有待提交的修改", 3);
        return save(edits.ToArray(), committing, () => transactionActive = false, token);
    });

    /// <summary>立即停止接受操作, 线程池排空后清理; 失败后仅下一次明确调用重试未确认步骤.</summary>
    public void Dispose()
    {
        lock (lifecycle) { disposed = true; StartCleanup(retry: true); }
    }

    /// <summary>共享当前真实清理任务, 等待在途操作、回滚和会话释放, 不同步阻塞 WinForms.</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) { disposed = true; return new(StartCleanup(retry: true)); }
    }

    /// <summary>串行接受的模型操作, 失败先停止接收再等待同一清理任务, 保留业务首错.</summary>
    /// <param name="action">在本操作独占原会话期间执行的准备或保存, 不重放任何模型修改.</param>
    /// <returns>操作和适用失败清理已经终结的任务; 清理失败附加到原异常.</returns>
    private async Task<T> RunOperationAsync<T>(Func<Task<T>> action)
    {
        Task entered;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entered = operationGate.WaitAsync();
        }
        await entered.ConfigureAwait(false);
        var ownsGate = true;
        var started = false;
        try
        {
            lock (lifecycle) ObjectDisposedException.ThrowIf(disposed, this);
            started = true;
            return await action().ConfigureAwait(false);
        }
        catch (Exception error) when (started)
        {
            Task cleanup;
            lock (lifecycle)
            {
                disposed = true;
                cleanupPrimary ??= error;
                cleanup = StartCleanup(retry: false);
            }
            operationGate.Release(); ownsGate = false;
            // 清理任务独立报告资源错误; 本业务路径必须保留原取消/提交异常和附加事实.
            await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
        finally { if (ownsGate) operationGate.Release(); }
    }

    /// <summary>无返回值操作复用同一会话门闩和失败清理, 不因取消跳过已有事务清理.</summary>
    private Task<bool> RunOperationAsync(Func<Task> action) => RunOperationAsync(async () => { await action().ConfigureAwait(false); return true; });

    /// <summary>由生命周期锁持有者创建唯一清理任务, 活动操作退出后才在线程池调用同步 SDK.</summary>
    /// <param name="retry">只有显式释放允许重试此前失败, 操作失败加入已有清理任务.</param>
    private Task StartCleanup(bool retry)
    {
        if (disposal is null || retry && disposal.IsCompleted && !disposal.IsCompletedSuccessfully)
            disposal = Task.Run(async () =>
            {
                await operationGate.WaitAsync().ConfigureAwait(false);
                try { Abandon(); }
                finally { operationGate.Release(); }
            }, CancellationToken.None);
        return disposal;
    }

    /// <summary>仅在会话门闩内清理未确认步骤; 成功关闭会话也结束其事务, 失败保留后续显式重试.</summary>
    private void Abandon()
    {
        Exception? cleanup = null;
        if (transactionActive)
        {
            try { rollbackTransaction(); transactionActive = false; }
            catch (Exception error) { cleanup = error; cleanupPrimary?.Data["DesktopTransactionRollbackFailed"] = true; }
        }
        if (!connectionDisposed)
        {
            try { connection.Dispose(); connectionDisposed = true; transactionActive = false; }
            catch (Exception error) { cleanup ??= error; cleanupPrimary?.Data["DesktopSessionDisposeFailed"] = true; }
        }
        if (cleanup is not null) throw new AppException("DESKTOP_SESSION_CLEANUP_FAILED", "原模型操作会话未能确认结束, 请重新读取模型核验", 4);
    }
}
