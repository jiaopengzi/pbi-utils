// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxGenerationStyleTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享生成器遵循 SQLBI 的变量命名及本地格式化约定, 不改公开业务名称.

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Code.Dax;
using PbiUtils.Application.Calendar;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar.TimeIntelligence;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>生成结果独立验证命名和排版, 不把文本断言作为真实引擎计算证据.</summary>
public sealed partial class DaxGenerationStyleTests
{
    /// <summary>普通及图像 Config 均保留公开名称, 使用 PascalCase 变量并经 Result 返回.</summary>
    [Theory]
    [InlineData("Uncategorized")]
    [InlineData("ImageUrl")]
    public void ConfigUsesPascalCaseVariablesAndFinalResult(string category)
    {
        using var database = new TOM.Database { Name = "style", CompatibilityLevel = 1606, Model = new TOM.Model() };
        ScaffoldTemplateMeasures.Apply(database.Model, [new("version", "1.0", category, true, "", "config")], "config");
        var measure = database.Model.Tables["Measure"].Measures["version"];
        Assert.Matches(@"(?m)^VAR ConfigTable\s*=\s*\n", measure.Expression);
        Assert.Matches(@"(?m)^VAR ConfigValue\s*=\s*\n", measure.Expression);
        if (category == "ImageUrl") Assert.Matches(@"(?m)^VAR Result\s*=\s*\n", measure.Expression);
        else Assert.Contains("\nVAR Result = ConfigValue\n", measure.Expression);
        Assert.EndsWith("\nRETURN\n    Result", measure.Expression, StringComparison.Ordinal);
        Assert.Equal(DaxFormatter.FormatMeasure(measure.Expression, DaxFormattingStyle.Long), measure.Expression);
        Assert.Equal("version", measure.Name);
    }

    /// <summary>全部期间保留原公开 ID 及结果名称, 内部变量具备可读 PascalCase, 输出经过同一格式化器.</summary>
    [Fact]
    public void EveryPeriodUsesFormattedPascalCaseVariables()
    {
        var options = new ScaffoldPeriodOptions("日期", "日期", "Measure", ["销售"], [.. ScaffoldTimeIntelligence.Templates.Select(item => item.Id)]);
        var generated = ScaffoldTimeIntelligence.Generate("销售", options);
        Assert.Equal(51, generated.Count);
        foreach (var measure in generated)
        {
            Assert.Equal(DaxFormatter.FormatMeasure(measure.Expression, DaxFormattingStyle.Long), measure.Expression);
            foreach (Match match in VariableDeclarationPattern().Matches(measure.Expression))
                Assert.Matches(@"^[A-Z][A-Za-z0-9]*$", match.Groups[1].Value);
            if (measure.Expression.Contains("VAR ", StringComparison.Ordinal))
                Assert.EndsWith("\nRETURN\n    Result", measure.Expression, StringComparison.Ordinal);
        }
    }

