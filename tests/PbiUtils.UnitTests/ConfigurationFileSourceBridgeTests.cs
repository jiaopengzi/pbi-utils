// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ConfigurationFileSourceBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证配置桥接展示磁盘权威值、目录回退及旧草稿版本拒绝, 不连接真实引擎.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>宿主文件能力由受控工作区提供, 任何意外进入模型修改立即失败.</summary>
public sealed class ConfigurationFileSourceBridgeTests
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly TemplateMeasureDefinition[] FileMeasures = [new("from_file", "current", "WebUrl", true, "说明", "目录")];

    /// <summary>TMDL 成功响应与独立文件回读一致, 保留自定义段时 Config 是空列表, 删除文件时才是无来源.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TmdlSaveProjectsExactRemainingFileSource(bool keepCustomSection)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-config-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", FileMeasures, null, null, null, null, MeasuresMode: "config"),
                project, project, null, default);
            var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
            var json = Path.Combine(project, "pbi-utils", "config.json");
            if (keepCustomSection)
            {
                var content = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
                content["NativeCustom"] = new JsonObject { ["keep"] = true };
                File.WriteAllText(json, content.ToJsonString());
            }
            var reader = new ProjectReader();
            var snapshot = await reader.ReadAsync(entry, default);
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var compiler = new ModelConfigurationCompiler();
            using var bridge = new ModelConfigurationBridge(reader, workspace, compiler);
            using var edits = new ModelEditService(reader, workspace);
            edits.Select(snapshot);
            var before = await bridge.ContextAsync("current", snapshot, default);

            var request = new ModelConfigurationRequest(Measures: FileMeasures, MeasuresMode: "tmdl", MeasureConfigurationRevision: before.MeasureConfigurationRevision);
            var input = JsonSerializer.SerializeToElement(new { before.TargetId, snapshot.BaselineId, Section = "measures", Request = request }, Json);

            var saved = await bridge.ApplyAsync(input, snapshot, edits, default);
            var fresh = await bridge.ContextAsync("current", saved.Project, default);

            Assert.Equal(fresh.MeasureConfigurationRevision, saved.MeasureConfigurationRevision);
            Assert.Equal(fresh.ConfigMeasures, saved.ConfigMeasures);
            if (keepCustomSection) Assert.Empty(saved.ConfigMeasures!);
            else Assert.Null(saved.ConfigMeasures);
            Assert.Equal(keepCustomSection, File.Exists(json));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>相邻段成功保存保留实际模板来源; 坏 JSON 不参与日期计划, 不阻断写入或被覆盖.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalendarSaveReturnsActualMeasureSourceWithoutBindingItsRevision(bool invalidJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-config-adjacent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", FileMeasures, null, null, null, null, MeasuresMode: "config"),
                project, project, null, default);
            var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
            var json = Path.Combine(project, "pbi-utils", "config.json");
            if (invalidJson) File.WriteAllText(json, "{");
            var bytes = File.ReadAllBytes(json);
            var reader = new ProjectReader();
            var snapshot = await reader.ReadAsync(entry, default);
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var compiler = new ModelConfigurationCompiler();
            using var bridge = new ModelConfigurationBridge(reader, workspace, compiler);
            using var edits = new ModelEditService(reader, workspace);
            edits.Select(snapshot);
            var before = await bridge.ContextAsync("current", snapshot, default);

            var request = new ModelConfigurationRequest(Calendar: new("simple", 2024, 2024));
            var input = JsonSerializer.SerializeToElement(new { before.TargetId, snapshot.BaselineId, Section = "calendar", Request = request }, Json);

            var saved = await bridge.ApplyAsync(input, snapshot, edits, default);

            Assert.Contains(saved.Project.Model.Tables, table => table.Name == "Calendar");
            Assert.Equal(!invalidJson, saved.CanUseMeasureConfig);
            Assert.Equal(invalidJson ? "tmdl" : "config", saved.MeasureConfiguration!.Mode);
            Assert.Equal(before.MeasureConfigurationRevision, saved.MeasureConfigurationRevision);
            Assert.Equal(bytes, File.ReadAllBytes(json));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>有效文件值和 revision 单独返回, 不把磁盘值伪装为引擎事实或创建编辑会话.</summary>
    [Fact]
    public async Task FileConfigurationIsExposedWithoutReplacingEngineSnapshot()
    {
        var snapshot = Snapshot();
        var workspace = new Workspace(Source());
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(snapshot), workspace, compiler, currentWorkspace: workspace);

        var context = await bridge.ContextAsync("current", snapshot, default);
        var wire = JsonSerializer.SerializeToElement(context, Json);

        Assert.True(wire.TryGetProperty("measureConfiguration", out var configuration));
        Assert.Equal("config", configuration.GetProperty("mode").GetString());
        Assert.Equal("from_file", configuration.GetProperty("measures")[0].GetProperty("name").GetString());
        Assert.Equal(new string('a', 64), wire.GetProperty("measureConfigurationRevision").GetString());
        Assert.Equal("from_engine", context.Project.Model.TemplateMeasureConfiguration!.Measures[0].Name);
        Assert.True(context.CanUseMeasureConfig);
        Assert.Equal(0, workspace.DraftCalls);
    }

    /// <summary>PBIP 后缀本身不能发放约定文件访问能力.</summary>
    [Fact]
    public async Task PbipSuffixDoesNotGrantConfigCapability()
    {
        var snapshot = Snapshot() with { ProjectPath = "C:/unverified/project.pbip" };
        var workspace = new Workspace(null);
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(snapshot), workspace, compiler, currentWorkspace: workspace);

        var context = await bridge.ContextAsync("current", snapshot, default);

        Assert.False(context.CanUseMeasureConfig);
        Assert.Equal(0, workspace.DraftCalls);
    }

    /// <summary>坏文件撤销 Config 并展示 TMDL 草稿, 读取不会自动向引擎写入.</summary>
    [Fact]
    public async Task InvalidFileUsesTmdlDisplayWithoutWriting()
    {
        var snapshot = Snapshot();
        var workspace = new Workspace(Source() with { CanUseMeasureConfig = false, ExistingConfiguration = null, ConfigurationRevision = null, UnavailableReason = "invalid" });
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(snapshot), workspace, compiler, currentWorkspace: workspace);

        var context = await bridge.ContextAsync("current", snapshot, default);
        var wire = JsonSerializer.SerializeToElement(context, Json);

        Assert.False(context.CanUseMeasureConfig);
        Assert.Equal("tmdl", wire.GetProperty("measureConfiguration").GetProperty("mode").GetString());
        Assert.Equal("invalid", wire.GetProperty("measureConfigUnavailableReason").GetString());
        Assert.Equal("config", context.Project.Model.TemplateMeasureConfiguration!.Mode);
        Assert.Equal(0, workspace.DraftCalls);
    }

    /// <summary>从旧版本文件编辑的请求不能取得新文件版本后继续写入.</summary>
    [Fact]
    public async Task StaleFileRevisionRejectsBeforeCreatingDraft()
    {
        var snapshot = Snapshot();
        var workspace = new Workspace(Source());
        var reader = new Reader(snapshot);
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(reader, workspace, compiler, currentWorkspace: workspace);
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(snapshot);
        var context = await bridge.ContextAsync("current", snapshot, default);
        var request = new ModelConfigurationRequest(Measures: FileMeasures, MeasuresMode: "config", MeasureConfigurationRevision: new string('b', 64));
        var parameters = JsonSerializer.SerializeToElement(new { context.TargetId, snapshot.BaselineId, Section = "measures", Request = request }, Json);

        var error = await Assert.ThrowsAsync<AppException>(() => bridge.ApplyAsync(parameters, snapshot, edits, default,
            () => throw new InvalidOperationException("过期配置来源不得提交")));

        Assert.Equal("STALE_CONFIGURATION_SOURCE", error.Code);
        Assert.Equal(0, workspace.DraftCalls);
    }

    /// <summary>构造受信来源事实, 不执行任何路径访问.</summary>
    private static ModelConfigurationAssetContext Source() => new("C:/host-verified-project", new Dictionary<string, string>(), _ => Task.CompletedTask,
        ExistingConfiguration: new("config", FileMeasures), CanUseMeasureConfig: true, ConfigurationRevision: new string('a', 64));

    /// <summary>引擎元数据故意与文件值不同, 验证两个事实来源不混用.</summary>
    private static ProjectSnapshot Snapshot() => new("model://source-test", "project", "baseline", "test", "", "", null,
        new("model", 1606, "zh-CN", [], [], [], 0, 0, TemplateMeasureConfiguration: new("config", [new("from_engine", "old", "Uncategorized", true, "", "")])),
        [], [], [], new("desktop", "Power BI Desktop"));

    /// <summary>仅返回调用方拥有的固定快照, 不访问文件.</summary>
    private sealed class Reader(ProjectSnapshot snapshot) : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    /// <summary>提供受控文件事实, 意外创建草稿立即失败并计数.</summary>
    private sealed class Workspace(ModelConfigurationAssetContext? source) : IModelEditWorkspace, IModelConfigurationAssetWorkspace
    {
        public int DraftCalls { get; private set; }
        public Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token) => Task.FromResult(source);
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            DraftCalls++;
            throw new InvalidOperationException("只读/过期文件请求不应创建编辑会话");
        }
    }
}
