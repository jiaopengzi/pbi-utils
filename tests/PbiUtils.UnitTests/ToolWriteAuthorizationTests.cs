// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ToolWriteAuthorizationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证直接写入中的精确提案授权与共享工作区并发边界.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>应用用例直接调用仍受授权与工作区锁保护.</summary>
public sealed class ToolWriteAuthorizationTests
{
    private static readonly ModelEditRequest Request = new("table.set", "Sales", "description", "after");
    /// <summary>直接模型写入仍须一次精确工具确认, 展示真实前后值后提交一次.</summary>
    [Fact]
    public async Task DirectModelWriteStillRequiresExactAuthorization()
    {
        var reader = new Reader(); var workspace = new Workspace();
        using var service = new ModelEditService(reader, workspace); service.Select(reader.Snapshot);
        var confirmations = 0; var receiptPrepared = false;
        var policy = new ToolWritePolicy(confirmTool: (proposal, token) =>
        {
            confirmations++;
            var change = Assert.Single(proposal.Changes);
            Assert.Equal("before", change.Before); Assert.Equal("after", change.After);
            Assert.Equal("Sales", change.ObjectName); Assert.Equal("description", change.Property);
            return Task.FromResult(true);
        });
        var saved = await service.EditAuthorizedAndSaveAsync(Request, "run", "workspace", policy, default, () => Assert.True(receiptPrepared), _ => { receiptPrepared = true; return Task.CompletedTask; });
        Assert.Equal(1, confirmations); Assert.Equal(1, workspace.Writes); Assert.False(saved.HasPendingChanges);
    }
    /// <summary>没有独立宿主确认、拒绝授权或回执预写失败都不得提交, 也不留下草稿.</summary>
    [Theory]
    [InlineData("missing", "WRITE_AUTHORIZATION_UNAVAILABLE")]
    [InlineData("declined", "CANCELLED")]
    [InlineData("receipt", "RECEIPT_FAILED")]
    [InlineData("stale", "WRITE_AUTHORIZATION_INVALID")]
    public async Task FailureBeforeCommitDoesNotWriteOrLeaveDraft(string failure, string code)
    {
        var reader = new Reader(); var workspace = new Workspace();
        using var service = new ModelEditService(reader, workspace); service.Select(reader.Snapshot);
        var policy = new ToolWritePolicy(confirmTool: failure == "missing" ? null : (_, _) =>
        {
            if (failure == "stale") reader.Snapshot = reader.Snapshot with { BaselineId = "external" };
            return Task.FromResult(failure != "declined");
        });
        var error = await Assert.ThrowsAsync<AppException>(() => service.EditAuthorizedAndSaveAsync(Request, "run", "workspace", policy, default,
            beforeCommit: _ => failure == "receipt" ? throw new AppException("RECEIPT_FAILED", "夹具满盘") : Task.CompletedTask));
        Assert.Equal(code, error.Code); Assert.Equal(0, workspace.Writes); Assert.False(service.HasPendingChanges);
    }
    /// <summary>跨运行、跨工作区、对象属性或值变化、远端与授权复用均拒绝.</summary>
    [Fact]
    public async Task GrantBindsExactScopeAndCanOnlyBeConsumedOnce()
    {
        var snapshot = new Reader().Snapshot;
        var proposal = ToolWriteProposal.Create("run", "workspace", snapshot, snapshot, Request);
        var policy = new ToolWritePolicy(confirmTool: (_, _) => Task.FromResult(true));
        var grant = await policy.AuthorizeAsync(proposal, default);
        Assert.Throws<AppException>(() => grant.Consume("other", "workspace", snapshot, Request));
        Assert.Throws<AppException>(() => grant.Consume("run", "other", snapshot, Request));
        Assert.Throws<AppException>(() => grant.Consume("run", "workspace", snapshot with { BaselineId = "external" }, Request));
        foreach (var changed in new[] { Request with { ObjectName = "Other" }, Request with { Property = "expression" }, Request with { Value = "bad" } })
            Assert.Throws<AppException>(() => grant.Consume("run", "workspace", snapshot, changed));
        Assert.Equal("READ_ONLY_MODEL", Assert.Throws<AppException>(() => grant.Consume("run", "workspace", snapshot with { Connection = new("ssas-xmla", "remote") }, Request)).Code);
        grant.Consume("run", "workspace", snapshot, Request);
        Assert.Equal("WRITE_AUTHORIZATION_USED", Assert.Throws<AppException>(() => grant.Consume("run", "workspace", snapshot, Request)).Code);
    }
    /// <summary>签发后三分钟即失效, 可控时钟覆盖边界而不让测试真实等待.</summary>
    [Fact]
    public async Task ExpiredGrantCannotBeConsumed()
    {
        var clock = new GrantClock(); var snapshot = new Reader().Snapshot;
        var policy = new ToolWritePolicy(confirmTool: (_, _) => Task.FromResult(true), grantTimeProvider: clock);
        var grant = await policy.AuthorizeAsync(ToolWriteProposal.Create("run", "workspace", snapshot, snapshot, Request), default);
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal("WRITE_AUTHORIZATION_INVALID", Assert.Throws<AppException>(() => grant.Consume("run", "workspace", snapshot, Request)).Code);
    }
    private sealed class GrantClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    /// <summary>绕过 bridge 的选择和读取也不能与保存并发, 等待确认时工作区不能换走.</summary>
    [Fact]
    public async Task SharedSessionRejectsReadOrSelectionDuringAuthorization()
    {
        var reader = new Reader(); var workspace = new Workspace();
        using var service = new ModelEditService(reader, workspace); service.Select(reader.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new ToolWritePolicy(confirmTool: (_, _) => { entered.SetResult(); return release.Task; });
        var save = service.EditAuthorizedAndSaveAsync(Request, "run", "workspace", policy, default);
        await entered.Task;
        Assert.Equal("BUSY", Assert.Throws<AppException>(() => service.Select(null)).Code);
        Assert.Equal("BUSY", (await Assert.ThrowsAsync<AppException>(() => service.ReadAsync("fixture.pbip", default))).Code);
        release.SetResult(true); await save;
        service.Select(null); service.Select(null);
    }
    private sealed class Reader : IProjectReader
    {
        public ProjectSnapshot Snapshot { get; set; } = new("fixture.pbip", "workspace", "baseline", "TMDL", "PBIR", "4.2", "4.0",
            new("fixture", 1606, "en-US", [new("Sales", "before", false, [], [])], [], [], 0, 0), [], [], [], new("pbip", "PBIP"));
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(Snapshot);
    }
    /// <summary>提交钩子之后 SDK 失去确定结果, 顶栏保存入口也不能再次提交同一草稿.</summary>
    [Fact]
    public async Task UnknownCommitResultBlocksEverySaveEntryPoint()
    {
        var reader = new Reader(); var workspace = new Workspace(true);
        using var service = new ModelEditService(reader, workspace); service.Select(reader.Snapshot);
        await service.StageAsync(Request, default);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(default));
        Assert.True(service.HasPendingChanges); Assert.Equal(1, workspace.Writes);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => service.SaveAsync(default))).Code);
        Assert.Equal(1, workspace.Writes);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await service.ReadAsync("fixture.pbip", default)).SaveResult?.ErrorCode);
        Assert.Equal("before", (await service.ReadAsync("fixture.pbip", default)).Model.Tables[0].Description);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => service.StageAsync(Request with { Value = "next" }, default))).Code);
    }

    private sealed class Workspace(bool failAfterCommit = false) : IModelEditWorkspace
    {
        private readonly bool failsAfterCommit = failAfterCommit;
        public int Writes { get; private set; }
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new Draft(baseline, this));
        private sealed class Draft(ProjectSnapshot initial, Workspace owner) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = initial;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            { Snapshot = Snapshot with { HasPendingChanges = true, Model = Snapshot.Model with { Tables = [Snapshot.Model.Tables[0] with { Description = request.Value }] } }; return Task.CompletedTask; }
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); committing?.Invoke(); owner.Writes++;
                if (owner.failsAfterCommit) throw new IOException("合成 SDK 断开, 未确定提交结果");
                return Task.FromResult(Snapshot with { HasPendingChanges = false });
            }
            public void Dispose() { }
        }
    }
}
