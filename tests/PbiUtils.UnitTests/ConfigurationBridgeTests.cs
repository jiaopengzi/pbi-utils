// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ConfigurationBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证配置目标上下文、严格参数与文档撤销, 不访问真实用户模型.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>实际目标分发器拒绝网页路径、旧身份和跨目标请求.</summary>
public sealed class ConfigurationBridgeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>同一当前目标刷新保持身份, 来源变化会撤销旧身份.</summary>
    [Fact]
    public async Task CurrentIdentityStableButDifferentTargetRevokesIt()
    {
        using var workspace = new ChangeWorkspace();
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(), workspace, compiler);
        var original = Snapshot("model://one", "one");
        var first = await bridge.ContextAsync("current", original, default);
        var refreshed = await bridge.ContextAsync("current", original with { BaselineId = "new" }, default);
        Assert.Equal(first.TargetId, refreshed.TargetId);
        Assert.False(first.CanGenerateSample);
        var second = await bridge.ContextAsync("current", Snapshot("model://two", "two"), default);
        Assert.NotEqual(first.TargetId, second.TargetId);
        var parameters = JsonSerializer.SerializeToElement(new { targetId = first.TargetId, baselineId = "new", section = "measures", request = new { } }, Json);
        var error = await Assert.ThrowsAsync<AppException>(() => bridge.ApplyAsync(parameters, original, null, default));
        Assert.Equal("STALE_CONFIGURATION_TARGET", error.Code);
    }

    /// <summary>只读模型可读目录信息, 但后端直接请求也不能修改.</summary>
    [Fact]
    public async Task RemoteContextCannotGrantWrite()
    {
        using var workspace = new ChangeWorkspace();
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(), workspace, compiler);
        var remote = Snapshot("model://cloud", "cloud") with { Connection = new("powerbi-service", "cloud") };
        var context = await bridge.ContextAsync("current", remote, default);
        Assert.False(context.CanWrite);
        var parameters = JsonSerializer.SerializeToElement(new { targetId = context.TargetId, baselineId = "base", section = "measures", request = new { } }, Json);
        var error = await Assert.ThrowsAsync<AppException>(() => bridge.ApplyAsync(parameters, remote, null, default));
        Assert.Equal("READ_ONLY_MODEL", error.Code);
    }

    /// <summary>额外路径、错误段和重复字段不能取得目标权限.</summary>
    [Theory]
    [InlineData("{\"targetId\":\"x\",\"baselineId\":\"b\",\"section\":\"measures\",\"request\":{},\"path\":\"C:/injected\"}")]
    [InlineData("{\"targetId\":\"x\",\"baselineId\":\"b\",\"section\":\"unknown\",\"request\":{}}")]
    [InlineData("{\"targetId\":\"x\",\"targetId\":\"y\",\"baselineId\":\"b\",\"section\":\"measures\",\"request\":{}}")]
    public async Task RejectsForgedFields(string input)
    {
        using var workspace = new ChangeWorkspace();
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(), workspace, compiler);
        using var document = JsonDocument.Parse(input);
        var error = await Assert.ThrowsAsync<AppException>(() => bridge.ApplyAsync(document.RootElement, null, null, default));
        Assert.Equal("VALIDATION_ERROR", error.Code);
    }

    /// <summary>未创建不取得目录权限; 结果中的未核验项目也不能绑定.</summary>
    [Fact]
    public async Task CreatedTargetRequiresVerifiedHostReceipt()
    {
        using var workspace = new ChangeWorkspace();
        using var compiler = new ModelConfigurationCompiler();
        var reader = new ProjectReader();
        using var bridge = new ModelConfigurationBridge(reader, workspace, compiler);
        await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", null, default));
        bridge.BindCreated(new("plan", "verification_pending", true, false, false, "C:/project.pbip", null, [], []));
        await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", null, default));
        var root = Path.Combine(Path.GetTempPath(), "pbi-config-receipt-" + Guid.NewGuid().ToString("N"));
        var file = PbipFixture.Create(root);
        try
        {
            var snapshot = await reader.ReadAsync(file, default);
            var content = File.ReadAllText(file);
            bridge.BindCreated(new("plan", "published", true, true, false, file, snapshot.BaselineId, [], []));
            var context = await bridge.ContextAsync("created", null, default);
            Assert.True(context.IsCreatedTarget);
            bridge.Invalidate();
            await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", null, default));
            File.Delete(file);
            bridge.BindCreated(new("plan", "published", true, true, false, file, snapshot.BaselineId, [], []));
            File.WriteAllText(file, content);
            await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", null, default));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>实际 PBIP 发生提交前保存故障后, 继续保存复用原草稿, 不重复生成或应用配置.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedPendingSaveCanCompleteWithoutApplyingAgain(bool sharedCurrent)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-configuration-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", null, null, null, null, null), project, project, null, default);
            var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
            var reader = new ProjectReader();
            var snapshot = await reader.ReadAsync(entry, default);
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var compiler = new ModelConfigurationCompiler();
            var source = new SourceProbeWorkspace(new FailOnceWorkspace(workspace), compiler);
            using var bridge = new ModelConfigurationBridge(reader, source, compiler);
            using var currentEdits = new ModelEditService(reader, new FailOnceWorkspace(workspace));
            currentEdits.Select(snapshot);
            bridge.BindCreated(new("plan", "published", true, true, false, entry, snapshot.BaselineId, [], []));
            var context = await bridge.ContextAsync("created", null, default);

            var input = JsonSerializer.SerializeToElement(new
            {
                targetId = context.TargetId,
                baselineId = context.Project.BaselineId,
                section = "measures",
                request = new ModelConfigurationRequest(Measures: [new("ReviewOne", "value", "Uncategorized", true, "", "")], MeasureConfigurationRevision: context.MeasureConfigurationRevision)
            }, Json);
            await Assert.ThrowsAsync<IOException>(() => bridge.ApplyAsync(input, sharedCurrent ? snapshot : null, sharedCurrent ? currentEdits : null, default));
            var pending = await bridge.ContextAsync("created", null, default);
            Assert.True(pending.Project.HasPendingChanges);
            Assert.True(bridge.HasPendingChanges);
            // 恢复完成后文件能力可能暂时丢失; 业务成功不能被新的展示探测覆盖成失败.
            source.Fail = true;
            var saved = await bridge.RecoverAsync(JsonSerializer.SerializeToElement(new { targetId = pending.TargetId, baselineId = pending.Project.BaselineId }, Json),
                false, sharedCurrent ? snapshot : null, sharedCurrent ? currentEdits : null, default);
            Assert.False(saved.Project.HasPendingChanges);
            Assert.Null(saved.MeasureConfigUnavailableReason);
            Assert.Single(saved.Project.Model.Measures, measure => measure.Name == "ReviewOne");
            Assert.Single((await reader.ReadAsync(entry, default)).Model.Measures, measure => measure.Name == "ReviewOne");
            Assert.Equal(context.TargetId, saved.TargetId);
            Assert.False(bridge.HasPendingChanges);
            // 相同路径和完全相同内容仍不等于原来的文件对象.
            File.Move(entry, entry + ".old");
            File.Copy(entry + ".old", entry);
            var replaced = await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", null, default));
            Assert.Equal("STALE_CONFIGURATION_TARGET", replaced.Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>准备失败、准备取消或升级确认拒绝不保留服务别名, 其他工程的草稿不能解除已创建目标的外部修改门禁.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailedPreparationCannotBorrowAnotherTargetsPendingState(bool cancelUpgrade, bool cancelPreparation)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-configuration-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            async Task<ProjectSnapshot> ComposeAsync(string name)
            {
                var project = Path.Combine(root, name); Directory.CreateDirectory(project);
                var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                    new(1, template.Info.Id, template.Info.Baseline, name, "zh-cn",
                        [new("FixtureValue", "value", "Uncategorized", true, "", "")], null, null, null, null), project, project, null, default);
                return await new ProjectReader().ReadAsync(Path.Combine(project, composition.EntryFile), default);
            }
            var first = await ComposeAsync("First");
            var second = await ComposeAsync("Second");
            var reader = new ProjectReader();
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var compiler = new ModelConfigurationCompiler();
            using var cancellation = new CancellationTokenSource();
            IModelEditWorkspace activeWorkspace = cancelPreparation ? new CancelFirstPreparationWorkspace(workspace, cancellation) : workspace;
            using var bridge = new ModelConfigurationBridge(reader, activeWorkspace, compiler,
                confirmUpgrade: (_, _, _) => Task.FromResult(!cancelUpgrade));
            using var current = new ModelEditService(reader, activeWorkspace);
            current.Select(first);
            bridge.BindCreated(new("plan", "published", true, true, false, first.ProjectPath, first.BaselineId, [], []));
            var created = await bridge.ContextAsync("created", first, default);
            var input = JsonSerializer.SerializeToElement(new
            {
                targetId = created.TargetId,
                baselineId = first.BaselineId,
                section = cancelPreparation ? "measures" : "udfs",
                request = cancelPreparation ? new ModelConfigurationRequest(Measures: [new("CancelledMeasure", "value", "Uncategorized", true, "", "")], MeasureConfigurationRevision: created.MeasureConfigurationRevision)
                    : new ModelConfigurationRequest(Udfs: [new("local", "missing", "1.0.0", ["Selfcheck.Missing"])])
            }, Json);
            if (cancelPreparation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.ApplyAsync(input, first, current, cancellation.Token));
            else
            {
                var failed = await Assert.ThrowsAsync<AppException>(() => bridge.ApplyAsync(input, first, current, default));
                Assert.Equal(cancelUpgrade ? "CANCELLED" : "UDF_PROVIDER_UNAVAILABLE", failed.Code);
            }
            current.Select(second);
            await current.StageAsync(new("table.set", second.Model.Tables[0].Name, "description", "second draft"), default);
            Assert.True(current.HasPendingChanges);
            using var external = new ModelEditService(reader, workspace);
            external.Select(first);
            await external.StageAsync(new("table.set", first.Model.Tables[0].Name, "description", "external edit"), default);
            await external.SaveAsync(default);
            Assert.NotEqual(first.BaselineId, (await reader.ReadAsync(first.ProjectPath, default)).BaselineId);
            var stale = await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("created", second, default));
            Assert.Equal("STALE_CONFIGURATION_TARGET", stale.Code);
            Assert.False(bridge.HasPendingChanges);
            Assert.True(current.HasPendingChanges);
            Assert.Equal(second.BaselineId, (await reader.ReadAsync(second.ProjectPath, default)).BaselineId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>尾部继续保存也校验原入口对象, 相同字节的新文件不能继承已创建目标的权限.</summary>
    [Fact]
    public async Task CreatedRecoveryRejectsReplacedEntryBeforeSaving()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-configuration-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", null, null, null, null, null), project, project, null, default);
            var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
            var reader = new ProjectReader();
            var snapshot = await reader.ReadAsync(entry, default);
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var compiler = new ModelConfigurationCompiler();
            using var bridge = new ModelConfigurationBridge(reader, new FailOnceWorkspace(workspace), compiler);
            bridge.BindCreated(new("plan", "published", true, true, false, entry, snapshot.BaselineId, [], []));
            var created = await bridge.ContextAsync("created", null, default);

            var input = JsonSerializer.SerializeToElement(new
            {
                targetId = created.TargetId,
                baselineId = snapshot.BaselineId,
                section = "measures",
                request = new ModelConfigurationRequest(Measures: [new("RecoveryIdentity", "value", "Uncategorized", true, "", "")], MeasureConfigurationRevision: created.MeasureConfigurationRevision)
            }, Json);
            await Assert.ThrowsAsync<IOException>(() => bridge.ApplyAsync(input, null, null, default));
            var pending = await bridge.ContextAsync("created", null, default);
            File.Move(entry, entry + ".old"); File.Copy(entry + ".old", entry);
            var recovery = JsonSerializer.SerializeToElement(new { targetId = pending.TargetId, baselineId = pending.Project.BaselineId }, Json);
            var stale = await Assert.ThrowsAsync<AppException>(() => bridge.RecoverAsync(recovery, false, null, null, default));
            Assert.Equal("STALE_CONFIGURATION_TARGET", stale.Code);
            Assert.True(bridge.HasPendingChanges);
            Assert.DoesNotContain((await reader.ReadAsync(entry, default)).Model.Measures, measure => measure.Name == "RecoveryIdentity");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>仅在第一次创建操作上下文时取消, 后续其他工程操作仍转交真实隔离工作区.</summary>
    private sealed class CancelFirstPreparationWorkspace(IModelEditWorkspace inner, CancellationTokenSource cancellation) : IModelEditWorkspace
    {
        private bool cancelled;
        /// <summary>准备入口观察取消后不创建草稿; 后续调用使用其独立令牌并保留真实业务行为.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            if (!cancelled) { cancelled = true; cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return inner.CreateDraftAsync(baseline, token);
        }
    }

    /// <summary>独立控制展示来源故障, 原工作区仍拥有实际保存及草稿.</summary>
    private sealed class SourceProbeWorkspace(IModelEditWorkspace inner, ModelConfigurationCompiler compiler) : IModelEditWorkspace, IModelConfigurationAssetWorkspace
    {
        public bool Fail { get; set; }
        /// <summary>恢复前提供实际离线来源; 恢复后模拟文件探测失效, 不触碰业务保存.</summary>
        public async Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token)
        {
            if (Fail) throw new IOException("展示探测不应覆盖已经恢复的业务成功");
            return await compiler.ReadAssetContextAsync(snapshot, token);
        }
        /// <summary>借用原编辑服务创建的草稿, 不复制模型.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => inner.CreateDraftAsync(baseline, token);
    }

    /// <summary>只在本测试的第一次保存前注入故障, 后续调用仍使用原工作区与原草稿.</summary>
    private sealed class FailOnceWorkspace(IModelEditWorkspace workspace) : IModelEditWorkspace
    {
        public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => new FailOnceDraft(await workspace.CreateDraftAsync(baseline, token));
        private sealed class FailOnceDraft(IModelDraft inner) : IModelDraft
        {
            private bool failed;
            public ProjectSnapshot Snapshot => inner.Snapshot;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => inner.ApplyAsync(request, token);
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                if (failed) return inner.SaveAsync(committing, token);
                failed = true;
                throw new IOException("isolated storage failure");
            }
            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>构造最小目录快照, 不把它当真实引擎回读.</summary>
    private static ProjectSnapshot Snapshot(string path, string id) => new(path, id, "base", "TMDL", "none", "4.2", null,
        new("Test", 1702, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Test"));

    /// <summary>仅测试宿主身份, 禁止任何未声明的磁盘访问.</summary>
    private sealed class Reader : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => Task.FromResult(Snapshot(projectPath, "created") with { Connection = null });
    }
}
