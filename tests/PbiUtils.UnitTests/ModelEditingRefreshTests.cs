// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelEditingRefreshTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Desktop 操作前核对客户端已观测完整基线, 显式重读后才接纳外部更改.

using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只替换真实 Desktop 读取/提交边界, 实际运行共享编辑与 工具 精确授权用例.</summary>
public sealed class ModelEditingRefreshTests
{
    private static readonly ModelEditRequest Edit = new("table.set", "Sales", "description", "edited");

    /// <summary>文件保存和 Bridge 恢复仅更新观测信息, 不得让同一引擎与基线永远失效.</summary>
    [Theory]
    [InlineData("unsaved", "inspect")]
    [InlineData("unavailable", "inspect")]
    [InlineData("unsaved", "read")]
    [InlineData("unavailable", "read")]
    [InlineData("unsaved", "edit")]
    [InlineData("unavailable", "edit")]
    public async Task FileObservationChangesDoNotInvalidateStableModelIdentity(string originalStatus, string operation)
    {
        var target = new Target();
        target.Current = target.Current with
        {
            Connection = target.Current.Connection! with { FileIdentityStatus = originalStatus, FileIdentityErrorCode = "DESKTOP_FILE_REQUIRED" }
        };
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        target.Current = target.Current with
        {
            Connection = target.Current.Connection! with { FilePath = @"C:\verified.pbix", FileIdentityStatus = "verified", FileIdentityErrorCode = null }
        };
        var result = operation switch
        {
            "inspect" => await edits.ReadAsync("model://connected", default),
            "read" => await edits.ExecuteReadAsync("identity", "original", (current, _) => Task.FromResult(current), default),
            _ => await edits.EditAndSaveAsync(Edit, default)
        };
        Assert.Equal("verified", result.Connection!.FileIdentityStatus);
        Assert.Equal(@"C:\verified.pbix", result.Connection.FilePath);
        Assert.Equal(operation == "edit" ? 1 : 0, target.Commits);
    }

