// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopModelDraftLifecycleTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实原对象草稿的清理重试、在途排空与共享编辑恢复测试, 不连接外部模型.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopModelDraftLifecycleTests
{
    /// <summary>已提交后首次连接释放失败, 权威重读重试清理即可恢复, 不再次提交或回滚.</summary>
    [Fact]
    public async Task CommittedSessionCleanupCanRecoverWithoutReplayingSave()
    {
        using var fixture = new Fixture { DisposeFailures = 1 };
        var edits = new ModelEditService(fixture, fixture);
        try
        {
            edits.Select(fixture.Current);
            await edits.StageAsync(new("table.set", "Dates", "description", "committed"), default);
            Assert.Equal("DESKTOP_SESSION_CLEANUP_FAILED", (await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default))).Code);
            Assert.True(edits.HasPendingChanges);

            var recovered = await edits.DiscardAsync(default);

            Assert.Equal("committed", recovered.Model.Tables.Single().Description);
            Assert.False(edits.HasPendingChanges);
            Assert.Equal(1, fixture.Submissions);
            Assert.Equal(0, fixture.Rollbacks);
            Assert.Equal(2, fixture.Disposals);
        }
        finally { await ObserveFailureAsync(edits.DisposeAsync().AsTask()); }
    }

    /// <summary>回滚已经确认而连接首次释放失败, 后续明确释放只重试连接, 不重复回滚.</summary>
    [Fact]
    public async Task CleanupRetriesOnlyUnconfirmedConnectionDisposal()
    {
        using var fixture = new Fixture { DisposeFailures = 1 };
        var draft = fixture.Create();
        await draft.ApplyAsync(new("table.set", "Dates", "description", "prepared"), default);
        Assert.Equal("DESKTOP_SESSION_CLEANUP_FAILED", (await Assert.ThrowsAsync<AppException>(() => draft.DisposeAsync().AsTask())).Code);

        await draft.DisposeAsync();
        await draft.DisposeAsync();

        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(2, fixture.Disposals);
        Assert.Equal(0, fixture.Submissions);
    }

    /// <summary>两个清理步骤均未确认时后续调用只重试这些步骤, 清理期间的业务取消仍保留原异常.</summary>
    [Fact]
    public async Task FailedCancellationCleanupPreservesPrimaryAndCanRetry()
    {
        using var fixture = new Fixture { DisposeFailures = 1, RollbackFailures = 1 };
        var draft = fixture.Create();
        await draft.ApplyAsync(new("table.set", "Dates", "description", "prepared"), default);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draft.ApplyAsync(new("table.set", "Dates", "description", "cancelled"), new(true)));
        Assert.Equal(true, error.Data["DesktopTransactionRollbackFailed"]);
        Assert.Equal(true, error.Data["DesktopSessionDisposeFailed"]);

        await draft.DisposeAsync();

        Assert.Equal(2, fixture.Rollbacks);
        Assert.Equal(2, fixture.Disposals);
        Assert.Equal(0, fixture.Submissions);
    }

    /// <summary>失败路径已在回滚时, 并发释放必须共享实际清理完成, 不能越过仍阻塞的 SDK.</summary>
    [Fact]
    public async Task ConcurrentDisposeWaitsForFailureCleanupAlreadyInProgress()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { OnRollback = () => { entered.TrySetResult(); WaitForRelease(release); } };
        var draft = fixture.Create();
        var apply = draft.ApplyAsync(new("table.set", "Missing", "description", "bad"), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = draft.DisposeAsync().AsTask();
            var second = draft.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromMilliseconds(150)));
            release.Set();
            await first;
            Assert.Equal("OBJECT_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => apply)).Code);
            Assert.Equal(1, fixture.Rollbacks);
            Assert.Equal(1, fixture.Disposals);
        }
        finally { release.Set(); await ObserveFailureAsync(apply); await draft.DisposeAsync(); }
    }

    /// <summary>事务 Begin 已受理但尚未返回时, 释放等它及 Apply 结束后再回滚和关闭连接.</summary>
    [Fact]
    public async Task DisposeDrainsAcceptedApplyBeforeClosingSession()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { OnBegin = () => { entered.TrySetResult(); WaitForRelease(release); } };
        var draft = fixture.Create();
        var apply = draft.ApplyAsync(new("table.set", "Dates", "description", "accepted"), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = draft.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(150)));
            release.Set();
            await apply;
            await disposal;
            Assert.Equal(1, fixture.Rollbacks);
            Assert.Equal(1, fixture.Disposals);
            Assert.Equal(0, fixture.Submissions);
        }
        finally { release.Set(); await ObserveFailureAsync(apply); await draft.DisposeAsync(); }
    }

    /// <summary>保存已经进入提交时, 释放等待提交与权威回读完成, 不同时回滚或重复提交.</summary>
    [Fact]
    public async Task DisposeDrainsAcceptedSaveWithoutRollingBackConfirmedCommit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { BeforeCommit = () => { entered.TrySetResult(); return release.Task; } };
        var draft = fixture.Create();
        await draft.ApplyAsync(new("table.set", "Dates", "description", "committed"), default);
        var save = draft.SaveAsync(null, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = draft.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(150)));
            release.TrySetResult();
            var saved = await save;
            await disposal;
            Assert.Equal("committed", saved.Model.Tables.Single().Description);
            Assert.Equal(0, fixture.Rollbacks);
            Assert.Equal(1, fixture.Disposals);
            Assert.Equal(1, fixture.Submissions);
        }
        finally { release.TrySetResult(); await ObserveFailureAsync(save); await draft.DisposeAsync(); }
    }

    /// <summary>有界等待测试控制的同步 SDK 门闩, 超时明确失败并避免悬挂测试宿主.</summary>
    private static void WaitForRelease(ManualResetEventSlim release)
    {
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("controlled SDK boundary was not released");
    }

    /// <summary>在 finally 中排空预期失败的业务任务, 保留测试正文的首个断言失败.</summary>
    private static async Task ObserveFailureAsync(Task operation) => await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>使用真实 TOM 原对象和共享编辑服务, 仅注入无法在单元测试连接的 SDK/权威读取边界.</summary>
    private sealed class Fixture : IProjectReader, IModelEditWorkspace, IDisposable
    {
        public TOM.Database Database { get; } = TOM.JsonSerializer.DeserializeDatabase("""{"id":"original","name":"original","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Dates","columns":[{"name":"Date","sourceColumn":"Date","dataType":"dateTime"}],"partitions":[{"name":"Dates","mode":"import","source":{"type":"m","expression":"#table({\"Date\"}, {{#date(2026, 1, 1)}})"}}]}]}}""");
        public ProjectSnapshot Current { get; private set; }
        public int DisposeFailures { get; init; }
        public int RollbackFailures { get; init; }
        public int Disposals { get; private set; }
        public int Rollbacks { get; private set; }
        public int Submissions { get; private set; }
        public Action? OnBegin { get; init; }
        public Action? OnRollback { get; init; }
        public Func<Task>? BeforeCommit { get; init; }

        /// <summary>冻结编辑前权威快照, 本地原对象暂存不更新此服务器事实.</summary>
        public Fixture() => Current = new("model://lifecycle", "identity", ModelConfigurationReadback.Fingerprint(Database), "online", "", "", null,
            ModelSnapshotMapper.Map(Database), [], [], [], new("desktop", "fixture"));

        /// <summary>创建唯一原对象会话, 仅 Commit 确认后才发布对应权威读取快照.</summary>
        public DesktopModelDraft Create() => new(Current, Database, new Connection(this), () => OnBegin?.Invoke(), () =>
        {
            Rollbacks++; OnRollback?.Invoke();
            if (Rollbacks <= RollbackFailures) throw new IOException("controlled rollback failure");
        }, async (_, committing, committed, _) =>
        {
            committing?.Invoke(); Submissions++;
            if (BeforeCommit is not null) await BeforeCommit();
            committed();
            Current = Current with { BaselineId = ModelConfigurationReadback.Fingerprint(Database), Model = ModelSnapshotMapper.Map(Database), SaveResult = new(true, false, true, "committed") };
            return Current;
        });

        /// <summary>返回独立服务器事实, 不把尚未提交的原对象映射当成权威读取.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <summary>共享服务通过相同原对象创建实际草稿, 不替换草稿生命周期实现.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(Create());

        /// <summary>只释放本测试独占 TOM 对象.</summary>
        public void Dispose() => Database.Dispose();

        private sealed class Connection(Fixture owner) : IDisposable
        {
            /// <summary>注入边界失败并记录真实草稿选择重试的次数, 不模拟成功提交.</summary>
            public void Dispose()
            {
                owner.Disposals++;
                if (owner.Disposals <= owner.DisposeFailures) throw new IOException("controlled connection disposal failure");
            }
        }
    }
}
