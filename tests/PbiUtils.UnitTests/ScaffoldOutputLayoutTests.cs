// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldOutputLayoutTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证新建工程的 PBIP 独立目录、项目命名及精简产物.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>在测试独占目录组合并通过真实读取器验证路径, 不连接或刷新用户模型.</summary>
public sealed class ScaffoldOutputLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-output-layout-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前空白模板均按项目名绑定, 数据仍在根目录, 不再产生附加清单或许可目录.</summary>
    /// <param name="kind">真实内置模板标识.</param>
    [Theory]
    [InlineData("blank")]
    public async Task CreatesNamedPbipInDedicatedFolderWithoutSidecars(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var original = template.Files.ToDictionary(file => file.RelativePath, file => file.Content.ToArray());
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        const string name = "项目 MyPowerBIProject25";
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, name, "zh-cn", null,
            new("zh-cn", 1, 1, 7, "2024-01-01", "2024-04-01", 42), null, null, null);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, name), null, default);
        Assert.Equal("pbip/" + name + ".pbip", result.EntryFile);
        var pbip = Path.Combine(stage, result.EntryFile);
        using var project = JsonDocument.Parse(await File.ReadAllBytesAsync(pbip));
        Assert.Equal(name + ".Report", project.RootElement.GetProperty("artifacts")[0].GetProperty("report").GetProperty("path").GetString());
        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(stage, "pbip", name + ".Report", "definition.pbir")));
        Assert.Equal("../" + name + ".SemanticModel", report.RootElement.GetProperty("datasetReference").GetProperty("byPath").GetProperty("path").GetString());
        Assert.True(Directory.Exists(Path.Combine(stage, "pbip", name + ".SemanticModel")));
        Assert.True(Directory.Exists(Path.Combine(stage, "data")));
        Assert.False(Directory.Exists(Path.Combine(stage, "pbip", "data")));
        foreach (var relative in new[] { "pbi-utils.scaffold.json", "README.scaffold.txt", "config/pbi-utils-configuration.json", "config/udf-lock.json" })
            Assert.Empty(Directory.EnumerateFiles(stage, Path.GetFileName(relative), SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(stage, "config", "licenses")));
        var snapshot = await new ProjectReader().ReadAsync(pbip, default);
        Assert.NotEmpty(snapshot.Model.Tables);
        foreach (var file in template.Files) Assert.Equal(original[file.RelativePath], file.Content);
    }

    /// <summary>重用当前工程并改变度量配置时更新实际 CSV 归属, 未开启的模拟段保持原始数据.</summary>
    /// <param name="sample">首次工程是否含模拟数据, 第二次始终关闭该段.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedProjectUpdatesChangedAssetHashesAndKeepsUnselectedData(bool sample)
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "generated"); Directory.CreateDirectory(source);
        var request = new ScaffoldRequest(1, builtin.Info.Id, builtin.Info.Baseline, "First", "zh-cn",
            [new("Value", "1", "Uncategorized", true, "", "")], sample ? new("zh-cn", 1, 1, 7, "2024-01-01", "2024-04-01", 42) : null, null, null, null, MeasuresMode: "config");
        await new ScaffoldModelWriter().ComposeAsync(builtin, request, source, source, null, default);
        var info = Assert.Single(await catalog.ListAsync(default), item => item.Name == "generated");
        var template = await catalog.GetAsync(info.Id, default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var next = request with
        {
            TemplateId = info.Id,
            TemplateBaseline = info.Baseline,
            ProjectName = "Second",
            Sample = null,
            Measures = [new("Value", "2", "Uncategorized", true, "", "")]
        };
        await new ScaffoldModelWriter().ComposeAsync(template, next, stage, Path.Combine(root, "Second"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(stage, "pbip", "Second.SemanticModel", "definition"));
        var ownership = ModelConfigurationOwnership.ReadRoles(database.Model);
        const string config = "pbi-utils/config.json";
        Assert.Equal(ModelConfigurationOwnership.FileHash(Path.Combine(stage, config)), ownership.Assets[config]);
        if (sample)
            foreach (var file in template.Files.Where(file => file.RelativePath.StartsWith("data/", StringComparison.Ordinal)
                && !file.RelativePath.StartsWith("data/config/", StringComparison.Ordinal)))
                Assert.Equal(file.Content, await File.ReadAllBytesAsync(Path.Combine(stage, file.RelativePath)));
    }

    /// <summary>仅删除本测试随机创建的根目录; 原始模板资源不在其内.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