    /// <summary>各共享入口拒绝基于过期观测准备修改; 显式重读后使用新定义并正确授权.</summary>
    [Theory]
    [InlineData("edit")]
    [InlineData("stage")]
    [InlineData("agent")]
    [InlineData("configuration")]
    [InlineData("analyze")]
    public async Task DesktopOperationsRequireExplicitObservationOfExternalDefinition(string operation)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        target.Change();
        var confirmations = 0;
        var policy = new ToolWritePolicy((proposal, _) =>
        {
            Assert.Equal("external", proposal.BaselineId);
            Assert.Equal("external description", Assert.Single(proposal.Changes).Before);
            confirmations++;
            return Task.FromResult(true);
        });
        async Task AttemptAsync()
        {
            if (operation == "analyze") _ = await edits.AnalyzeUdfsAsync("identity", "external", default);
            else _ = operation switch
            {
                "edit" => await edits.EditAndSaveAsync(Edit, default),
                "stage" => await edits.StageAsync(Edit, default),
                "agent" => await edits.EditAuthorizedAndSaveAsync(Edit, "run", "identity", policy, default),
                _ => await edits.EditConfigurationAndSaveAsync(Configuration("external"), default)
            };
        }
        var conflict = await Assert.ThrowsAsync<AppException>(AttemptAsync);
        Assert.Equal("STALE_BASELINE", conflict.Code);
        Assert.Equal(0, target.Drafts);
        Assert.Equal(0, target.Commits);
        Assert.Equal(0, confirmations);
        var observed = await edits.ReadAsync("model://connected", default);
        Assert.Equal("external", observed.BaselineId);
        if (operation == "analyze")
        {
            var result = await edits.AnalyzeUdfsAsync("identity", "external", default);
            Assert.True(result.Complete);
        }
        else
        {
            var result = operation switch
            {
                "edit" => await edits.EditAndSaveAsync(Edit, default),
                "stage" => await edits.StageAsync(Edit, default),
                "agent" => await edits.EditAuthorizedAndSaveAsync(Edit, "run", "identity", policy, default),
                _ => await edits.EditConfigurationAndSaveAsync(Configuration("external"), default)
            };
            Assert.Equal("edited", result.Model.Tables[0].Description);
        }
        Assert.Equal(operation == "agent" ? 1 : 0, confirmations);
        Assert.Equal("external", target.LastDraftBaseline);
        Assert.Equal(operation is "analyze" or "stage" ? 0 : 1, target.Commits);
    }

    /// <summary>刷新不重写冻结配置与 UDF 分析请求的旧基线, 旧选择不能复用于新定义.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRequestsStillRejectOldBaseline(bool configuration)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        target.Change();
        var error = await Assert.ThrowsAsync<AppException>(() => configuration
            ? edits.EditConfigurationAndSaveAsync(Configuration("original"), default)
            : edits.AnalyzeUdfsAsync("identity", "original", default));
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal(0, target.Drafts);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>实时读取与操作准备均拒绝换实例、换模型、换来源或异常待处理快照.</summary>
    [Theory]
    [InlineData("identity", false)]
    [InlineData("path", false)]
    [InlineData("instance", false)]
    [InlineData("remote", false)]
    [InlineData("pending", false)]
    [InlineData("incomplete", false)]
    [InlineData("identity", true)]
    [InlineData("path", true)]
    [InlineData("instance", true)]
    [InlineData("remote", true)]
    [InlineData("pending", true)]
    [InlineData("incomplete", true)]
    public async Task RefreshCannotAdoptDifferentTargetOrPendingState(string change, bool read)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        target.Current = change switch
        {
            "identity" => target.Current with { ProjectId = "other" },
            "path" => target.Current with { ProjectPath = "model://other" },
            "instance" => target.Current with { Connection = new("desktop", "Desktop", InstanceId: "other-instance") },
            "remote" => target.Current with { Connection = new("ssas-xmla", "Remote") },
            "pending" => target.Current with { HasPendingChanges = true },
            _ => target.Current with { SaveResult = new(true, false, false, "incomplete") }
        };
        var error = await Assert.ThrowsAsync<AppException>(() => read
            ? edits.ReadAsync("model://connected", default)
            : edits.EditAndSaveAsync(Edit, default));
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal(0, target.Drafts);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>读取失败或已取消的迟到结果不得建立候选, 后续可以重新读取并执行.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledRefreshDoesNotStartAnEdit(bool cancel, bool read)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        using var cancellation = new CancellationTokenSource();
        target.BeforeRead = () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("synthetic read failure");
        };
        // 以同一捕获状态分别执行读取或编辑, 两条路径都必须传播刷新失败与取消.
        Task RunOperationAsync() => read
            ? edits.ReadAsync("model://connected", cancellation.Token)
            : edits.EditAndSaveAsync(Edit, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(RunOperationAsync);
        else await Assert.ThrowsAsync<IOException>(RunOperationAsync);
        Assert.Equal(0, target.Drafts);
        Assert.False(edits.HasPendingChanges);
        target.BeforeRead = null;
        await edits.EditAndSaveAsync(Edit, default);
        Assert.Equal(1, target.Commits);
    }

    /// <summary>准备完成或 工具 确认期间再次外部修改仍拒绝提交, 不自动重试旧请求.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeAfterPreparationIsNeverRebased(bool agent)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        var policy = new ToolWritePolicy((_, _) => { target.Change(); return Task.FromResult(true); });
        if (!agent) target.BeforeSave = target.Change;
        var error = await Assert.ThrowsAsync<AppException>(() => agent
            ? edits.EditAuthorizedAndSaveAsync(Edit, "run", "identity", policy, default)
            : edits.EditAndSaveAsync(Edit, default));
        Assert.Equal(agent ? "WRITE_AUTHORIZATION_INVALID" : "STALE_BASELINE", error.Code);
        Assert.Equal(0, target.Commits);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>已有草稿和未知提交不被自动刷新替换, 未知结果保持阻止新写入.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingStateSurvivesExternalChanges(bool unknown)
    {
        var target = new Target();
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        await edits.StageAsync(Edit, default);
        if (unknown)
        {
            target.FailAfterCommit = true;
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(default));
        }
        target.Change();
        var observed = await edits.ReadAsync("model://connected", default);
        Assert.True(observed.HasPendingChanges);
        if (unknown) Assert.Equal("EXECUTION_NEEDS_VERIFICATION", observed.SaveResult!.ErrorCode);
        else Assert.Equal("edited", observed.Model.Tables[0].Description);
        await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(Edit, default));
        Assert.True(edits.HasPendingChanges);
        Assert.Equal(unknown ? 1 : 0, target.Commits);
    }

    /// <summary>离线 PBIP 保留原基线拒绝外部改动, 远端保持只读且不执行自动准备读取.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshDoesNotChangeOfflineOrReadOnlyPolicy(bool remote)
    {
        var target = new Target();
        target.Current = target.Current with { ProjectPath = "fixture.pbip", Connection = new(remote ? "ssas-xmla" : "pbip", "Fixture") };
        using var edits = new ModelEditService(target, target);
        edits.Select(target.Current);
        target.Change();
        var error = await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(Edit, default));
        Assert.Equal(remote ? "READ_ONLY_MODEL" : "STALE_BASELINE", error.Code);
        Assert.Equal(0, target.Reads);
        Assert.Equal(0, target.Commits);
    }

    /// <summary>创建仅绑定当前冻结基线的合成配置, 不执行真实用户模型生成.</summary>
    private static ModelEditRequest Configuration(string baseline)
    {
        var plan = new ModelConfigurationPlan(baseline, "digest", new(), new([], []), [], null);
        return new("configuration.apply", "configuration", "config", "digest", Configuration: plan);
    }

    /// <summary>严格复核基线的 Desktop 边界替身, 不访问本机模型或文件.</summary>
    private sealed class Target : IProjectReader, IModelEditWorkspace
    {
        public ProjectSnapshot Current { get; set; } = new("model://connected", "identity", "original", "Desktop", "online", "1", null,
            new("Connected", 1702, "en-US", [new("Sales", "original description", false, [], [])], [], [], 0, 0), [], [], [], new("desktop", "Desktop", InstanceId: "instance"));
        public Action? BeforeRead { get; set; }
        public Action? BeforeSave { get; set; }
        public string? LastDraftBaseline { get; private set; }
        public bool FailAfterCommit { get; set; }
        public int Reads { get; private set; }
        public int Drafts { get; private set; }
        public int Commits { get; private set; }

        /// <summary>模拟 Desktop 外部刷新后的完整定义变化.</summary>
        public void Change() => Current = Current with
        {
            BaselineId = "external",
            Model = Current.Model with { Tables = [Current.Model.Tables[0] with { Description = "external description" }] }
        };

        /// <summary>允许模拟不配合取消的同步 SDK 返回, 共享服务须自行检查结果是否仍有效.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
        {
            Reads++;
            BeforeRead?.Invoke();
            return Task.FromResult(Current);
        }

        /// <summary>真实适配器在准备原对象前复核基线; 此处保留相同行为.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            Verify(baseline);
            Drafts++;
            LastDraftBaseline = baseline.BaselineId;
            return Task.FromResult<IModelDraft>(new Draft(this, baseline));
        }

        /// <summary>外部定义变化后拒绝使用旧候选, 不替服务自动切换目标.</summary>
        private void Verify(ProjectSnapshot baseline)
        {
            if (baseline.ProjectId != Current.ProjectId || baseline.ProjectPath != Current.ProjectPath || baseline.BaselineId != Current.BaselineId)
                throw new AppException("STALE_BASELINE", "synthetic external change", 3);
        }

        private sealed class Draft(Target owner, ProjectSnapshot baseline) : IModelDraft, IUdfLintDraft
        {
            private readonly ProjectSnapshot initial = baseline;
            public ProjectSnapshot Snapshot { get; private set; } = baseline;

            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            {
                Snapshot = Snapshot with { HasPendingChanges = true, Model = Snapshot.Model with { Tables = [Snapshot.Model.Tables[0] with { Description = "edited" }] } };
                return Task.CompletedTask;
            }

            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                owner.BeforeSave?.Invoke();
                owner.Verify(initial);
                committing?.Invoke();
                owner.Commits++;
                if (owner.FailAfterCommit) throw new IOException("synthetic unknown commit");
                owner.Current = Snapshot with { HasPendingChanges = false, BaselineId = "saved", SaveResult = new(true, false, true, "manual file save") };
                return Task.FromResult(owner.Current);
            }

            public Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token) => Task.FromResult(new UdfLintResult(initial.ProjectId, initial.BaselineId, [], 0, true));
            public void Dispose() { }
        }
    }
}
