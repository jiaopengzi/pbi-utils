// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormatterOracleTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用 SQLBI 实际 API 快照逐个验证生产度量及边界的布局, 测试不联网.

using PbiUtils.Application.Code.Dax;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class DaxFormatterOracleTests
{
    /// <summary>读取固定 API 快照, 每个生产度量和边界均生成独立用例, 资源缺失或损坏直接失败.</summary>
    public static TheoryData<string, string, string, DaxFormattingStyle> OracleCases()
    {
        var cases = new TheoryData<string, string, string, DaxFormattingStyle>();
        using var document = ReadOracle();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            foreach (var style in new[] { DaxFormattingStyle.Long, DaxFormattingStyle.Short })
            {
                cases.Add(
                    item.GetProperty("id").GetString() + ": " + item.GetProperty("name").GetString(),
                    item.GetProperty("expression").GetString()!,
                    item.GetProperty("expected" + style).GetString()!,
                    style
                );
            }
        }
        return cases;
    }

    /// <summary>逐个对比官方双样式正文和完整定义并验证幂等, 只还原裸词大小写, 不改布局.</summary>
    /// <param name="name">显示于测试报告中的原始度量或边界名称.</param>
    /// <param name="source">来自任务或人工边界的原始正文.</param>
    /// <param name="expected">从真实 API 响应仅去固定定义头和服务末尾换行后的正文.</param>
    /// <param name="style">官方请求与本地调用共同使用的长行或短行样式.</param>
    [Theory]
    [MemberData(nameof(OracleCases))]
    public void MatchesRecordedLayout(string name, string source, string expected, DaxFormattingStyle style)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        var preservedExpected = PreserveSourceTokens(source, expected);
        var actual = DaxFormatter.Format(source, style: style);
        Assert.Equal(preservedExpected, actual);
        Assert.Equal(actual, DaxFormatter.Format(actual, style: style));
        var definition = DaxFormatter.Format("OracleMeasure =\n" + source, style: style);
        Assert.Equal("OracleMeasure =\n" + preservedExpected, definition);
        Assert.Equal(definition, DaxFormatter.Format(definition, style: style));
    }

    /// <summary>实际度量正文只保留前导 LF, 顶级行与官方布局同样顶格, 字面量和注释内部保持原文.</summary>
    /// <param name="name">原始度量或边界名称.</param>
    /// <param name="source">任务提取的原始正文.</param>
    /// <param name="expected">API 排版正文.</param>
    /// <param name="style">选择的长行或短行样式.</param>
    [Theory]
    [MemberData(nameof(OracleCases))]
    public void MatchesRecordedLayoutThroughMeasureBody(string name, string source, string expected, DaxFormattingStyle style)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        var expectedBody = "\n" + PreserveSourceTokens(source, expected);
        var actual = DaxFormatter.FormatMeasure(source, style: style);
        Assert.Equal(expectedBody, actual);
        Assert.Equal(actual, DaxFormatter.FormatMeasure(actual, style: style));
        Assert.Equal([actual], DaxFormatter.FormatMeasures([source], style: style));
    }

    /// <summary>预测度量的真实正文经 TOM/TMDL 重开后仍顶格, 保留内部语法缩进及 Desktop 定义头换行.</summary>
    /// <param name="style">格式化和离线官方快照使用的同一布局样式.</param>
    [Theory]
    [InlineData(DaxFormattingStyle.Long)]
    [InlineData(DaxFormattingStyle.Short)]
    public void ForecastMeasureKeepsFlushLeftBodyAfterTmdlRoundTrip(DaxFormattingStyle style)
    {
        const string name = "1500_预测_预测达成_单位_";
        using var document = ReadOracle();
        var fixture = Assert.Single(document.RootElement.GetProperty("cases").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        var source = fixture.GetProperty("expression").GetString()!;
        var expected = "\n" + PreserveSourceTokens(source, fixture.GetProperty("expected" + style).GetString()!);
        using var database = new TOM.Database { Name = "forecast", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Measure" };
        database.Model.Tables.Add(table);
        table.Measures.Add(new TOM.Measure { Name = name, Expression = DaxFormatter.FormatMeasure(source, style) });
        var folder = Path.Combine(Path.GetTempPath(), "pbi-utils-dax-forecast-" + Guid.NewGuid().ToString("N"));
        try
        {
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
            using var reopened = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
            var body = reopened.Model.Tables["Measure"].Measures[name].Expression;
            Assert.Equal(expected, body);
            Assert.StartsWith(name + " =\nVAR ", name + " =" + body, StringComparison.Ordinal);
            Assert.Contains("\nVAR ", body);
            Assert.Contains("\nRETURN\n    ", body);
            Assert.DoesNotContain("\n    VAR ", body);
            Assert.Equal(body, DaxFormatter.FormatMeasure(body, style));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>基准必须包含任务中的92个唯一生产度量和两种真实成功响应, 防止漏项或空成功.</summary>
    [Fact]
    public void SnapshotCoversEveryProductionMeasureInBothStyles()
    {
        using var document = ReadOracle();
        var root = document.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var production = cases.Where(item => item.GetProperty("sourceKind").GetString() == "production").ToArray();
        Assert.Equal(92, production.Length);
        Assert.Equal(92, production.Select(item => item.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(92, root.GetProperty("productionMeasureCount").GetInt32());
        Assert.Equal(0, root.GetProperty("requestLong").GetProperty("maxLineLength").GetInt32());
        Assert.Equal(1, root.GetProperty("requestShort").GetProperty("maxLineLength").GetInt32());
        Assert.Equal(cases.Length, root.GetProperty("requestLong").GetProperty("dax").GetArrayLength());
        Assert.Equal(cases.Length, root.GetProperty("requestShort").GetProperty("dax").GetArrayLength());
        foreach (var item in cases)
        {
            foreach (var style in new[] { "Long", "Short" })
            {
                var response = item.GetProperty("response" + style);
                Assert.Empty(response.GetProperty("errors").EnumerateArray());
                Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("formatted").GetString()));
                var expected = item.GetProperty("expected" + style).GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(expected));
                var raw = response.GetProperty("formatted").GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal);
                var prefix = raw.StartsWith("OracleMeasure =\n", StringComparison.Ordinal) ? "OracleMeasure =\n" : " =\n";
                Assert.StartsWith(prefix, raw, StringComparison.Ordinal);
                Assert.EndsWith("\n", raw, StringComparison.Ordinal);
                Assert.Equal(expected, raw[prefix.Length..^1]);
                PreserveSourceTokens(item.GetProperty("expression").GetString()!, expected);
            }
        }
    }

    /// <summary>独立对照保存的原始 TMDL 全部度量名称及顺序, 避免固定数量仍漏一项补一项.</summary>
    [Fact]
    public void SnapshotNamesExactlyMatchProductionTmdl()
    {
        using var stream = typeof(DaxFormatterOracleTests).Assembly.GetManifestResourceStream("PbiUtils.Tests.DaxFormatting.ProductionTmdl")
            ?? throw new InvalidOperationException("缺少原始生产 TMDL 测试资源");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var tmdlNames = ProductionMeasureHeaders().Matches(reader.ReadToEnd())
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(92, tmdlNames.Length);
        using var document = ReadOracle();
        var recordedNames = document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("sourceKind").GetString() == "production")
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Equal(tmdlNames, recordedNames);
    }

    /// <summary>从测试程序集读取完整快照, 不依赖工作目录、任务私有文件或网络.</summary>
    private static JsonDocument ReadOracle()
    {
        using var stream = typeof(DaxFormatterOracleTests).Assembly.GetManifestResourceStream("PbiUtils.Tests.DaxFormatting.Oracle")
            ?? throw new InvalidOperationException("缺少 DAX 官方格式基准资源");
        return JsonDocument.Parse(stream);
    }

    /// <summary>保留 API 的每个空白字符, 仅把大写化裸词还原为输入词元; 字面量、注释或绑定变化均失败.</summary>
    /// <param name="source">原始 DAX 正文, 作为词元原文的独立依据.</param>
    /// <param name="formatted">实际 API 正文, 作为空白布局的独立依据.</param>
    /// <returns>具有官方布局和原词元大小写的标准文本.</returns>
    private static string PreserveSourceTokens(string source, string formatted)
    {
        var original = DaxTokens().Matches(source);
        var standard = DaxTokens().Matches(formatted);
        Assert.Equal(original.Count, standard.Count);
        var output = new StringBuilder();
        var previousEnd = 0;
        for (var index = 0; index < original.Count; index++)
        {
            var before = original[index];
            var after = standard[index];
            var unchanged = before.Value.Equals(after.Value, StringComparison.Ordinal);
            var wordCaseOnly = before.Groups["word"].Success && after.Groups["word"].Success
                && before.Value.Equals(after.Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(unchanged || wordCaseOnly, $"API 改写了第 {index + 1} 个词元, 不能用作仅排版标准");
            output.Append(formatted.AsSpan(previousEnd, after.Index - previousEnd));
            output.Append(before.Value);
            previousEnd = after.Index + after.Length;
        }
        output.Append(formatted.AsSpan(previousEnd));
        return output.ToString();
    }

    /// <summary>独立扫描测试语料词元, 把字符串、转义引用和注释视为整体, 不执行格式化逻辑.</summary>
    [GeneratedRegex("""//[^\r\n]*|--[^\r\n]*|/\*[\s\S]*?\*/|(?:[dD][tT])?"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|(?<word>[^\W\d]\w*)|&&|\|\||<=|>=|<>|==|:=|=>|\S""")]
    private static partial Regex DaxTokens();

    /// <summary>独立读取原始 TMDL 的度量定义头, 不复用采集脚本的表达式提取逻辑.</summary>
    [GeneratedRegex(@"^\t\tmeasure ([^\r\n]+?) =", RegexOptions.Multiline)]
    private static partial Regex ProductionMeasureHeaders();
}
