// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldCalendarMTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 检查自包含M编译边界与字段映射, 真实M执行由隔离Desktop验收覆盖.

using PbiUtils.Application.Code.PowerQuery;
using PbiUtils.Application.Calendar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>编译契约测试不冒充真实Power Query引擎执行.</summary>
public sealed class ScaffoldCalendarMTests
{
    /// <summary>宿主必须提前拒绝M独立函数不接受的不可见别名.</summary>
    [Fact]
    public void BomAliasIsRejectedBeforePublishing()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates"]);
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("\uFEFFDates", "Dates")]));
    }
    /// <summary>百年输出仍是短函数, 仅315字节农历年规则且没有全日期行或外部读取.</summary>
    [Fact]
    public void CenturyCompilesToSelfContainedFunction()
    {
        var options = new ScaffoldCalendarOptions("full", 2000, 2099);
        var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
        var code = program.FunctionExpression;
        Assert.True((long)Encoding.UTF8.GetByteCount(code) + Encoding.UTF8.GetByteCount(program.InvocationExpression) <= PowerQueryFormatter.MaximumOutputBytes);
        var singleYear = options with { EndYear = options.StartYear };
        Assert.Equal(ScaffoldCalendarM.Compile(singleYear, ScaffoldCalendarFields.Resolve(singleYear)).FunctionExpression, code);
        Assert.Contains("List.Dates(First", code, StringComparison.Ordinal);
        Assert.Contains("type date", code, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "File.Contents", "Folder.", "Web.Contents", "DateTime.LocalNow", "DateTimeZone.", "#table(", "__LUNAR__", "__FIELDS__", "__TYPES__" })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        var resource = ScaffoldCalendar.GetLunarYearData();
        Assert.Equal(315, resource.Length);
        Assert.Equal("93E54D4A49576BEF30BA1C595013DFFC0A7465BF46B995DD3372E4C2B48F3432", Convert.ToHexString(SHA256.HashData(resource)));
        Assert.Contains(Convert.ToBase64String(resource), code, StringComparison.Ordinal);
        resource[0] = 0;
        Assert.Equal((byte)'L', ScaffoldCalendar.GetLunarYearData()[0]);
    }

    /// <summary>输出绑定保留多别名, 目录排序稳定, 不把名称内容解释成M.</summary>
    [Fact]
    public void ExistingAliasesAndTextEscapesArePreserved()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates", "Status"], NumberedColumns: false,
            Overrides: [new("2024-01-01", false, "引号\"与#(lf)")]);
        var code = ScaffoldCalendarM.Compile(options, [new("状态", "Status"), new("日期\"#(lf)", "Dates"), new("旧日期", "Dates")]).FunctionExpression;
        Assert.Contains("\"日期\"\"#(#)(lf)\"", code, StringComparison.Ordinal);
        Assert.Contains("\"旧日期\"", code, StringComparison.Ordinal);
        Assert.Contains("引号\"\"与#(#)(lf)", ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options)).InvocationExpression, StringComparison.Ordinal);
        Assert.True(code.IndexOf("\"旧日期\"", StringComparison.Ordinal) < code.IndexOf("\"状态\"", StringComparison.Ordinal));
        var values = ScaffoldCalendar.GenerateFields(options, ["Dates", "Dates", "Status"]);
        Assert.Equal(["2024-01-01", "2024-01-01", "假期"], values.Rows[0]);
    }

    /// <summary>不同字段输入顺序、工作周顺序、覆盖顺序与系统区域不改变编译结果.</summary>
    [Fact]
    public void EquivalentParametersCompileDeterministically()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Status", "Dates", "Year"], Workdays: [6, 0],
            Overrides: [new("2024-03-01", true, ""), new("2024-01-01", false, "假")]);
        var first = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var reordered = options with { Fields = ["Dates", "Year", "Status"], Workdays = [0, 6], Overrides = [.. options.Overrides!.Reverse()] };
            Assert.Equal(first, ScaffoldCalendarM.Compile(reordered, ScaffoldCalendarFields.Resolve(reordered)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>实际输出中的农历依赖独立校验, 不因用户只选公历而忽略锁定的旧农历列.</summary>
    [Fact]
    public void ActualMappingEnforcesLunarBoundsAndNames()
    {
        var options = new ScaffoldCalendarOptions("full", 1900, 1900, Fields: ["Dates"]);
        Assert.Contains("PbiUtilsCalendar(1900, 1900", ScaffoldCalendarM.Compile(options, [new("Dates", "Dates")]).InvocationExpression, StringComparison.Ordinal);
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("Dates", "Dates"), new("Lunar", "LunarYear")]));
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("Dates", "Dates"), new("dates", "Day")]));
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("非法\n列", "Dates")]));
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("未知", "Bad")]));
    }

    /// <summary>自定义工作周下补班、假期与无覆盖休息日严格区分.</summary>
    [Fact]
    public void CustomWorkweekKeepsExplicitOverrideMeanings()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates", "Status"], NumberedColumns: false, Workdays: [6],
            Overrides: [new("2024-01-01", true, ""), new("2024-01-07", false, ""), new("2024-01-08", false, "")]);
        var data = ScaffoldCalendar.Generate(options);
        Assert.Equal("补班", data.Rows[0][1]);
        Assert.Equal("休息日", data.Rows[1][1]);
        Assert.Equal("假期", data.Rows[6][1]);
        Assert.Equal("假期", data.Rows[7][1]);
        Assert.Equal("工作日", data.Rows[13][1]);
    }

    /// <summary>旧已冻结选项中的名称空格不因引入批量导入而被隐式修改.</summary>
    [Fact]
    public void ExistingOverrideNamesKeepOriginalText()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Overrides: [new("2024-01-01", false, " 公司假期 ")]);
        Assert.Equal(" 公司假期 ", ScaffoldCalendar.Generate(options).Rows[0][55]);
        Assert.Contains(" 公司假期 ", ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options)).InvocationExpression, StringComparison.Ordinal);
    }
}
