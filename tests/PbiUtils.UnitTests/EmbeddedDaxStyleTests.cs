// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\EmbeddedDaxStyleTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前随包示例的全部 DAX 表达式使用共享排版和变量命名约定.

using PbiUtils.Application.Code.Dax;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>直接回读发布所嵌入的示例 TMDL 来源, 枚举其实际 DAX 槽位, 不执行数据源.</summary>
public sealed partial class EmbeddedDaxStyleTests
{
    /// <summary>固定迁移前所有活动 DAX 字符串的多重集合, 保护多行 SVG、业务键值与格式串的逐字符语义.</summary>
    [Theory]
    [InlineData("SampleData/Assets/pbip/demo.SemanticModel", "1a320d31edc51d99d595d229a4bb92582b3bd20350c5735fc2d6d466a74e59ad")]
    public void FormattingPreservesEveryBusinessLiteral(string model, string expected)
    {
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(RepositoryRoot(), "src/PbiUtils.Infrastructure", model, "definition"));
        var literals = ReadExpressions(database.Model)
            .SelectMany(slot => BusinessLiteralPattern().Matches(WithoutComments(slot.Expression.Replace("\r\n", "\n", StringComparison.Ordinal))))
            .Select(match => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(match.Value))))
            .Order(StringComparer.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', literals))));
        Assert.Equal(expected, hash);
    }

    /// <summary>Calendar 与内置函数同名, 即使仅有字母也必须保留表标识符引号.</summary>
    [Fact]
    public void ReservedTableNameRetainsRequiredQuotes()
    {
        using var database = new TOM.Database { Name = "quoted-table", CompatibilityLevel = 1606, Model = new TOM.Model() };
        database.Model.Tables.Add(new TOM.Table { Name = "Calendar" });
        var mirror = new TOM.Table { Name = "Mirror" };
        mirror.Partitions.Add(new TOM.Partition { Name = "Mirror", Source = new TOM.CalculatedPartitionSource { Expression = DaxFormatter.Format("'Calendar'") } });
        database.Model.Tables.Add(mirror);
        using var reopened = TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(database));
        var partition = Assert.Single(reopened.Model.Tables["Mirror"].Partitions);
        Assert.Equal("'Calendar'", Assert.IsType<TOM.CalculatedPartitionSource>(partition.Source).Expression);
    }

    /// <summary>真实度量正文持久化首个换行与顶格正文, 保证 Desktop 的定义头后不把 VAR 挤在同一行.</summary>
    [Theory]
    [InlineData("SampleData/Assets/pbip/demo.SemanticModel")]
    public void MeasureBodiesPersistDefinitionHeaderLineBreak(string model)
    {
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(RepositoryRoot(), "src/PbiUtils.Infrastructure", model, "definition"));
        var measures = ReadExpressions(database.Model).Where(slot => slot.IsMeasure).ToArray();
        Assert.NotEmpty(measures);
        foreach (var (rawExpression, _) in measures)
        {
            var expression = rawExpression.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith("\n", expression, StringComparison.Ordinal);
            Assert.False(char.IsWhiteSpace(expression[1]));
            Assert.Equal(expression, DaxFormatter.FormatMeasure(expression, DaxFormattingStyle.Long));
        }
    }

    /// <summary>内置表达式与共享格式化器幂等一致, 活动变量使用 PascalCase, 最终均返回 Result.</summary>
    [Theory]
    [InlineData("SampleData/Assets/pbip/demo.SemanticModel")]
    public void EmbeddedExpressionsFollowSharedStyle(string model)
    {
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(RepositoryRoot(), "src/PbiUtils.Infrastructure", model, "definition"));
        var expressions = ReadExpressions(database.Model).ToArray();
        Assert.NotEmpty(expressions);
        foreach (var (rawExpression, isMeasure) in expressions)
        {
            var normalized = rawExpression.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(normalized, isMeasure ? DaxFormatter.FormatMeasure(normalized, DaxFormattingStyle.Long) : DaxFormatter.Format(normalized, DaxFormattingStyle.Long));
            var active = WithoutComments(normalized);
            var variables = VariableDeclarationPattern().Matches(active);
            foreach (Match variable in variables)
                Assert.Matches("^[A-Z][A-Za-z0-9]*$", variable.Groups[1].Value);
            if (variables.Count > 0)
                Assert.Matches(@"\bRETURN\s+Result\s*$", active);
        }
    }

    /// <summary>枚举 TOM 中活动 DAX 槽位; M 分区、模型注解、报表与注释均不作为 DAX 重写.</summary>
    private static IEnumerable<(string Expression, bool IsMeasure)> ReadExpressions(TOM.Model model)
    {
        foreach (var table in model.Tables)
        {
            foreach (var measure in table.Measures)
            {
                yield return (measure.Expression, true);
                if (measure.FormatStringDefinition is not null) yield return (measure.FormatStringDefinition.Expression, false);
            }
            foreach (var column in table.Columns.OfType<TOM.CalculatedColumn>()) yield return (column.Expression, false);
            foreach (var partition in table.Partitions)
                if (partition.Source is TOM.CalculatedPartitionSource calculated) yield return (calculated.Expression, false);
            if (table.CalculationGroup is null) continue;
            foreach (var item in table.CalculationGroup.CalculationItems)
            {
                yield return (item.Expression, false);
                if (item.FormatStringDefinition is not null) yield return (item.FormatStringDefinition.Expression, false);
            }
        }
        foreach (var role in model.Roles)
            foreach (var permission in role.TablePermissions)
                if (!string.IsNullOrWhiteSpace(permission.FilterExpression)) yield return (permission.FilterExpression, false);
    }

    /// <summary>只移除注释, 跳过字符串和转义标识符中的注释样式文字, 避免检查停用的原作者示例.</summary>
    private static string WithoutComments(string expression) => CommentOrLiteralPattern().Replace(expression, match => match.Value.StartsWith("//", StringComparison.Ordinal) || match.Value.StartsWith("--", StringComparison.Ordinal)
            || match.Value.StartsWith("/*", StringComparison.Ordinal) ? " " : match.Value);

    /// <summary>由测试输出位置向上定位真实仓库; 缺少根标记时明确失败, 不读取其他工程.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("未找到 pbi-utils 仓库根目录");
    }

    /// <summary>匹配注释及需要保护的字面量, 供注释剥离时原样保留字符串和标识符.</summary>
    [GeneratedRegex("\"(?:\"\"|[^\"])*\"|'(?:''|[^'])*'|\\[(?:\\]\\]|[^\\]])*\\]|//[^\\r\\n]*|--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/")]
    private static partial Regex CommentOrLiteralPattern();

    /// <summary>提取业务文本字面量, 用于比较迁移前后的完整字符串多重集合.</summary>
    [GeneratedRegex("\"(?:\"\"|[^\"])*\"")]
    private static partial Regex BusinessLiteralPattern();

    /// <summary>提取活动 VAR 声明, 不把注释中的停用示例当作生成变量.</summary>
    [GeneratedRegex(@"\bVAR\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex VariableDeclarationPattern();
}
