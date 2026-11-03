// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasurePathLayoutTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板配置与 PBIP 固定平级, staging/最终根分离和离线保存不污染嵌套目录.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只操作每例随机测试根, 不打开 Desktop 或读取用户工程.</summary>
public sealed class TemplateMeasurePathLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-template-path-" + Guid.NewGuid().ToString("N"));
    private static readonly TemplateMeasureDefinition[] Definitions = [new("layout_value", "first", "Uncategorized", true, "", "")];

    /// <summary>staging 的 JSON 与 PBIP 平级, 模型 M 参数已经绑定最终根, 不把 staging 路径发布给用户.</summary>
    [Fact]
    public async Task ConfigStagingKeepsDirectoriesParallelAndBindsFinalRoot()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var staging = Path.Combine(root, "staging"); Directory.CreateDirectory(staging);
        var final = Path.Combine(root, "final");

        var result = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "Layout", "zh-cn", Definitions, null, null, null, null, MeasuresMode: "config"),
            staging, final, null, default);

        Assert.Equal("pbip/Layout.pbip", result.EntryFile);
        Assert.True(File.Exists(Path.Combine(staging, "pbi-utils", "config.json")));
        Assert.False(Directory.Exists(Path.Combine(staging, "pbip", "pbi-utils")));
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(staging, "pbip", "Layout.SemanticModel", "definition"));
        var expression = database.Model.Expressions["PbiUtilsProjectRoot"].Expression;
        Assert.Equal("\"" + final.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]", expression);
        Assert.DoesNotContain(staging, expression, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(staging, "pbi-utils", "config.json")));
        Assert.Equal("layout_value", document.RootElement.GetProperty("MeasureTemplates")[0].GetProperty("name").GetString());
    }

    /// <summary>离线切换只更新真实同级配置; 错置在 pbip 内的用户文件原字节保持.</summary>
    [Fact]
    public async Task OfflineConfigSaveWritesSiblingFileAndPreservesNestedDecoy()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var result = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "Layout", "zh-cn", Definitions, null, null, null, null), project, project, null, default);
        var nested = Path.Combine(project, "pbip", "pbi-utils", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        var original = """{"keep":1}"""u8.ToArray();
        File.WriteAllBytes(nested, original);
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(Path.Combine(project, result.EntryFile), default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        using var compiler = new ModelConfigurationCompiler();
        edits.Select(snapshot);
        var source = await compiler.ReadAssetContextAsync(snapshot, default);
        var prepared = await compiler.PrepareAsync(new(Measures: Definitions, MeasuresMode: "config", MeasureConfigurationRevision: source.ConfigurationRevision), snapshot, default);
        try
        {

            var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));

            Assert.Equal("config", saved.Model.TemplateMeasureConfiguration!.Mode);
            Assert.Equal(project, prepared.Configuration!.AssetRoot);
            Assert.Contains(prepared.Configuration.Assets, asset => asset.RelativePath == "pbi-utils/config.json");
            Assert.True(File.Exists(Path.Combine(project, "pbi-utils", "config.json")));
            Assert.Equal(original, File.ReadAllBytes(nested));
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>清理本测试随机根下的全部夹具.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
