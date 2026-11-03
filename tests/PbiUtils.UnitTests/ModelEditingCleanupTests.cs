// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelEditingCleanupTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 编辑首错与会话清理同时失败时保留首错和资源所有权, 回读恢复不得重放提交.

using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelEditingCleanupTests
{
    /// <summary>各原对象入口取消时清理再失败仍报告原取消, 保留待释放会话, 显式回读后才接受新参数.</summary>
    [Theory]
    [InlineData("stage")]
    [InlineData("direct")]
    [InlineData("configuration")]
    [InlineData("agent")]
    [InlineData("analysis")]
    [InlineData("save")]
    public async Task CleanupFailurePreservesCancellationAndCanRecoverWithoutReplay(string entry)
    {
        var target = new Target { FailureAt = entry == "save" ? "save" : "apply", DisposeFailures = 1 };
        await using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        var request = new ModelEditRequest("table.set", "Sales", "description", "cancelled input");
        var policy = new ToolWritePolicy((_, _) => Task.FromResult(true));
        if (entry == "save") await edits.StageAsync(request, default);
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(), new([], []), [], null);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => entry switch
        {
            "stage" => edits.StageAsync(request, default),
            "configuration" => edits.EditConfigurationAndSaveAsync(new("configuration.apply", "configuration", "config", "digest", Configuration: plan), default),
            "agent" => edits.EditAuthorizedAndSaveAsync(request, "run", "workspace", policy, default),
            "analysis" => edits.AnalyzeUdfsAsync("identity", "baseline", default),
            "save" => edits.SaveAsync(default),
            _ => edits.EditAndSaveAsync(request, default)
        });
        Assert.Same(target.Primary, error);
        Assert.Equal(true, error.Data["ModelDraftCleanupFailed"]);
        Assert.True(edits.HasPendingChanges);
        Assert.Throws<AppException>(() => edits.Select(null));
        await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(request, default));
        Assert.Equal(0, target.Commits);

        target.FailureAt = null;
        await edits.DiscardAsync(default);
        Assert.False(edits.HasPendingChanges);
        Assert.Equal(2, target.Disposals);
        var saved = await edits.EditAndSaveAsync(request with { Value = "new input" }, default);
        Assert.Equal("new input", saved.Model.Tables[0].Description);
        Assert.Equal(1, target.Commits);
    }

    /// <summary>只读分析成功但释放失败也保留会话所有权, 关闭必须再次等待真实释放.</summary>
    [Fact]
    public async Task SuccessfulAnalysisCleanupFailureRemainsOwnedUntilShutdown()
    {
        var target = new Target { DisposeFailures = 1 };
        var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        await Assert.ThrowsAsync<IOException>(() => edits.AnalyzeUdfsAsync("identity", "baseline", default));
        Assert.True(edits.HasPendingChanges);
        await edits.DisposeAsync();
        Assert.Equal(2, target.Disposals);
    }

    /// <summary>关闭失败不丢失草稿所有权, 下一次明确关闭只重试释放, 成功后多次关闭不重复执行.</summary>
    [Fact]
    public async Task ExplicitShutdownRetriesOnlyUnconfirmedCleanup()
    {
        var target = new Target { DisposeFailures = 1 };
        var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        await edits.StageAsync(new("table.set", "Sales", "description", "never committed"), default);
        await Assert.ThrowsAsync<IOException>(() => edits.DisposeAsync().AsTask());
        await edits.DisposeAsync();
        await edits.DisposeAsync();
        Assert.Equal(2, target.Disposals);
        Assert.Equal(0, target.Commits);
    }

    /// <summary>放弃时清理失败不能允许重新保存已停止的会话, 下一次权威回读和清理成功后才解锁.</summary>
    [Fact]
    public async Task FailedDiscardBlocksWriteUntilExplicitRecovery()
    {
        var target = new Target { DisposeFailures = 1 };
        await using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        var request = new ModelEditRequest("table.set", "Sales", "description", "discarded");
        await edits.StageAsync(request, default);
        await Assert.ThrowsAsync<IOException>(() => edits.DiscardAsync(default));
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default))).Code);
        await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(request, default));
        await edits.DiscardAsync(default);
        Assert.False(edits.HasPendingChanges);
        Assert.Equal(0, target.Commits);
    }

    /// <summary>提供可控的应用首错与异步释放失败, 只证明共享用例行为, 不替代真实 TOM.</summary>
    private sealed class Target : IProjectReader, IModelEditWorkspace
    {
        public ProjectSnapshot Current { get; private set; } = new("model://connected", "identity", "baseline", "Desktop", "online", "1", null,
            new("Connected", 1702, "en-US", [new("Sales", "before", false, [], [])], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        public OperationCanceledException Primary { get; } = new("controlled cancellation");
        public string? FailureAt { get; set; }
        public int DisposeFailures { get; init; }
        public int Disposals { get; private set; }
        public int Commits { get; private set; }
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(Current);
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new Draft(this, baseline));

        private sealed class Draft(Target owner, ProjectSnapshot baseline) : IModelDraft, IUdfLintDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = baseline;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            {
                if (owner.FailureAt == "apply") throw owner.Primary;
                Snapshot = Snapshot with { HasPendingChanges = true, Model = Snapshot.Model with { Tables = [Snapshot.Model.Tables[0] with { Description = request.Value }] } };
                return Task.CompletedTask;
            }
            public Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token) => owner.FailureAt == "apply"
                ? throw owner.Primary : Task.FromResult(new UdfLintResult("identity", "baseline", [], 0, true));
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                if (owner.FailureAt == "save") throw owner.Primary;
                committing?.Invoke(); owner.Commits++;
                owner.Current = Snapshot with { HasPendingChanges = false, BaselineId = "saved", SaveResult = new(true, false, true, "saved") };
                return Task.FromResult(owner.Current);
            }
            public void Dispose() => throw new InvalidOperationException("async cleanup required");
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return owner.Disposals <= owner.DisposeFailures ? ValueTask.FromException(new IOException("controlled cleanup failure")) : ValueTask.CompletedTask;
            }
        }
    }
}
