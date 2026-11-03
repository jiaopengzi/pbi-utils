// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelDraftSaveReceiptTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证新内存草稿与历史保存回执分离, 失败和待核验事实不得丢失.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelDraftSaveReceiptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-draft-receipt-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实 PBIP 第二轮保存因外部修改拒绝时, 保留新草稿但不能显示上一轮成功回执.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSecondSaveDoesNotInheritCompletedReceipt(bool tmdl)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var modelFile = Path.Combine(root, "project", "model", "model.bim");
        if (tmdl)
        {
            using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile));
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, Path.Combine(root, "project", "model", "definition"));
            File.Delete(modelFile);
            File.WriteAllText(Path.Combine(root, "project", "model", "definition.pbism"), """{"version":"4.2"}""");
        }
        var reader = new ProjectReader();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, new ReceiptWorkspace(workspace));
        edits.Select(await reader.ReadAsync(entry, default));
        await edits.StageAsync(new("table.set", "销售", "description", "first saved"), default);
        var first = await edits.SaveAsync(default);
        Assert.Equal("first saved", (await reader.ReadAsync(entry, default)).Model.Tables.Single().Description);
        Assert.NotNull(first.SaveResult);
        Assert.True(first.SaveResult.ModelSaved);
        Assert.True(first.SaveResult.FileSaved);
        Assert.Null(first.SaveResult.ErrorCode);

        await edits.StageAsync(new("table.set", "销售", "description", "second draft"), default);
        using (var external = new ChangeWorkspace(Path.Combine(root, "external-user")))
        using (var other = new ModelEditService(reader, external))
        {
            other.Select(await reader.ReadAsync(entry, default));
            await other.StageAsync(new("table.set", "销售", "description", "external saved"), default);
            await other.SaveAsync(default);
        }
        var commits = 0;
        var error = await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default, () => commits++));
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal(0, commits);
        Assert.True(edits.HasPendingChanges);
        var draft = await edits.ReadAsync(entry, default);
        Assert.True(draft.HasPendingChanges);
        Assert.Equal("second draft", draft.Model.Tables.Single().Description);
        Assert.Null(draft.SaveResult);
        Assert.Equal("external saved", (await reader.ReadAsync(entry, default)).Model.Tables.Single().Description);
    }

    /// <summary>PBIP/Desktop 共用的新有效编辑清成功回执, 保留部分保存与未知提交的错误事实.</summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "SAVE_PENDING")]
    [InlineData(true, "SAVE_PENDING")]
    [InlineData(false, "EXECUTION_NEEDS_VERIFICATION")]
    [InlineData(true, "EXECUTION_NEEDS_VERIFICATION")]
    public async Task NewDraftClearsOnlyCompletedReceipt(bool desktop, string? errorCode)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var baseline = await new ProjectReader().ReadAsync(entry, default);
        var receipt = new ModelSaveResult(true, errorCode is null, errorCode is null, "previous result", errorCode);
        baseline = baseline with { SaveResult = receipt };
        if (desktop) baseline = baseline with { ProjectPath = "model://desktop-fixture", Connection = new("desktop", "fixture") };
        await using var draft = CreateDraft(baseline);
        await draft.ApplyAsync(new("table.set", "销售", "description", "new draft"), default);
        Assert.True(draft.Snapshot.HasPendingChanges);
        Assert.Equal("new draft", draft.Snapshot.Model.Tables.Single().Description);
        if (errorCode is null) Assert.Null(draft.Snapshot.SaveResult);
        else Assert.Same(receipt, draft.Snapshot.SaveResult);
    }

    /// <summary>校验失败、提交前取消与无变化配置不创建新草稿, 原成功回执和基线均保持.</summary>
    [Fact]
    public async Task FailedCancelledAndUnchangedEditsRetainCompletedReceipt()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var baseline = (await new ProjectReader().ReadAsync(entry, default)) with { SaveResult = new(true, true, true, "previous complete") };
        await using var draft = CreateDraft(baseline);
        Assert.Equal("OBJECT_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => draft.ApplyAsync(new("table.set", "missing", "description", "bad"), default))).Code);
        Assert.Same(baseline, draft.Snapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draft.ApplyAsync(new("table.set", "销售", "description", "cancelled"), cancellation.Token));
        Assert.Same(baseline, draft.Snapshot);
        using var plan = new ModelConfigurationPlan(baseline.BaselineId, "empty", new(), new([], []), [], null);
        await draft.ApplyAsync(new("configuration.apply", "configuration", "config", "empty", Configuration: plan), default);
        Assert.Same(baseline, draft.Snapshot);
        Assert.False(draft.Snapshot.HasPendingChanges);
    }

    /// <summary>已提交但保存或核验待完成时拒绝新暂存, 不创建草稿也不替换原回执.</summary>
    [Theory]
    [InlineData("SAVE_PENDING")]
    [InlineData("EXECUTION_NEEDS_VERIFICATION")]
    public async Task CommittedPendingResultRejectsNewStage(string errorCode)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var reader = new ProjectReader();
        using var files = new ChangeWorkspace(Path.Combine(root, "user"));
        var receipt = new ModelSaveResult(true, true, false, "desktop completion pending", errorCode);
        var workspace = new ReceiptWorkspace(files, receipt);
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(entry, default));
        await edits.StageAsync(new("table.set", "销售", "description", "committed"), default);
        var saved = await edits.SaveAsync(default);
        Assert.Equal("committed", (await reader.ReadAsync(entry, default)).Model.Tables.Single().Description);
        Assert.Same(receipt, saved.SaveResult);
        Assert.True(edits.HasPendingChanges);
        Assert.Equal(1, workspace.DraftsCreated);

        var error = await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(new("table.set", "销售", "description", "must not stage"), default));
        Assert.Equal("UNSAVED_CHANGES", error.Code);
        Assert.Equal(1, workspace.DraftsCreated);
        var observed = await edits.ReadAsync(entry, default);
        Assert.Same(receipt, observed.SaveResult);
        Assert.Equal("committed", observed.Model.Tables.Single().Description);
        Assert.False(observed.HasPendingChanges);
    }

    /// <summary>正常草稿继续接受多步修改, 只清上一轮成功回执, 下一轮保存后文件包含全部新修改.</summary>
    [Fact]
    public async Task CompletedSaveAllowsMultipleStagesInNextDraft()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var reader = new ProjectReader();
        using var files = new ChangeWorkspace(Path.Combine(root, "user"));
        var workspace = new ReceiptWorkspace(files);
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(entry, default));
        await edits.StageAsync(new("table.set", "销售", "description", "first"), default);
        await edits.SaveAsync(default);
        await edits.StageAsync(new("table.set", "销售", "description", "next table"), default);
        var staged = await edits.StageAsync(new("measure.set", "销售额", "description", "next measure", "销售"), default);
        Assert.Null(staged.SaveResult);
        Assert.True(staged.HasPendingChanges);
        Assert.Equal(2, workspace.DraftsCreated);
        await edits.SaveAsync(default);
        var saved = await reader.ReadAsync(entry, default);
        Assert.Equal("next table", saved.Model.Tables.Single().Description);
        Assert.Equal("next measure", saved.Model.Measures.Single().Description);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>Desktop 在同一原对象暂存并提交, 保留原 lineage 和非展示元数据, 不转移完整候选.</summary>
    [Fact]
    public async Task OriginalDesktopSaveUsesSameObjectWithExistingLineage()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var baseline = await new ProjectReader().ReadAsync(entry, default);
        var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "model", "model.bim")));
        database.Model.Tables["销售"].LineageTag = "6042f8a9-9d14-40f4-a458-fbe7e3527af1";
        database.Model.Tables["销售"].Measures["销售额"].LineageTag = "e3605f0d-3fd6-46d1-ae72-a91b2096406c";
        database.Model.Annotations.Add(new TOM.Annotation { Name = "Preserve.FullDefinition", Value = "not in ModelInfo" });
        baseline = baseline with
        {
            ProjectPath = "model://receipt-original",
            Connection = new("desktop", "fixture"),
            Model = ModelSnapshotMapper.Map(database),
            BaselineId = ModelConfigurationReadback.Fingerprint(database)
        };
        var table = database.Model.Tables["销售"];
        var measure = table.Measures["销售额"];
        var saves = 0;
        var transactions = 0;
        var rollbacks = 0;
        await using var draft = new DesktopModelDraft(baseline, database, database, () => transactions++, () => rollbacks++,
            (requests, committing, committed, token) =>
            {
                token.ThrowIfCancellationRequested();
                saves++;
                Assert.Same(table, database.Model.Tables["销售"]);
                Assert.Same(measure, table.Measures["销售额"]);
                Assert.Equal("next draft", table.Description);
                Assert.Equal("6042f8a9-9d14-40f4-a458-fbe7e3527af1", table.LineageTag);
                Assert.Equal("e3605f0d-3fd6-46d1-ae72-a91b2096406c", measure.LineageTag);
                Assert.Equal("not in ModelInfo", database.Model.Annotations["Preserve.FullDefinition"].Value);
                Assert.Single(requests);
                Assert.Equal("next draft", requests[0].Value);
                committing?.Invoke();
                committed();
                return Task.FromResult(baseline with { HasPendingChanges = false });
            });
        await draft.ApplyAsync(new("table.set", "销售", "description", "next draft"), default);
        Assert.Equal("next draft", table.Description);
        Assert.Same(table, database.Model.Tables["销售"]);
        await draft.SaveAsync(null, default);
        Assert.Equal(1, saves);
        Assert.Equal(1, transactions);
        Assert.Equal(0, rollbacks);
        Assert.Equal("next draft", draft.Snapshot.Model.Tables.Single().Description);
    }

    /// <summary>从固定文件分别建立真实离线草稿或 Desktop 原对象上下文, 此回执边界测试不允许提交.</summary>
    private IModelDraft CreateDraft(ProjectSnapshot baseline)
    {
        var original = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "model", "model.bim")));
        return baseline.Connection?.Kind == "desktop"
            ? new DesktopModelDraft(baseline, original, original, () => { }, () => { },
                (_, _, _, _) => throw new InvalidOperationException("此测试不得提交"))
            : new MemoryModelDraft(baseline, original, (_, _, _) => throw new InvalidOperationException("此测试不得保存"));
    }

    /// <summary>保留真实 PBIP 写入, 只替代外部同步回执边界; 不将模拟的 Desktop 同步事实作为 Windows 验收.</summary>
    private sealed class ReceiptWorkspace(IModelEditWorkspace workspace, ModelSaveResult? receipt = null) : IModelEditWorkspace
    {
        public int DraftsCreated { get; private set; }
        /// <summary>保留真实 PBIP 草稿和保存副作用, 只在完成返回处提供保存回执.</summary>
        public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            var draft = await workspace.CreateDraftAsync(baseline, token);
            DraftsCreated++;
            return new ReceiptDraft(draft, receipt ?? new(true, true, false, "file saved"));
        }

        private sealed class ReceiptDraft(IModelDraft draft, ModelSaveResult receipt) : IModelDraft
        {
            public ProjectSnapshot Snapshot => draft.Snapshot;
            /// <summary>使用真实共享草稿原子校验修改, 不改变失败或取消行为.</summary>
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => draft.ApplyAsync(request, token);
            /// <summary>等待真实 PBIP 持久化后提供目标回执夹具; 默认离线完成不声称 Desktop 同步.</summary>
            public async Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => (await draft.SaveAsync(committing, token)) with { SaveResult = receipt };
            /// <summary>释放实际草稿, 不执行外部写入.</summary>
            public void Dispose() => draft.Dispose();
        }
    }

    /// <summary>只清理本测试唯一拥有的临时目录, 不触碰用户工程.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
