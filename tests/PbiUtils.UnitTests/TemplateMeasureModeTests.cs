// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureModeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板度量存储模式、资产生命周期与人工改写保护.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using System.Text.Json;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用测试自有目录, 不执行数据源或连接真实模型.</summary>
public sealed class TemplateMeasureModeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-modes-" + Guid.NewGuid().ToString("N"));

    /// <summary>默认字符串度量不生成无用途的参数加载表或配置文件.</summary>
    [Fact]
    public async Task TmdlModeHasNoMeasureConfigurationTableOrAsset()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "Modes", "zh-cn",
            [new("author", "Jane", "Uncategorized", true, "", "")], null, null, null, null);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(stage, "pbip", "Modes.SemanticModel", "definition"));
        Assert.DoesNotContain(database.Model.Tables, table => table.Name is "C01_ReportVisualTemplates" or "PbiUtilsTemplateMeasures");
        Assert.False(File.Exists(Path.Combine(stage, "pbi-utils", "config.json")));
        Assert.Equal("\n\"Jane\"", database.Model.Tables["Measure"].Measures["author"].Expression);
    }

    /// <summary>Config 加载完整六字段 JSON, 引号与 SVG 保留原内容并生成安全 DAX 查值.</summary>
    [Fact]
    public async Task ConfigModeReadsJsonAndEscapesNames()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        TemplateMeasureDefinition[] definitions = [new("a\"b", "raw \"value\"", "Uncategorized", true, "说明", "folder"), new("image", "\r\n\t\u3000<svg/>", "ImageUrl", true, "", "")];
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "Modes", "zh-cn", definitions, null, null, null, null, MeasuresMode: "config");
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(stage, "pbip", "Modes.SemanticModel", "definition"));
        var config = database.Model.Tables["PbiUtilsTemplateMeasures"];
        Assert.True(config.IsHidden);
        Assert.Contains("/pbi-utils/config.json", ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression);
        Assert.Contains("Source[MeasureTemplates]", ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression);
        Assert.Contains("\"a\"\"b\"", database.Model.Tables["Measure"].Measures["a\"b"].Expression);
        Assert.Contains("data:image/svg+xml;utf8,", database.Model.Tables["Measure"].Measures["image"].Expression);
        DaxAssert.Contains("SEARCH(\"<svg*</svg>\", ConfigValue, 1, BLANK())", database.Model.Tables["Measure"].Measures["image"].Expression);
        Assert.DoesNotContain("GENERATESERIES", database.Model.Tables["Measure"].Measures["image"].Expression);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(stage, "pbi-utils", "config.json")));
        Assert.Equal("raw \"value\"", json.RootElement.GetProperty("MeasureTemplates")[0].GetProperty("value").GetString());
        Assert.Equal("\r\n\t\u3000<svg/>", json.RootElement.GetProperty("MeasureTemplates")[1].GetProperty("value").GetString());
        Assert.Equal("config", ModelSnapshotMapper.Map(database, "model").TemplateMeasureConfiguration!.Mode);
    }

    /// <summary>真实离线文件事务完成双向切换, 已移除旧配置项实际删掉且不产生悬挂引用.</summary>
    [Fact]
    public async Task OfflineSwitchRemovesJsonAndDeletesRemovedItems()
    {
        var entry = CreateEntry();
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        TemplateMeasureDefinition[] definitions = [new("first", "one", "Uncategorized", true, "", ""), new("removed", "two", "WebUrl", true, "", "")];
        var prepared = await compiler.PrepareAsync(new(Measures: definitions, MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        Assert.True(File.Exists(Path.Combine(root, "project", "pbi-utils", "config.json")));
        Assert.Equal("config", snapshot.Model.TemplateMeasureConfiguration!.Mode);
        var next = await compiler.PrepareAsync(new(Measures: [definitions[0]], MeasuresMode: "tmdl"), snapshot, default);
        Assert.Contains(next.Configuration!.Assets, asset => asset.RelativePath == "pbi-utils/config.json" && asset.AfterHash is null);
        snapshot = await edits.EditConfigurationAndSaveAsync(next, default, beforeCommit: token => compiler.VerifyAsync(next, token));
        Assert.False(File.Exists(Path.Combine(root, "project", "pbi-utils", "config.json")));
        Assert.DoesNotContain(snapshot.Model.Tables, table => table.Name == "PbiUtilsTemplateMeasures");
        Assert.DoesNotContain(snapshot.Model.Measures, measure => measure.Name == "removed");
        Assert.Equal("tmdl", snapshot.Model.TemplateMeasureConfiguration!.Mode);
        var again = await compiler.PrepareAsync(new(Measures: definitions, MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(again, default, beforeCommit: token => compiler.VerifyAsync(again, token));
        DaxAssert.Contains("CONCATENATEX", snapshot.Model.Measures.Single(measure => measure.Name == "first").Expression);
    }

    /// <summary>其他业务配置段保持原结构, 只移除本功能配置; 修改未知文件仍拒绝覆盖.</summary>
    [Fact]
    public void JsonRemovalPreservesUnrelatedSections()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"schemaVersion":1,"MeasureTemplates":[],"ReportPages":[{"id":"keep"}],"Custom":{"enabled":true}}""");
        using var content = JsonDocument.Parse(TemplateMeasureConfigurationStorage.JsonBytes(bytes, "tmdl", [])!);
        Assert.False(content.RootElement.TryGetProperty("MeasureTemplates", out _));
        Assert.Equal("keep", content.RootElement.GetProperty("ReportPages")[0].GetProperty("id").GetString());
        Assert.True(content.RootElement.GetProperty("Custom").GetProperty("enabled").GetBoolean());
        Assert.Throws<AppException>(() => TemplateMeasureConfigurationStorage.JsonBytes(System.Text.Encoding.UTF8.GetBytes("{broken"), "config", []));
    }

    /// <summary>删除计划冻结后外部改写不得被删除或覆盖, 模型和文件仍保留原配置事实.</summary>
    [Fact]
    public async Task FrozenDeletionRejectsConcurrentJsonChange()
    {
        var (entry, snapshot) = await CreateConfigProject();
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Measures: snapshot.Model.TemplateMeasureConfiguration!.Measures, MeasuresMode: "tmdl"), snapshot, default);
        var config = Path.Combine(root, "project", "pbi-utils", "config.json");
        File.AppendAllText(config, " ");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => compiler.VerifyAsync(prepared, default))).Code);
        Assert.Equal("config", (await new ProjectReader().ReadAsync(entry, default)).Model.TemplateMeasureConfiguration!.Mode);
        Assert.True(File.Exists(config));
        compiler.Release(prepared);
    }

    /// <summary>人工更改度量或参数表后明确配置仍可覆盖并保存, 不以旧归属阻止模式切换.</summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("table")]
    public async Task ExplicitConfigurationOverwritesManualTemplateChanges(string target)
    {
        var (entry, _) = await CreateConfigProject();
        var modelFile = Path.Combine(root, "project", "pbip", "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
        {
            if (target == "measure") database.Model.Tables["Measure"].Measures["first"].Expression = "\"manual\"";
            else database.Model.Tables["PbiUtilsTemplateMeasures"].Description = "manual";
            File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Measures: [new("first", "next", "Uncategorized", true, "", "")]), snapshot, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(new ProjectReader(), workspace); edits.Select(snapshot);

        var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        Assert.Equal("\n\"next\"", saved.Model.Measures.Single(measure => measure.Name == "first").Expression);
        Assert.Equal("tmdl", saved.Model.TemplateMeasureConfiguration!.Mode);
    }

    /// <summary>未知模型引用保留隐藏参数表和用户计算, 同时允许模板度量切回 TMDL.</summary>
    [Fact]
    public async Task UnknownReferenceKeepsParameterTableAndAllowsTmdl()
    {
        var (entry, _) = await CreateConfigProject();
        var modelFile = Path.Combine(root, "project", "pbip", "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
        {
            database.Model.Tables["销售"].Measures.Add(new TOM.Measure { Name = "user", Expression = "COUNTROWS('PbiUtilsTemplateMeasures')" });
            File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Measures: snapshot.Model.TemplateMeasureConfiguration!.Measures), snapshot, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(new ProjectReader(), workspace); edits.Select(snapshot);

        var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        Assert.Equal("tmdl", saved.Model.TemplateMeasureConfiguration!.Mode);
        Assert.Contains(saved.Model.Tables, table => table.Name == "PbiUtilsTemplateMeasures");
        Assert.Equal("COUNTROWS('PbiUtilsTemplateMeasures')", saved.Model.Measures.Single(measure => measure.Name == "user").Expression);
        Assert.Equal("\n\"one\"", saved.Model.Measures.Single(measure => measure.Name == "first").Expression);
    }

    /// <summary>没有宿主可信文件能力的 Desktop 模型在冻结前回退 TMDL, 不创建或覆盖文件.</summary>
    [Fact]
    public async Task ConfigRequiresTrustedDesktopAssetContext()
    {
        var entry = CreateEntry();
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        snapshot = snapshot with { ProjectPath = "model://trusted", Connection = new("desktop", "Desktop") };
        using var compiler = new ModelConfigurationCompiler();
        TemplateMeasureDefinition[] definitions = [new("first", "one", "Uncategorized", true, "", "")];
        var fallback = await compiler.PrepareAsync(new(Measures: definitions, MeasuresMode: "config"), snapshot, default);
        Assert.Equal("tmdl", fallback.Configuration!.Request.MeasuresMode);
        Assert.Empty(fallback.Configuration.Assets);
        compiler.Release(fallback);
        var prepared = await compiler.PrepareAsync(new(Measures: definitions), snapshot, default);
        Assert.Empty(prepared.Configuration!.Assets);
        compiler.Release(prepared);
    }

    /// <summary>搬移后的可信工程根覆盖旧的受保护参数值, 不让新资产与旧加载路径分离.</summary>
    [Fact]
    public async Task MovedProjectUpdatesOwnedRootToCurrentAssetDirectory()
    {
        var (entry, _) = await CreateConfigProject();
        var moved = Path.Combine(root, "moved");
        Directory.Move(Path.Combine(root, "project"), moved);
        entry = Path.Combine(moved, "pbip", Path.GetFileName(entry));
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Measures: snapshot.Model.TemplateMeasureConfiguration!.Measures, MeasuresMode: "config"), snapshot, default);
        using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(moved, "pbip", "model", "model.bim")));
        ModelMutation.Apply(database, new(entry, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        Assert.StartsWith(PowerQueryLiterals.Text(moved), database.Model.Expressions["PbiUtilsProjectRoot"].Expression);
        compiler.Release(prepared);
    }

    /// <summary>未选中用户度量保持原值; 用户随后明确选中同名项时直接覆盖并保存.</summary>
    [Fact]
    public async Task ExplicitSelectionOverwritesPreviouslyUnmanagedMeasure()
    {
        var entry = CreateEntry();
        var modelFile = Path.Combine(root, "project", "pbip", "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
        {
            var table = new TOM.Table { Name = "Measure" };
            table.Measures.Add(new TOM.Measure { Name = "user", Expression = "123" });
            database.Model.Tables.Add(table);
            File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var first = await compiler.PrepareAsync(new(Measures: [new("generated", "one", "Uncategorized", true, "", "")]), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(first, default, beforeCommit: token => compiler.VerifyAsync(first, token));
        Assert.Equal("123", snapshot.Model.Measures.Single(measure => measure.Name == "user").Expression);
        var replacement = await compiler.PrepareAsync(new(Measures: [new("user", "replace", "Uncategorized", true, "", "")]), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(replacement, default, beforeCommit: token => compiler.VerifyAsync(replacement, token));
        Assert.Equal("\n\"replace\"", snapshot.Model.Measures.Single(measure => measure.Name == "user").Expression);
    }

    /// <summary>TMDL 离线保存真正移除旧表文档, 不能只在候选内删表后由落盘回读复活.</summary>
    [Fact]
    public async Task TmdlFileTransactionRemovesDeletedParameterTableDocument()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        TemplateMeasureDefinition[] definitions = [new("first", "one", "Uncategorized", true, "", "")];
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "Modes", "zh-cn", definitions, null, null, null, null, MeasuresMode: "config");
        var composed = await new ScaffoldModelWriter().ComposeAsync(template, request, project, project, null, default);
        var entry = Path.Combine(project, composed.EntryFile);
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Measures: definitions), snapshot, default);
        var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        Assert.DoesNotContain(saved.Model.Tables, table => table.Name == "PbiUtilsTemplateMeasures");
        Assert.False(File.Exists(Path.Combine(project, "pbip", "Modes.SemanticModel", "definition", "tables", "PbiUtilsTemplateMeasures.tmdl")));
        Assert.False(File.Exists(Path.Combine(project, "pbi-utils", "config.json")));
    }

    /// <summary>其他功能生成的同名表继续保留角色与内容; Config 自动使用独立参数表而不阻止切换.</summary>
    [Theory]
    [InlineData("tmdl")]
    [InlineData("config")]
    public async Task CalendarUsingDefaultConfigNameKeepsItsOwnRole(string mode)
    {
        var entry = CreateEntry();
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        const string name = "PbiUtilsTemplateMeasures";
        var calendar = await compiler.PrepareAsync(new(Calendar: new("simple", 2024, 2024, name)), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(calendar, default, beforeCommit: token => compiler.VerifyAsync(calendar, token));
        var request = new ModelConfigurationRequest(Measures: [new("author", "Jane", "Uncategorized", true, "", "")], MeasuresMode: mode);
        var measures = await compiler.PrepareAsync(request, snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(measures, default, beforeCommit: token => compiler.VerifyAsync(measures, token));
        Assert.Contains(snapshot.Model.Tables, table => table.Name == name);
        using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "pbip", "model", "model.bim")));
        Assert.Equal("calendar", ModelConfigurationOwnership.ReadRoles(database.Model).Roles[name]);
        Assert.True(ModelConfigurationOwnership.IsOwned(database.Model.Tables[name], database.Model.Tables[name].Annotations));
        if (mode == "config")
        {
            Assert.Contains("PbiUtilsTemplateMeasures_1", snapshot.Model.Measures.Single(measure => measure.Name == "author").Expression);
            Assert.Equal("measure-config", ModelConfigurationOwnership.ReadRoles(database.Model).Roles["PbiUtilsTemplateMeasures_1"]);
        }
    }

    /// <summary>没有模板参数角色的用户同名表既不被认领也不阻止普通 TMDL 度量添加.</summary>
    [Fact]
    public void UnownedDefaultConfigNameIsPreservedInTmdlMode()
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model { Culture = "en-US" } };
        var user = new TOM.Table { Name = "PbiUtilsTemplateMeasures", Description = "user table" };
        database.Model.Tables.Add(user);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [new("author", "Jane", "Uncategorized", true, "", "")], "tmdl", roles, configuration: true);
        Assert.Same(user, database.Model.Tables.Find(user.Name));
        Assert.Equal("user table", user.Description);
        Assert.False(ModelConfigurationOwnership.IsOwned(user, user.Annotations));
        Assert.DoesNotContain(user.Name, roles.Keys);
    }

    /// <summary>创建真实最小 PBIP 并经过共享确认保存通道安装 Config, 返回权威文件快照.</summary>
    private async Task<(string Entry, ProjectSnapshot Snapshot)> CreateConfigProject()
    {
        var entry = CreateEntry();
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Measures: [new("first", "one", "Uncategorized", true, "", "")], MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        return (entry, snapshot);
    }

    /// <summary>仅为 Config 用例建立约定的真实同级目录.</summary>
    private string CreateEntry()
    {
        Directory.CreateDirectory(Path.Combine(root, "project", "pbi-utils"));
        return PbipFixture.Create(Path.Combine(root, "project", "pbip"));
    }

    /// <summary>仅删除本测试创建的随机目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
