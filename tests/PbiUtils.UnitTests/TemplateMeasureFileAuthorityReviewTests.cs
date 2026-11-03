// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureFileAuthorityReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证外部有效 JSON 的当前值在切回 TMDL 保留引用项时参与冻结与物化, 不复活旧注解值.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只对随机生成的 PBIP 做实际文件事务, 不连接 Desktop 或访问用户模型.</summary>
public sealed class TemplateMeasureFileAuthorityReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-file-authority-review-" + Guid.NewGuid().ToString("N"));

    /// <summary>公共 JSON 已更新但模型注解仍旧时, 被其他度量引用的移除项在 TMDL 中使用文件最新值.</summary>
    [Fact]
    public async Task ReturningToTmdlMaterializesRetainedItemFromCurrentFile()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "Authority", "zh-cn",
                [new("Probe", "old annotation", "Uncategorized", true, "原说明", "原目录")], null, null, null, null, MeasuresMode: "config"),
            project, project, null, default);
        var folder = Path.Combine(project, "pbip", "Authority.SemanticModel", "definition");
        using (var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder))
        {
            database.Model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "[Probe]" });
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        }
        var path = Path.Combine(project, "pbi-utils", "config.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["MeasureTemplates"]![0]!["value"] = "from current file";
        json["Custom"] = true;
        File.WriteAllText(path, json.ToJsonString());
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(Path.Combine(project, composition.EntryFile), default);
        Assert.Equal("old annotation", Assert.Single(snapshot.Model.TemplateMeasureConfiguration!.Measures).Value);
        using var compiler = new ModelConfigurationCompiler();
        var source = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.Equal("from current file", Assert.Single(source.ExistingConfiguration!.Measures).Value);
        var prepared = await compiler.PrepareAsync(new(Measures: [], MeasuresMode: "tmdl", MeasureConfigurationRevision: source.ConfigurationRevision), snapshot, default, source);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        try
        {
            var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
            var retained = Assert.Single(saved.Model.Measures, measure => measure.Name == "Probe");
            Assert.Equal("\n\"from current file\"", retained.Expression);
            Assert.Equal("原说明", retained.Description);
            Assert.Equal("原目录", retained.DisplayFolder);
            Assert.Empty(saved.Model.TemplateMeasureConfiguration!.Measures);
            Assert.Equal("[Probe]", Assert.Single(saved.Model.Measures, measure => measure.Name == "Consumer").Expression);
            Assert.True(JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<bool>());
            Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["MeasureTemplates"]);
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>仅删除本测试拥有的随机工程和用户状态根.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
