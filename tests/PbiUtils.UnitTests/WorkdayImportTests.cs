// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\WorkdayImportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证工作日导入内容、规范化输出和最终合并预算.

using PbiUtils.Application.Calendar;
using System.Globalization;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>导入与最终覆盖统一复用应用层的有界解析器.</summary>
public sealed class WorkdayImportTests
{
    /// <summary>规范化预算按可移植JSON文本计量, 补充平面、行分隔符和反斜杠与前端相同.</summary>
    [Fact]
    public void NormalizedJsonPreservesValidUnicodeScalars()
    {
        var values = new[] { new WorkdayOverride("2024-01-01", true, "😀中\u2028\"\\uD83D") };
        var expected = "[{\"date\":\"2024-01-01\",\"isWorkday\":true,\"name\":\"😀中\u2028\\\"\\\\uD83D\"}]";
        Assert.Equal(expected, Encoding.UTF8.GetString(WorkdayImport.SerializeNormalized(values)));
    }
    /// <summary>内容导入支持BOM、声明表头、引用逗号及双引号转义, 并按日期规范化.</summary>
    [Fact]
    public void CsvAndTsvNormalizeToSameOverrides()
    {
        const string csv = "\uFEFFdate,isWorkday,name\r\n2026-02-18,false,\"节日,休息\"\r\n2026-02-17,工作日,\"名称\"\"说明\"\r\n";
        var actual = WorkdayImport.Parse(csv, "csv", 2026, 2026);
        Assert.Equal([new("2026-02-17", true, "名称\"说明"), new WorkdayOverride("2026-02-18", false, "节日,休息")], actual);
        const string tsv = "日期\t是否工作日\t名称\n2026-02-17\t是\t名称\"说明\n";
        Assert.Throws<AppException>(() => WorkdayImport.Parse(tsv, "tsv", 2026, 2026));
        Assert.Equal(actual, WorkdayImport.Parse("日期\t是否工作日\t名称\n2026-02-18\t否\t节日,休息\n2026-02-17\t1\t\"名称\"\"说明\"", "\t", 2026, 2026));
    }

    /// <summary>声明之外的布尔值不可被猜测为工作或休息.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("是", true)]
    [InlineData("否", false)]
    [InlineData("工作日", true)]
    [InlineData("休息日", false)]
    public void DeclaredBooleanTokensAreExact(string value, bool expected)
    {
        Assert.Equal(expected, Assert.Single(WorkdayImport.Parse($"date,isWorkday,name\n2026-01-01,{value},", ",", 2026, 2026)).IsWorkday);
    }

    /// <summary>错误整批拒绝并给出原始行号, 不返回可误用的部分结果.</summary>
    [Theory]
    [InlineData("2026-01-01,true,a\n2026-01-01,true,a")]
    [InlineData("2026-01-01,true,a\n2026-01-01,false,b")]
    [InlineData("2026-1-01,true,a")]
    [InlineData("2025-01-01,true,a")]
    [InlineData("2026-02-30,true,a")]
    [InlineData("2026-01-01,TRUE,a")]
    [InlineData("2026-01-01,yes,a")]
    [InlineData("2026-01-01,true,\"a\nb\"")]
    [InlineData("2026-01-01,true,\"a\"garbage")]
    [InlineData("2026-01-01,true,\"unfinished")]
    [InlineData("2026-01-01,true,a,extra")]
    [InlineData("2026-01-01,true,a\u0000b")]
    public void InvalidRowsRejectWholeImport(string rows)
    {
        var error = Assert.Throws<AppException>(() => WorkdayImport.Parse("date,isWorkday,name\n" + rows, ",", 2026, 2026));
        Assert.Equal("INVALID_WORKDAY_IMPORT", error.Code);
        Assert.Contains("行", error.Message, StringComparison.Ordinal);
    }

    /// <summary>表头、分隔符和UTF8原文预算必须先行校验.</summary>
    [Fact]
    public void HeaderDelimiterAndUtf8BudgetAreBounded()
    {
        Assert.Throws<AppException>(() => WorkdayImport.Parse("date,isWorkday,unknown\n2026-01-01,true,a", ",", 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.Parse("date;isWorkday;name", ";", 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.Parse("date,isWorkday,name\n2026-01-01,true," + new string('中', 700000), ",", 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.Parse("date,isWorkday,name\n2026-01-01,true,\uD800", ",", 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.Parse("", ",", 2026, 2026));
    }

    /// <summary>最终合并数量、规范化JSON字节及名称上限独立受约束.</summary>
    [Fact]
    public void FinalMergedOverrideBudgetUsesNormalizedUtf8Json()
    {
        var first = new DateOnly(1900, 1, 1);
        var shortRows = Enumerable.Range(0, 10000).Select(index => new WorkdayOverride(first.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true, "")).ToArray();
        Assert.Equal(10000, WorkdayImport.ValidateFinal(shortRows, 1900, 2100).Length);
        Assert.Throws<AppException>(() => WorkdayImport.ValidateFinal([.. shortRows, new("2100-01-01", false, "")], 1900, 2100));
        var chineseRows = Enumerable.Range(0, 1000).Select(index => new WorkdayOverride(first.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false, new string('中', 200))).ToArray();
        Assert.Throws<AppException>(() => WorkdayImport.ValidateFinal(chineseRows, 1900, 2100));
        var valid = WorkdayImport.ValidateFinal([new("2026-01-02", true, "  公司班  "), new("2026-01-01", false, "")], 2026, 2026);
        Assert.Equal("公司班", valid[1].Name);
        Assert.Equal("[{\"date\":\"2026-01-01\",\"isWorkday\":false,\"name\":\"\"},{\"date\":\"2026-01-02\",\"isWorkday\":true,\"name\":\"公司班\"}]", Encoding.UTF8.GetString(WorkdayImport.SerializeNormalized(valid)));
        Assert.Throws<AppException>(() => WorkdayImport.ValidateFinal([new("2026-01-01", false, new string('a', 201))], 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.ValidateFinal([new("2026-01-01", false, "\t")], 2026, 2026));
        Assert.Throws<AppException>(() => WorkdayImport.ValidateFinal([new("2026-01-01", false, ""), new("2026-01-01", true, "")], 2026, 2026));
    }
}
