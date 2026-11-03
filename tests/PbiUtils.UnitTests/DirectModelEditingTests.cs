// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DirectModelEditingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 连接模型即时提交的准备失败清理、手动文件保存与未知提交保护回归.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class DirectModelEditingTests
{
    /// <summary>准备或提交前失败释放私有候选, 不留下要求用户保存或放弃的草稿; 后续可提交新参数.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDirectCommitReleasesCandidateAndAllowsNextEdit(bool configuration)
    {
        var target = new Target { FailBeforeCommit = true };
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        var request = new ModelEditRequest("table.set", "Sales", "description", "first");
        if (configuration)
        {
            var plan = new PbiUtils.Application.Models.Configuration.ModelConfigurationPlan("baseline", "digest", new(), new([], []), [], null);
            request = new("configuration.apply", "configuration", "config", "digest", Configuration: plan);
        }
        await Assert.ThrowsAsync<AppException>(() => configuration
            ? edits.EditConfigurationAndSaveAsync(request, default)
            : edits.EditAndSaveAsync(request, default));
        Assert.False(edits.HasPendingChanges);
        Assert.Equal(1, target.Disposed);
        Assert.Equal("baseline", (await edits.ReadAsync(target.Current.ProjectPath, default)).BaselineId);
        target.FailBeforeCommit = false;
        var saved = await edits.EditAndSaveAsync(new("table.set", "Sales", "description", "second"), default);
        Assert.Equal("second", saved.Model.Tables[0].Description);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>文件未自动落盘不等于模型提交未完成, 连续两次即时编辑均可更新原模型.</summary>
    [Fact]
    public async Task ManualDesktopFileSaveDoesNotBlockFollowingModelCommit()
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        for (var index = 0; index < 2; index++)
        {
            var saved = await edits.EditAndSaveAsync(new("table.set", "Sales", "description", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), default);
            Assert.True(saved.SaveResult!.ModelSaved);
            Assert.False(saved.SaveResult.FileSaved);
            Assert.False(edits.HasPendingChanges);
        }
        Assert.Equal(2, target.Commits);
    }

    /// <summary>提交边界后结果未知仍锁定, 移除保存按钮不能允许重放原模型修改.</summary>
    [Fact]
    public async Task UnknownDirectCommitRetainsVerificationGuard()
    {
        var target = new Target { FailAfterCommit = true };
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(new("table.set", "Sales", "description", "unknown"), default));
        Assert.True(edits.HasPendingChanges);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await edits.ReadAsync(target.Current.ProjectPath, default)).SaveResult?.ErrorCode);
        await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default));
        Assert.Equal(1, target.Commits);
    }

    /// <summary>缺少完整权威回读的旧提交快照即使无错误码也不可叠加新的模型修改.</summary>
    [Fact]
    public async Task IncompleteDesktopReadbackCannotStartAnotherWrite()
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current with { SaveResult = new(true, false, false, "尚未核实引擎完整定义") });
        Assert.True(edits.HasPendingChanges);
        await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(new("table.set", "Sales", "description", "blocked"), default));
        Assert.Equal(0, target.Commits);
    }

    /// <summary>控制真实外部提交的不可观测边界, 不声称替代 Desktop 引擎验收.</summary>
    private sealed class Target : IProjectReader, IModelEditWorkspace
    {
        public ProjectSnapshot Current { get; private set; } = new("model://connected", "identity", "baseline", "Desktop", "online", "1", null,
            new("Connected", 1702, "en-US", [new("Sales", "before", false, [], [])], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        public bool FailBeforeCommit { get; set; }
        public bool FailAfterCommit { get; set; }
        public int Disposed { get; private set; }
        public int Commits { get; private set; }
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(Current);
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new Draft(this, baseline));
        private sealed class Draft(Target owner, ProjectSnapshot baseline) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = baseline;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            {
                Snapshot = Snapshot with { HasPendingChanges = true, Model = Snapshot.Model with { Tables = [Snapshot.Model.Tables[0] with { Description = request.Value }] } };
                return Task.CompletedTask;
            }
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                if (owner.FailBeforeCommit) throw new AppException("STALE_BASELINE", "提交前外部修改", 3);
                committing?.Invoke(); owner.Commits++;
                if (owner.FailAfterCommit) throw new AppException("TRANSPORT_FAILED", "合成响应丢失", 4);
                owner.Current = Snapshot with
                {
                    HasPendingChanges = false,
                    BaselineId = "saved-" + owner.Commits,
                    SaveResult = new(true, false, true, "模型已更新, 请在 Desktop 保存文件")
                };
                return Task.FromResult(owner.Current);
            }
            public void Dispose() => owner.Disposed++;
        }
    }
}
