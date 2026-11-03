// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureTextNormalizationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板度量常量的 LF 输出与 Config 查值边界, 保留草稿和 JSON 原文.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Application.TemplateMeasures;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实引擎数值另行验收, 本类验证新生成定义及业务 JSON 原值边界.</summary>
public sealed class TemplateMeasureTextNormalizationTests
{
    /// <summary>字符串常量遵循 TMDL 实际保存的 LF 约定, 引号和 Unicode 空白保持原意义.</summary>
    [Theory]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\r\nb", "\"a\nb\"")]
    [InlineData("a\rb", "\"a\nb\"")]
    [InlineData("\r\n\t\u3000<svg/>", "\"\n\t\u3000<svg/>\"")]
    [InlineData("", "\"\"")]
    [InlineData("中文\"引号\"\r\nnext\rlast", "\"中文\"\"引号\"\"\nnext\nlast\"")]
    public void LiteralUsesCanonicalLineFeeds(string value, string expected) => Assert.Equal(expected, ScaffoldTemplateMeasures.Literal(value));

    /// <summary>Config 通过完整参数行拼接原值, 不给生成表达式或业务 JSON 追加换行替换规则.</summary>
    [Theory]
    [InlineData("Uncategorized")]
    [InlineData("ImageUrl")]
    public void ConfigExpressionPreservesSourceTextWithoutSubstitution(string category)
    {
        using var database = new TOM.Database { Name = "text", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var definition = new TemplateMeasureDefinition("text", "\r\n\t\u3000<svg title=\"quote\"/>\rnext", category, true, "", "");
        ScaffoldTemplateMeasures.Apply(database.Model, [definition], mode: "config");
        var expression = database.Model.Tables["Measure"].Measures[definition.Name].Expression;
        DaxAssert.Contains("VAR ConfigTable = FILTER(", expression);
        DaxAssert.Contains("VAR ConfigValue = CONCATENATEX(ConfigTable, 'PbiUtilsTemplateMeasures'[value])", expression);
        Assert.DoesNotContain("SUBSTITUTE", expression);
        Assert.DoesNotContain("UNICHAR", expression);
        Assert.DoesNotContain("LOOKUPVALUE", expression);
        using var json = JsonDocument.Parse(TemplateMeasureConfigurationStorage.JsonBytes(null, "config", [definition])!);
        Assert.Equal(definition.Value, json.RootElement.GetProperty("MeasureTemplates")[0].GetProperty("value").GetString());
        Assert.Equal("\r\n\t\u3000<svg title=\"quote\"/>\rnext", definition.Value);
    }

    /// <summary>SVG 只补一次前缀, LF/CRLF/单 CR 的前导空白产生相同生成字符串.</summary>
    [Theory]
    [InlineData("\n\t\u3000<svg title=\"q\"/>")]
    [InlineData("\r\n\t\u3000<svg title=\"q\"/>")]
    [InlineData("\r\t\u3000<svg title=\"q\"/>")]
    public void SvgLiteralUsesSamePrefixAndCanonicalValue(string value)
    {
        using var database = new TOM.Database { Name = "image", CompatibilityLevel = 1606, Model = new TOM.Model() };
        ScaffoldTemplateMeasures.Apply(database.Model, [new("image", value, "ImageUrl", true, "", "")]);
        Assert.Equal("\n\"data:image/svg+xml;utf8,\n\t\u3000<svg title=\"\"q\"\"/>\"", database.Model.Tables["Measure"].Measures["image"].Expression);
    }
}
