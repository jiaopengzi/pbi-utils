// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureExistingQnaTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前生成参数表被人工 Q&A 引用时的模式切换和元数据保护.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只在当前空白模板生成的工程内加入自有人工词汇, 不模拟已移除的内置 Q&A.</summary>
public sealed class TemplateMeasureExistingQnaTests : IDisposable
{
    private const string ConfigTable = "PbiUtilsTemplateMeasures";
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-existing-qna-" + Guid.NewGuid().ToString("N"));

    /// <summary>人工表/列词汇保留原文并阻止参数表被删除, 切回 TMDL 后改用完整内嵌快照.</summary>
    [Theory]
    [InlineData(true, "table")]
    [InlineData(true, "column")]
    [InlineData(true, "extra")]
    [InlineData(false, "table")]
    [InlineData(false, "column")]
    public async Task ManualQnaIsPreservedWithoutBlockingTmdl(bool generatedCalendar, string bindingKind)
    {
        var (entry, folder, manual) = await CreateExisting(generatedCalendar, bindingKind);
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);
        var definitions = TemplateMeasureValidation.DefaultMeasures();
        var config = await compiler.PrepareAsync(new(Measures: definitions, MeasuresMode: "config"), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(config, default, beforeCommit: token => compiler.VerifyAsync(config, token));
        using (var configured = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder))
            Assert.Equal(manual, configured.Model.Cultures.Single().LinguisticMetadata.Content);
        var tmdl = await compiler.PrepareAsync(new(Measures: definitions), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(tmdl, default, beforeCommit: token => compiler.VerifyAsync(tmdl, token));
        Assert.Equal("tmdl", snapshot.Model.TemplateMeasureConfiguration!.Mode);
        using var unchanged = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        Assert.True(unchanged.Model.Tables.ContainsName(ConfigTable));
        Assert.True(unchanged.Model.Tables[ConfigTable].IsHidden);
        Assert.Contains("#table", ((TOM.MPartitionSource)unchanged.Model.Tables[ConfigTable].Partitions.Single().Source).Expression);
        Assert.Equal(manual, unchanged.Model.Cultures.Single().LinguisticMetadata.Content);
        foreach (var definition in definitions)
            Assert.Equal("\n\"" + definition.Value + "\"", unchanged.Model.Tables["Measure"].Measures[definition.Name].Expression);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PbipProjectLayout.ProjectRoot(entry), "pbi-utils", "config.json")));
        Assert.False(json.RootElement.TryGetProperty("MeasureTemplates", out _));
        Assert.True(json.RootElement.GetProperty("Custom").GetProperty("keep").GetBoolean());
        Assert.Equal("用户维护的其他配置", json.RootElement.GetProperty("Custom").GetProperty("description").GetString());
    }

    /// <summary>通过当前创建流程得到完整参数表归属, 再加入明确的用户语言元数据.</summary>
    /// <param name="generatedCalendar">是否同时包含当前生成日历.</param>
    /// <param name="bindingKind">人工表绑定、列绑定或附加自有字段.</param>
    /// <returns>测试拥有的工程入口、TMDL 定义目录和原始人工词汇 JSON.</returns>
    private async Task<(string Entry, string Folder, string Manual)> CreateExisting(bool generatedCalendar, string bindingKind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "Existing", "zh-cn", TemplateMeasureValidation.DefaultMeasures(), null,
            generatedCalendar ? new ScaffoldCalendarOptions("full", 2024, 2024) : null, null, null, MeasuresMode: "config");
        var result = await new ScaffoldModelWriter().ComposeAsync(template, request, project, project, null, default);
        var configPath = Path.Combine(project, "pbi-utils", "config.json");
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        configuration["Custom"] = new JsonObject { ["keep"] = true, ["description"] = "用户维护的其他配置" };
        await File.WriteAllTextAsync(configPath, configuration.ToJsonString());
        var folder = Path.Combine(Directory.GetDirectories(Path.Combine(project, "pbip"), "*.SemanticModel").Single(), "definition");
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        var binding = new JsonObject { ["ConceptualEntity"] = ConfigTable };
        if (bindingKind != "table") binding["ConceptualProperty"] = "name";
        var content = new JsonObject
        {
            ["Entities"] = new JsonObject { ["user_config"] = new JsonObject { ["Definition"] = new JsonObject { ["Binding"] = binding } } },
            ["ManualDescription"] = "用户维护的词汇"
        };
        if (bindingKind == "extra") content["UserExtra"] = new JsonObject { ["preserve"] = true };
        var manual = content.ToJsonString();
        database.Model.Cultures.Single().LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = manual };
        TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        return (Path.Combine(project, result.EntryFile), folder, manual);
    }

    /// <summary>仅删除本测试拥有的随机根目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
