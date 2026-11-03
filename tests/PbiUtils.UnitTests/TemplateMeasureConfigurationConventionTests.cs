// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureConfigurationConventionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证约定配置文件的权威读取、并发冻结和模板度量真实删除边界.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅访问本测试随机目录及离线 TOM, 不接入用户模型.</summary>
public sealed class TemplateMeasureConfigurationConventionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-convention-" + Guid.NewGuid().ToString("N"));

    /// <summary>固定字段使用 MeasureTemplates, 六字段状态和其他业务段均可往返.</summary>
    [Fact]
    public void ConfigurationUsesRequestedSemanticField()
    {
        var before = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"Custom":{"keep":true}}""");
        var bytes = TemplateMeasureConfigurationStorage.JsonBytes(before, "config", [Definition("author", "from file")])!;
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal("from file", json.RootElement.GetProperty("MeasureTemplates")[0].GetProperty("value").GetString());
        Assert.True(json.RootElement.GetProperty("MeasureTemplates")[0].GetProperty("status").GetBoolean());
        Assert.True(json.RootElement.GetProperty("Custom").GetProperty("keep").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("ReportVisualTemplates", out _));
    }

    /// <summary>有效文件由磁盘读取, 不以缺少历史模型注解为拒绝原因.</summary>
    [Fact]
    public void ExistingConfigurationComesFromEditableFile()
    {
        var project = Path.Combine(root, "project");
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        File.WriteAllText(Path.Combine(project, "pbi-utils", "config.json"), """{"schemaVersion":1,"MeasureTemplates":[{"name":"author","value":"from file","dataCategory":"Uncategorized","status":true,"description":"","displayFolder":""}]}""");
        using var database = Database();
        var context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, project, entry, _ => Task.CompletedTask);
        Assert.Equal("from file", Assert.Single(context.ExistingConfiguration!.Measures).Value);
    }

    /// <summary>列表缩减实际移除受保护对象, 改名不会留下旧常量, 未托管用户度量继续保留.</summary>
    [Theory]
    [InlineData("tmdl")]
    [InlineData("config")]
    public void RemovingAndRenamingManagedItemsDeletesOldMeasures(string mode)
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(model, [Definition("old", "one"), Definition("keep", "two")], mode, roles, configuration: true);
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "user", Expression = "123" });
        ModelConfigurationOwnership.Mark(model.Tables["Measure"], model.Tables["Measure"].Annotations);
        ScaffoldTemplateMeasureMode.Apply(model, [Definition("renamed", "one"), Definition("keep", "changed")], mode, roles, configuration: true);
        Assert.Null(model.Tables["Measure"].Measures.Find("old"));
        Assert.NotNull(model.Tables["Measure"].Measures.Find("renamed"));
        Assert.Equal("123", model.Tables["Measure"].Measures["user"].Expression);
    }

    /// <summary>存活用户表达式引用旧模板时, 真删必须整批拒绝而不是留下过期配置事实.</summary>
    [Fact]
    public void ReferencedManagedItemCannotBeDeleted()
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("old", "one")], "tmdl", roles, configuration: true);
        database.Model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "user", Expression = "[old]" });
        ModelConfigurationOwnership.Mark(database.Model.Tables["Measure"], database.Model.Tables["Measure"].Annotations);
        Assert.Equal("CONFIGURATION_MEASURE_REFERENCED", Assert.Throws<AppException>(() =>
            ScaffoldTemplateMeasureMode.Apply(database.Model, [], "tmdl", roles, configuration: true)).Code);
        Assert.NotNull(database.Model.Tables["Measure"].Measures.Find("old"));
    }

    /// <summary>用户外部修改公共配置后可读取和编辑最新内容, 确认期间再次变化仍拒绝覆盖.</summary>
    [Fact]
    public async Task ExternalConfigurationChangesAreReadableButFrozenRevisionIsProtected()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var initial = await compiler.PrepareAsync(new(Measures: [Definition("author", "initial")], MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(initial, default, beforeCommit: ct => compiler.VerifyAsync(initial, ct));
        compiler.Release(initial);
        var path = Path.Combine(project, "pbi-utils", "config.json");
        var old = await compiler.ReadAssetContextAsync(snapshot, default);
        File.WriteAllText(path, """{"schemaVersion":1,"MeasureTemplates":[{"name":"author","value":"external","dataCategory":"Uncategorized","status":true,"description":"","displayFolder":""}],"Custom":{"keep":true}}""");
        var current = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.True(current.CanUseMeasureConfig);
        Assert.NotEqual(old.ConfigurationRevision, current.ConfigurationRevision);
        Assert.Equal("external", Assert.Single(current.ExistingConfiguration!.Measures).Value);
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(
            new(Measures: [Definition("author", "stale")], MeasuresMode: "config", MeasureConfigurationRevision: old.ConfigurationRevision), snapshot, default))).Code);
        var prepared = await compiler.PrepareAsync(new(Measures: [Definition("author", "next")], MeasuresMode: "config",
            MeasureConfigurationRevision: current.ConfigurationRevision), snapshot, default);
        var asset = Assert.Single(prepared.Configuration!.Assets);
        using (var content = JsonDocument.Parse(File.ReadAllBytes(asset.SourceFile!))) Assert.True(content.RootElement.GetProperty("Custom").GetProperty("keep").GetBoolean());
        File.AppendAllText(path, " ");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => compiler.VerifyAsync(prepared, default))).Code);
        Assert.Contains("external", File.ReadAllText(path));
        compiler.Release(prepared);
    }

    /// <summary>缺失配置文件可首次创建, 固定 missing 修订不能覆盖随后新增的文件.</summary>
    [Fact]
    public async Task MissingConfigurationUsesExplicitRevision()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var context = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.True(context.CanUseMeasureConfig);
        Assert.Equal("missing", context.ConfigurationRevision);
        Assert.Null(context.ExistingConfiguration);
        var prepared = await compiler.PrepareAsync(new(Measures: [], MeasuresMode: "config", MeasureConfigurationRevision: "missing"), snapshot, default);
        Assert.Null(Assert.Single(prepared.Configuration!.Assets).BeforeHash);
        File.WriteAllText(Path.Combine(project, "pbi-utils", "config.json"), """{"schemaVersion":1,"MeasureTemplates":[]}""");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => compiler.VerifyAsync(prepared, default))).Code);
        compiler.Release(prepared);
    }

    /// <summary>坏 JSON、重复键和非法字段撤销 Config 能力, 冻结为 TMDL 并保留所有原始字节.</summary>
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":1,\"MeasureTemplates\":[],\"MeasureTemplates\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"MeasureTemplates\":[{\"name\":\"a\",\"value\":\"v\",\"dataCategory\":\"unknown\",\"status\":true,\"description\":\"\",\"displayFolder\":\"\"}]}")]
    public async Task InvalidConfigurationFallsBackWithoutChangingTheFile(string content)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        var path = Path.Combine(project, "pbi-utils", "config.json"); File.WriteAllText(path, content);
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var context = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.False(context.CanUseMeasureConfig);
        Assert.Equal("invalid", context.UnavailableReason);
        Assert.Null(context.ExistingConfiguration);
        var prepared = await compiler.PrepareAsync(new(Measures: [Definition("author", "next")], MeasuresMode: "config"), snapshot, default);
        Assert.Equal("tmdl", prepared.Configuration!.Request.MeasuresMode);
        Assert.Empty(prepared.Configuration.Assets);
        Assert.Equal(content, File.ReadAllText(path));
        compiler.Release(prepared);
    }

    /// <summary>普通 PBIP 和缺失目录均可首次使用 Config, 其他位置的用户文件不参与当前配置生命周期.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothLayoutsAllowConfigAndPreserveUnrelatedConfiguration(bool wrongEntry)
    {
        var project = Path.Combine(root, "project");
        var entry = PbipFixture.Create(wrongEntry ? project : Path.Combine(project, "pbip"));
        if (wrongEntry) Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var oldPath = Path.Combine(project, "config", "config.json"); Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "old user configuration");
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var context = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.True(context.CanUseMeasureConfig);
        Assert.Equal("missing", context.ConfigurationRevision);
        Assert.Null(context.UnavailableReason);
        var prepared = await compiler.PrepareAsync(new(Measures: [Definition("author", "next")], MeasuresMode: "config"), snapshot, default);
        Assert.Equal("config", prepared.Configuration!.Request.MeasuresMode);
        Assert.Equal("pbi-utils/config.json", Assert.Single(prepared.Configuration.Assets).RelativePath);
        Assert.Equal("old user configuration", File.ReadAllText(oldPath));
        compiler.Release(prepared);
    }

    /// <summary>新建两种模式均保留约定目录, TMDL 不生成配置 JSON.</summary>
    [Theory]
    [InlineData("tmdl")]
    [InlineData("config")]
    public async Task NewProjectsAlwaysCreateTheConfigurationDirectory(string mode)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "test", "zh-cn", [Definition("author", "one")], null, null, null, null, MeasuresMode: mode),
            project, project, null, default);
        Assert.True(Directory.Exists(Path.Combine(project, "pbi-utils")));
        Assert.Equal(mode == "config", File.Exists(Path.Combine(project, "pbi-utils", "config.json")));
        var snapshot = await new ProjectReader().ReadAsync(Path.Combine(project, composition.EntryFile), default);
        using var compiler = new ModelConfigurationCompiler();
        Assert.True((await compiler.ReadAssetContextAsync(snapshot, default)).CanUseMeasureConfig);
    }

    /// <summary>用户模板中的坏配置仍保留原文, 创建阶段就回退 TMDL, 不发布无有效数据文件的 Config 模型.</summary>
    [Fact]
    public async Task NewProjectWithInvalidConfigurationFallsBackBeforeModelComposition()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        template = template with { Files = [.. template.Files, new("pbi-utils/config.json", Encoding.UTF8.GetBytes("{broken"))] };
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "test", "zh-cn", [Definition("author", "one")], null, null, null, null, MeasuresMode: "config"),
            project, project, null, default);
        var snapshot = await new ProjectReader().ReadAsync(Path.Combine(project, composition.EntryFile), default);
        Assert.Equal("tmdl", snapshot.Model.TemplateMeasureConfiguration!.Mode);
        Assert.Equal("\n\"one\"", snapshot.Model.Measures.Single(value => value.Name == "author").Expression);
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(project, "pbi-utils", "config.json")));
    }

    /// <summary>日期段冻结仅绑定其模型和输出资产, 无关模板 JSON 的后续编辑不会阻断日期提交.</summary>
    [Fact]
    public async Task CalendarPreparationDoesNotBindUnrelatedMeasureConfiguration()
    {
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        var path = Path.Combine(project, "pbi-utils", "config.json"); File.WriteAllText(path, """{"schemaVersion":1,"MeasureTemplates":[]}""");
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Calendar: new("simple", 2024, 2024)), snapshot, default);
        File.AppendAllText(path, " ");
        await compiler.VerifyAsync(prepared, default);
        Assert.Null(prepared.Configuration!.AssetContext);
        compiler.Release(prepared);
    }

    /// <summary>既有 Config 目录被移除或 JSON 损坏后仍可明确提交 TMDL, 不重新创建目录或触碰无效文件.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingConfigCanReturnToTmdlWhenItsFileCapabilityIsLost(bool corrupt)
    {
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(Path.Combine(project, "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(project, "pbip"));
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var initial = await compiler.PrepareAsync(new(Measures: [Definition("author", "before")], MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(initial, default, beforeCommit: ct => compiler.VerifyAsync(initial, ct));
        compiler.Release(initial);
        var path = Path.Combine(project, "pbi-utils", "config.json");
        if (corrupt) File.WriteAllText(path, "{broken");
        else { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        var prepared = await compiler.PrepareAsync(new(Measures: [Definition("author", "after")], MeasuresMode: "tmdl"), snapshot, default);
        Assert.Equal("tmdl", prepared.Configuration!.Request.MeasuresMode);
        Assert.Empty(prepared.Configuration.Assets);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
        Assert.Equal("tmdl", snapshot.Model.TemplateMeasureConfiguration!.Mode);
        Assert.Equal("\n\"after\"", snapshot.Model.Measures.Single(value => value.Name == "author").Expression);
        Assert.DoesNotContain(snapshot.Model.Tables, table => table.Name == "PbiUtilsTemplateMeasures");
        if (corrupt) Assert.Equal("{broken", File.ReadAllText(path));
        else Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        compiler.Release(prepared);
    }

    /// <summary>创建单个普通文本定义, 期望值不依赖被测序列化器.</summary>
    private static TemplateMeasureDefinition Definition(string name, string value) => new(name, value, "Uncategorized", true, "", "");

    /// <summary>最小离线 TOM 模型不加载真实业务数据或执行查询.</summary>
    private static TOM.Database Database() => new() { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model { Culture = "en-US" } };

    /// <summary>仅清理本测试创建的随机目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
