// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldMeasureCategoryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 穷举文本模板度量值分类的请求边界和真实 TMDL 回读, 拒绝已移除的应用扩展.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只在测试自有目录生成新工程, 不连接或修改运行中的 Desktop 模型.</summary>
public sealed class ScaffoldMeasureCategoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-categories-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] ModelCategories = ["Uncategorized", "Address", "Place", "City", "County", "StateOrProvince", "PostalCode", "Country", "Continent", "Latitude", "Longitude", "WebUrl", "ImageUrl", "Barcode"];

    /// <summary>共享目录只暴露已核实的模型集合, 已移除的应用扩展不能映射为模型分类.</summary>
    [Fact]
    public void CatalogOnlyAcceptsModelCategories()
    {
        Assert.Equal(ModelCategories, TemplateMeasureCategories.ModelCategories);
        Assert.Equal(ModelCategories, TemplateMeasureCategories.All);
        Assert.False(TemplateMeasureCategories.IsSupported("RefreshTime"));
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => TemplateMeasureCategories.ToModelCategory("RefreshTime")).Code);
        Assert.False(TemplateMeasureCategories.IsSupported(null));
        Assert.Throws<AppException>(() => TemplateMeasureCategories.ToModelCategory("Time"));
    }

    /// <summary>本机官方 Desktop 对文本度量值提供的全部分类可经共享输入边界使用.</summary>
    [Theory]
    [InlineData("Uncategorized")]
    [InlineData("Address")]
    [InlineData("Place")]
    [InlineData("City")]
    [InlineData("County")]
    [InlineData("StateOrProvince")]
    [InlineData("PostalCode")]
    [InlineData("Country")]
    [InlineData("Continent")]
    [InlineData("Latitude")]
    [InlineData("Longitude")]
    [InlineData("WebUrl")]
    [InlineData("ImageUrl")]
    [InlineData("Barcode")]
    public void AcceptsSupportedTextMeasureCategory(string category) => ScaffoldValidation.Validate(Request() with { Measures = [new("custom", "value", category, true, "", "")] });

    /// <summary>表分类、未证实的别名和空白大小写变体不得被静默解释为模型分类.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Time")]
    [InlineData("Date")]
    [InlineData("CountryRegion")]
    [InlineData("WebURL")]
    [InlineData(" ImageUrl")]
    [InlineData("Executable")]
    [InlineData("RefreshTime")]
    public void RejectsUnrecognizedCategory(string category) => Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Measures = [new("custom", "value", category, true, "", "")] })).Code);

    /// <summary>全部模型分类由真实 TMDL 文件回读, 不对固定文本值派生时间表达式.</summary>
    [Fact]
    public async Task EveryCategoryRoundTripsWithoutBeingDowngraded()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        var definitions = ModelCategories.Select(category => new TemplateMeasureDefinition("category_" + category, "text", category, true, "", "")).ToArray();
        var request = Request() with { TemplateBaseline = template.Info.Baseline, Measures = [.. definitions, new("empty", "", "Uncategorized", true, "", "")] };
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        var measures = database.Model.Tables["Measure"].Measures;
        foreach (var category in ModelCategories)
        {
            Assert.Equal(category, measures["category_" + category].DataCategory);
            Assert.Equal("\n\"text\"", measures["category_" + category].Expression);
        }
        Assert.Equal("Uncategorized", measures["empty"].DataCategory);
        Assert.Equal("\n\"\"", measures["empty"].Expression);
    }

    /// <summary>构造固定名称和有效身份的最小请求, 不接收用户路径.</summary>
    private static ScaffoldRequest Request() => new(1, "builtin:blank", new string('a', 64), "categories", "zh-cn", null, null, null, null, null);

    /// <summary>只释放本测试自行分配的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
