// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarTemplateTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 日期模板输入边界、标识符转义、状态选择和比例口径的独立测试.

using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>验证生成契约, 不把字符串测试替代真实引擎计算.</summary>
public sealed class CalendarTemplateTests
{
    /// <summary>年份边界统一, 非法配置、原版状态 ID 和过大精度均拒绝.</summary>
    [Fact]
    public void RejectsUnsupportedConfiguration()
    {
        Assert.Throws<AppException>(() => CalendarTemplates.Calendar(new(1899, 2024)));
        Assert.Throws<AppException>(() => CalendarTemplates.Calendar(new(2025, 2024)));
        Assert.Throws<AppException>(() => CalendarTemplates.Calendar(new(2024, 2101)));
        Assert.Throws<AppException>(() => CalendarTemplates.Parse<CalendarOptions>("""{"startYear":2024,"StartYear":2025,"endYear":2026}"""));
        Assert.Throws<AppException>(() => CalendarTemplates.Periods("销售", new("日期", "日", "指标", ["PM"])));
        Assert.Throws<AppException>(() => CalendarTemplates.Periods("销售", new("日期", "日", "指标", ["YTD"], 9)));
    }
    /// <summary>中文与单引号/方括号名称安全引用; 只选同比率时不依赖未生成的对象.</summary>
    [Fact]
    public void EscapesIdentifiersAndKeepsPercentageNumeric()
    {
        var item = Assert.Single(CalendarTemplates.Periods("销售]额", new("日期'表", "日]期", "指标", ["YOY", "YOY"], DigitsP: 3)));
        Assert.Contains("'日期''表'[日]]期]", item.Expression);
        Assert.Contains("[销售]]额] - PreviousPeriod", item.Expression);
        Assert.DoesNotContain("FORMAT", item.Expression);
        Assert.DoesNotContain("* 100", item.Expression);
        Assert.Equal("0.000%", item.FormatString);
    }
}
