// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldPeriodTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证51项声明式时间智能及安全引用、依赖和期间边界定义.

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>验证生成定义; 没有真实DAX引擎计算结论.</summary>
public sealed class ScaffoldPeriodTests
{
    /// <summary>51个原始状态全部可选并按六组分布.</summary>
    [Fact]
    public void ContainsAll51OriginalDefinitions()
    {
        Assert.Equal(51, ScaffoldTimeIntelligence.Templates.Count);
        Assert.Equal([7, 10, 10, 10, 10, 4], ScaffoldTimeIntelligence.Templates.GroupBy(t => t.Group).Select(g => g.Count()));
        Assert.Equal(51, ScaffoldTimeIntelligence.Templates.Select(t => t.Id).Distinct().Count());
        var generated = ScaffoldTimeIntelligence.Generate("销售", Options([.. ScaffoldTimeIntelligence.Templates.Select(t => t.Id)]));
        Assert.Equal(51, generated.Count);
        Assert.All(generated, item => { Assert.DoesNotContain("SELECTEDMEASURE", item.Expression); Assert.DoesNotContain("Calendar", item.Expression); });
    }

    /// <summary>选择差值率自动补入当前及比较期, 输出顺序不存在悬空引用.</summary>
    [Fact]
    public void ResolvesDependencyClosureAndKeepsPercentNumeric()
    {
        var generated = ScaffoldTimeIntelligence.Generate("销售]额", Options(["ΔPMTD%", "ΔPMTD%"]));
        Assert.Equal(["MTD", "PMTD", "ΔPMTD%"], generated.Select(g => g.TemplateId));
        var percent = generated[^1];
        DaxAssert.Contains("DIVIDE ( [20_MTD_销售]]额] - [21_PMTD_销售]]额], [21_PMTD_销售]]额], BLANK () )", percent.Expression);
        Assert.Equal("#,0.00%", percent.FormatString);
        Assert.DoesNotContain("FORMAT", percent.Expression);
        Assert.DoesNotContain("* 100", percent.Expression);
        Assert.Equal(["MTD", "PMTD"], ScaffoldTimeIntelligence.Templates.Single(t => t.Id == "ΔPMTD%").Dependencies);
    }

    /// <summary>带引号的表名与方括号列名不改变DAX语法边界.</summary>
    [Fact]
    public void EscapesAllObjectNames()
    {
        var result = ScaffoldTimeIntelligence.Generate("销售]额", new("日期'表", "日]期", "指标'表", ["销售]额"], ["AC"]));
        Assert.Contains("'日期''表'[日]]期]", result[0].Expression);
        Assert.Contains("[销售]]额]", result[0].Expression);
    }

    /// <summary>跨年周只依赖所选日期列, 不偷用模板固定周/年列.</summary>
    [Fact]
    public void WeekFormulaUsesGregorianMondayRuleWithoutExtraColumns()
    {
        var previous = ScaffoldTimeIntelligence.Generate("销售", Options(["PYWTD"])).Single();
        Assert.Contains("WEEKNUM (", previous.Expression);
        DaxAssert.Contains(", 2 )", previous.Expression);
        Assert.DoesNotContain("C10_WeekNumber", previous.Expression);
        Assert.DoesNotContain("C28_Year", previous.Expression);
        Assert.Contains("YEAR (", previous.Expression);
    }

    /// <summary>闰日/月末、季度与半年边界保留参考中的截断及整期对齐定义.</summary>
    [Theory]
    [InlineData("WTD", "WEEKDAY ( CurrentDateEnd, 3 )")]
    [InlineData("PMTD", "ENDOFMONTH")]
    [InlineData("PQTD", "ENDOFQUARTER")]
    [InlineData("PHTD", "MOD ( MONTH ( CurrentDateEnd ), 6 ) = 0")]
    [InlineData("PYTD", "DATEADD ( CurrentDateEnd, -1, YEAR )")]
    public void PeriodBoundaryDefinitionsRetained(string id, string boundary)
    {
        var measure = ScaffoldTimeIntelligence.Generate("销售", Options([id])).Single();
        DaxAssert.Contains(boundary, measure.Expression);
        Assert.Contains("'日期'[日期]", measure.Expression);
    }

    /// <summary>无效选择、空身份和越界精度必须在生成前失败.</summary>
    [Fact]
    public void RejectsInvalidOptions()
    {
        Assert.Throws<AppException>(() => ScaffoldTimeIntelligence.Generate("销售", Options(["unknown"])));
        Assert.Throws<AppException>(() => ScaffoldTimeIntelligence.Generate("", Options(["AC"])));
        Assert.Throws<AppException>(() => ScaffoldTimeIntelligence.Generate("销售", Options(["AC"]) with { NumberDigits = 9 }));
        Assert.Throws<AppException>(() => ScaffoldTimeIntelligence.Generate("销售", Options(["AC"]) with { PercentDigits = -1 }));
        Assert.Throws<AppException>(() => ScaffoldTimeIntelligence.Generate("销售", Options(["AC"]) with { BaseMeasures = ["别的度量值"] }));
    }

    /// <summary>单项选择与全量选择产生相同定义, 百分比均保留安全零分母处理.</summary>
    [Fact]
    public void EverySelectableDefinitionHasDeterministicClosedDependencies()
    {
        var all = ScaffoldTimeIntelligence.Generate("销售", Options([.. ScaffoldTimeIntelligence.Templates.Select(t => t.Id)])).ToDictionary(m => m.TemplateId);
        foreach (var template in ScaffoldTimeIntelligence.Templates)
        {
            var generated = ScaffoldTimeIntelligence.Generate("销售", Options([template.Id]));
            Assert.Equal(all[template.Id], generated[^1]);
            Assert.All(template.Dependencies, id => Assert.Contains(generated.Take(generated.Count - 1), m => m.TemplateId == id));
            if (template.Id.EndsWith('%'))
            {
                Assert.Contains("DIVIDE (", generated[^1].Expression);
                DaxAssert.Contains(", BLANK () )", generated[^1].Expression);
            }
        }
    }

    /// <summary>未选择期间不生成对象, 并且外部修改展示列表无法污染内部依赖.</summary>
    [Fact]
    public void EmptySelectionAndDefensiveMetadata()
    {
        Assert.Empty(ScaffoldTimeIntelligence.Generate("销售", Options([])));
        var copy = ScaffoldTimeIntelligence.Templates.Single(t => t.Id == "ΔPD");
        copy.Dependencies[0] = "unknown";
        Assert.Equal("AC", ScaffoldTimeIntelligence.Templates.Single(t => t.Id == "ΔPD").Dependencies[0]);
        Assert.Equal(3, ScaffoldTimeIntelligence.Generate("销售", Options(["ΔPD"])).Count);
    }

    /// <summary>名称中的占位文本是用户数据, 不允许再次插值成表引用.</summary>
    [Fact]
    public void PlaceholderLikeNamesRemainLiteral()
    {
        var item = ScaffoldTimeIntelligence.Generate("{{DATE}}]额", new("{{MEASURE}}'表", "{{TABLE}}]列", "指标", ["{{DATE}}]额"], ["AC"])).Single();
        Assert.Contains("'{{MEASURE}}''表'[{{TABLE}}]]列]", item.Expression);
        Assert.Contains("[{{DATE}}]]额]", item.Expression);
    }

    private static ScaffoldPeriodOptions Options(string[] templates) => new("日期", "日期", "指标", ["销售", "销售]额"], templates);
}