    /// <summary>独立日期和经典期间入口复用同一排版, 经典同比率保留原数值结果及公开名.</summary>
    [Fact]
    public void ClassicCalendarAndPeriodsShareLocalFormatter()
    {
        var calendar = CalendarTemplates.Calendar(new(2024, 2024));
        Assert.Equal(DaxFormatter.Format(calendar, DaxFormattingStyle.Long), calendar);
        foreach (var measure in CalendarTemplates.Periods("销售", new("日期", "日期", "Measure", ["MTD", "QTD", "YTD", "PY", "YOY"])))
        {
            Assert.Equal(DaxFormatter.FormatMeasure(measure.Expression, DaxFormattingStyle.Long), measure.Expression);
            if (measure.TemplateId.EndsWith("/YOY", StringComparison.Ordinal))
            {
                Assert.Equal("销售 同比率", measure.Name);
                Assert.EndsWith("\nRETURN\n    Result", measure.Expression, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>日期表和输出表可能占用新变量名, 用户标识符仍按原名称引用, 变量数字后缀保持确定.</summary>
    [Fact]
    public void PeriodVariablesAvoidRoleTablesBeforeInterpolatingUserNames()
    {
        var options = new ScaffoldPeriodOptions("currentdateend", "Date", "RESULT", ["CurrentDateEnd]额"], ["AC"]);
        var item = Assert.Single(ScaffoldTimeIntelligence.Generate("CurrentDateEnd]额", options,
            ["CurrentDateEnd1", "Result1"]));
        Assert.Contains("VAR CurrentDateEnd2 =", item.Expression);
        Assert.Contains("VAR Result2 =", item.Expression);
        Assert.Contains("'currentdateend'[Date]", item.Expression);
        Assert.Contains("[CurrentDateEnd]]额]", item.Expression);
        Assert.EndsWith("\nRETURN\n    Result2", item.Expression, StringComparison.Ordinal);
        Assert.Equal(item, Assert.Single(ScaffoldTimeIntelligence.Generate("CurrentDateEnd]额", options,
            ["Result1", "CurrentDateEnd1"])));
    }

    /// <summary>完整模型额外表名参与期间生成及配置回读, 不能因后续重建公式使用另一组变量名而丢失方案.</summary>
    [Fact]
    public void ModelPeriodWriterAndConfigurationReadShareCompleteTableNames()
    {
        using var database = new TOM.Database { Name = "periods", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var date = new TOM.Table { Name = "Dates" };
        date.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime });
        var measures = new TOM.Table { Name = "Measure" };
        measures.Measures.Add(new TOM.Measure { Name = "Sales", Expression = "1" });
        database.Model.Tables.Add(date);
        database.Model.Tables.Add(measures);
        foreach (var name in new[] { "CurrentDateEnd", "CurrentDateEnd1", "Result", "Result1" })
            database.Model.Tables.Add(new TOM.Table { Name = name });
        var options = new ScaffoldPeriodOptions("Dates", "Date", "Measure", ["Sales"], ["AC"]);

        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);

        var expression = measures.Measures["00_AC_Sales"].Expression;
        Assert.Contains("VAR CurrentDateEnd2 =", expression);
        Assert.EndsWith("\nRETURN\n    Result2", expression, StringComparison.Ordinal);
        Assert.NotNull(ScaffoldPeriodConfiguration.Read(database.Model));
        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
        Assert.Equal(expression, measures.Measures["00_AC_Sales"].Expression);
    }

    /// <summary>经典同比率新增 Result 变量也须避开完整模型表名, 原基础指标及日期引用保持不变.</summary>
    [Fact]
    public void ClassicModelWriterAvoidsEveryExistingTableName()
    {
        using var database = new TOM.Database { Name = "classic", CompatibilityLevel = 1606, Model = new TOM.Model() };
        CalendarModelWriter.Calendar(database.Model, "Dates", new(2024, 2024));
        var measures = new TOM.Table { Name = "Measure" };
        measures.Measures.Add(new TOM.Measure { Name = "Sales", Expression = "1" });
        database.Model.Tables.Add(measures);
        foreach (var name in new[] { "PreviousPeriod", "PreviousPeriod1", "Result", "Result1" })
            database.Model.Tables.Add(new TOM.Table { Name = name });

        CalendarModelWriter.Periods(database.Model, "Sales", new("Dates", "Date", "Measure", ["YOY"]));

        var expression = measures.Measures["Sales 同比率"].Expression;
        Assert.Contains("VAR PreviousPeriod2 =", expression);
        Assert.Contains("VAR Result2 =", expression);
        DaxAssert.Contains("DIVIDE ( [Sales] - PreviousPeriod2, PreviousPeriod2 )", expression);
        Assert.Contains("'Dates'[Date]", expression);
    }

    /// <summary>数字后缀不覆盖另一个已声明变量, 字符串、表名、列名及注释中的同名文字逐字保留.</summary>
    [Fact]
    public void TrustedTemplateRenamingProtectsOtherVariablesAndLiteralContent()
    {
        const string source = """
            // VAR Result = 用户说明
            VAR Result = "Result and VAR Result1"
            VAR Result1 = Result & 'Result'[Result]
            /* VAR Result2 = 注释 */
            RETURN Result1
            """;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Result", "Result2" };
        var renamed = DaxGeneratedVariables.AvoidTableNames(source, reserved);
        Assert.Contains("VAR Result3 = \"Result and VAR Result1\"", renamed);
        Assert.Contains("VAR Result1 = Result3 & 'Result'[Result]", renamed);
        Assert.Contains("// VAR Result = 用户说明", renamed);
        Assert.Contains("/* VAR Result2 = 注释 */", renamed);
        Assert.EndsWith("RETURN Result1", renamed, StringComparison.Ordinal);
    }

    /// <summary>提取生成表达式中的变量声明, 用于独立核验 PascalCase 命名.</summary>
    [GeneratedRegex(@"\bVAR\s+(\w+)")]
    private static partial Regex VariableDeclarationPattern();
}
