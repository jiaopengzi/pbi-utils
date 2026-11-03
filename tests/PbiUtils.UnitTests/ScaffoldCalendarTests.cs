// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldCalendarTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证离线日期字段、农历年界与逐日工作日覆盖.

using PbiUtils.Application.Calendar;
using System.Globalization;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>以已知日期验证字段语义, 不联网取得日期信息.</summary>
public sealed class ScaffoldCalendarTests
{
    /// <summary>显式子集使用固定原序号且不保留未选字段.</summary>
    [Fact]
    public void ExplicitSubsetUsesStableNumbers()
    {
        var options = JsonSerializer.Deserialize<ScaffoldCalendarOptions>("""{"Kind":"full","StartYear":2024,"EndYear":2024,"Fields":["Status","Dates","Month"],"NumberedColumns":true}""")!;
        var data = ScaffoldCalendar.Generate(options);
        Assert.Equal(["C01_Dates", "C13_Month", "C63_Status"], data.Columns);
        Assert.Equal(["2024-01-01", "1", "工作日"], data.Rows[0]);
    }

    /// <summary>按自定义工作周解释休息与补班, 不固定周末.</summary>
    [Fact]
    public void ExplicitWorkweekControlsStatus()
    {
        var options = JsonSerializer.Deserialize<ScaffoldCalendarOptions>("""{"Kind":"full","StartYear":2024,"EndYear":2024,"Fields":["Dates","Status"],"NumberedColumns":false,"Workdays":[6]}""")!;
        var data = ScaffoldCalendar.Generate(options);
        Assert.Equal("休息日", data.Rows[0][^1]);
        Assert.Equal("工作日", data.Rows[6][^1]);
    }

    /// <summary>无农历字段的full请求允许公历边界, 不读取农历范围外数据.</summary>
    [Theory]
    [InlineData(1900)]
    [InlineData(2100)]
    public void ExplicitGregorianSubsetAllowsEdges(int year)
    {
        var options = JsonSerializer.Deserialize<ScaffoldCalendarOptions>($$"""{"Kind":"full","StartYear":{{year}},"EndYear":{{year}},"Fields":["Dates","Status"]}""")!;
        Assert.Equal(365, ScaffoldCalendar.Generate(options).Rows.Count);
    }

    /// <summary>新字段方案必须包含日期且拒绝未知、重复ID与非法每周集合.</summary>
    [Theory]
    [InlineData("[\"Status\"]", "[0,1]")]
    [InlineData("[\"Dates\",\"Bogus\"]", "[0,1]")]
    [InlineData("[\"Dates\",\"Dates\"]", "[0,1]")]
    [InlineData("[\"Dates\"]", "[0,0]")]
    [InlineData("[\"Dates\"]", "[7]")]
    public void RejectsInvalidExplicitConfiguration(string fields, string workdays)
    {
        var options = JsonSerializer.Deserialize<ScaffoldCalendarOptions>($$"""{"Kind":"full","StartYear":2024,"EndYear":2024,"Fields":{{fields}},"Workdays":{{workdays}}}""")!;
        Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(options));
    }

    /// <summary>冻结逐日v1独立预言机逐日核验新年度压缩资源.</summary>
    [Fact]
    public void FrozenYearRulesMatchAll36525IndependentLunarTuples()
    {
        using var stream = typeof(ScaffoldCalendarTests).Assembly.GetManifestResourceStream("PbiUtils.Tests.Scaffolding.Lunar.v1")!;
        var oracle = new byte[36525 * 4];
        stream.ReadExactly(oracle);
        var data = ScaffoldCalendar.GenerateFields(new("full", 2000, 2099), ["Dates", "LunarYear", "LunarMonth", "LunarDate", "LunarMonthCN"]);
        for (var day = 0; day < 36525; day++)
        {
            Assert.Equal((oracle[day * 4] + 256 * oracle[day * 4 + 1]).ToString(CultureInfo.InvariantCulture), data.Rows[day][1]);
            Assert.Equal((oracle[day * 4 + 2] & 127).ToString(CultureInfo.InvariantCulture), data.Rows[day][2]);
            Assert.Equal(oracle[day * 4 + 3].ToString(CultureInfo.InvariantCulture), data.Rows[day][3]);
            Assert.Equal((oracle[day * 4 + 2] & 128) != 0, data.Rows[day][4].StartsWith('闰'));
        }
    }

    /// <summary>简单表保留参考五种显示语义和完整闰年.</summary>
    [Fact]
    public void SimpleKeepsFiveReferenceFields()
    {
        var data = ScaffoldCalendar.Generate(new("simple", 2024, 2024));
        Assert.Equal(["C01_Dates", "C02_Day", "C14_MonthM", "C18_YearMonthM", "C29_FY00"], data.Columns);
        Assert.Equal(["DateTime", "Int64", "String", "String", "String"], data.DataTypes);
        Assert.Equal("C01_Dates", data.DateColumn);
        Assert.Equal(366, data.Rows.Count);
        Assert.Equal(["2024-02-29", "29", "M02", "2024M02", "FY24"], data.Rows[59]);
    }

    /// <summary>默认快捷字段同样尊重关闭编号, 不依赖是否显式提供 Fields.</summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("full")]
    public void ShortcutFieldsUseCurrentNamingChoice(string kind)
    {
        var options = new ScaffoldCalendarOptions(kind, 2024, 2024, NumberedColumns: false);
        var expected = kind == "simple" ? ScaffoldCalendarFields.Simple : ScaffoldCalendarFields.All.Select(field => field.Id).ToArray();
        Assert.Equal(expected, ScaffoldCalendarFields.Resolve(options).Select(column => column.Name));
        Assert.Equal("Dates", ScaffoldCalendar.Generate(options).DateColumn);
    }

    /// <summary>完整表每一天都存在且字段类型与值数一致.</summary>
    [Fact]
    public void FullCoversCenturyWith63TypedFields()
    {
        var data = ScaffoldCalendar.Generate(new("full", 2000, 2099));
        Assert.Equal(36525, data.Rows.Count);
        Assert.Equal(63, data.Columns.Length);
        Assert.Equal(63, data.Columns.Distinct().Count());
        Assert.Equal(63, data.DataTypes.Length);
        Assert.Equal("2000-01-01", data.Rows[0][0]);
        Assert.Equal("2099-12-31", data.Rows[^1][0]);
        Assert.Equal("C01_Dates", data.DateColumn);
        Assert.All(data.Rows, row => Assert.Equal(63, row.Length));
        for (var i = 0; i < data.Rows.Count; i++)
        {
            Assert.Equal(new DateOnly(2000, 1, 1).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data.Rows[i][0]);
            for (var c = 0; c < data.Columns.Length; c++)
            {
                if (data.DataTypes[c] == "Int64") Assert.True(long.TryParse(data.Rows[i][c], CultureInfo.InvariantCulture, out _));
                if (data.DataTypes[c] == "Double") Assert.True(double.TryParse(data.Rows[i][c], CultureInfo.InvariantCulture, out _));
            }
        }
    }

    /// <summary>春节、农历上年及闰月均由固定资源提供.</summary>
    [Theory]
    [InlineData("2000-01-01", "1999", "11", "25", "十一月", "廿五", "兔")]
    [InlineData("2024-02-09", "2023", "12", "30", "腊月", "三十", "兔")]
    [InlineData("2024-02-10", "2024", "1", "1", "正月", "初一", "龙")]
    [InlineData("2023-03-22", "2023", "2", "1", "闰二月", "初一", "兔")]
    [InlineData("2023-04-20", "2023", "3", "1", "三月", "初一", "兔")]
    public void LunarKnownDates(string date, string year, string month, string day, string monthCn, string dayCn, string animal)
    {
        var values = Row(date);
        Assert.Equal(year, values["C62_LunarYear"]);
        Assert.Equal(month, values["C61_LunarMonth"]);
        Assert.Equal(day, values["C60_LunarDate"]);
        Assert.Equal(monthCn, values["C59_LunarMonthCN"]);
        Assert.Equal(dayCn, values["C58_LunarDateCN"]);
        Assert.Equal(animal, values["C57_Animal"]);
    }

    /// <summary>跨年周按公历首周计数而非ISO, 各周期边界和比例真实一致.</summary>
    [Theory]
    [InlineData("2021-01-01", "1", "2020-12-28", "2021-01-03", "2021-01-01", "2021-06-30", "181")]
    [InlineData("2024-06-30", "26", "2024-06-24", "2024-06-30", "2024-01-01", "2024-06-30", "182")]
    [InlineData("2024-07-01", "27", "2024-07-01", "2024-07-07", "2024-07-01", "2024-12-31", "184")]
    public void MondayAndPeriodBoundaries(string date, string week, string weekStart, string weekEnd, string halfStart, string halfEnd, string halfDays)
    {
        var values = Row(date);
        Assert.Equal(week, values["C10_WeekNumber"]);
        Assert.Equal(weekStart, values["C31_StartOfWeek"]);
        Assert.Equal(weekEnd, values["C36_EndOfWeek"]);
        Assert.Equal(halfStart, values["C34_StartOfHalfYear"]);
        Assert.Equal(halfEnd, values["C39_EndOfHalfYear"]);
        Assert.Equal(halfDays, values["C49_DaysOfHalfYear"]);
        Assert.Equal(double.Parse(values["C44_DayOfHalfYear"], CultureInfo.InvariantCulture) / double.Parse(halfDays, CultureInfo.InvariantCulture), double.Parse(values["C54_ProgressOfHalfYear"], CultureInfo.InvariantCulture));
    }

    /// <summary>工作日覆盖优先于星期规则, 农历节日不冒充官方假期.</summary>
    [Fact]
    public void WorkdayOverridesTakePriority()
    {
        var data = ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides:
            [new("2024-02-10", true, "业务补班"), new("2024-02-12", false, "公司休假")]));
        var saturday = Values(data, "2024-02-10");
        var monday = Values(data, "2024-02-12");
        Assert.Equal("补班", saturday["C63_Status"]);
        Assert.Equal("业务补班", saturday["C56_Holiday"]);
        Assert.Equal("假期", monday["C63_Status"]);
        Assert.Equal("公司休假", monday["C56_Holiday"]);
        Assert.Equal("工作日", Row("2024-02-12")["C63_Status"]);
        Assert.Equal("周末", Row("2024-02-10")["C63_Status"]);
    }

    /// <summary>错误类型、年份及覆盖范围拒绝, 不静默忽略输入.</summary>
    [Theory]
    [InlineData("full", 1999, 2000)]
    [InlineData("full", 2099, 2100)]
    [InlineData("simple", 1899, 2000)]
    [InlineData("simple", 2000, 2101)]
    [InlineData("simple", 2025, 2024)]
    [InlineData("other", 2024, 2024)]
    public void RejectsInvalidRanges(string kind, int start, int end) => Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new(kind, start, end)));

    /// <summary>覆盖必须使用规范日期并唯一绑定到生成范围.</summary>
    [Theory]
    [InlineData("2024-02-30")]
    [InlineData("2024-2-01")]
    [InlineData("2023-12-31")]
    public void RejectsInvalidOverrideDates(string date) => Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides: [new(date, true, "名称")])));

    /// <summary>重复覆盖无论值是否相同均拒绝.</summary>
    [Fact]
    public void RejectsDuplicateOverrides() => Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides: [new("2024-01-01", true, ""), new("2024-01-01", false, "")])));

    /// <summary>完整字段在历史41列重复编号处依语义排列, 不按错误编号覆盖.</summary>
    [Fact]
    public void EveryReferenceFieldHasCorrectSemanticValue()
    {
        var data = ScaffoldCalendar.Generate(new("full", 2024, 2024));
        var row = data.Rows.Single(r => r[0] == "2024-02-29");
        var expected = new[]
        {
            "2024-02-29", "29", "3", "四", "星期四", "T", "Thu", "Thursday", "6479", "9", "W09", "2024W09", "2", "M02", "二月", "Feb", "February", "2024M02", "Feb-24", "202402",
            "1", "Q1", "2024Q1", "202401", "上半年", "H1", "2024H1", "2024", "FY24", "FY", "2024-02-26", "2024-02-01", "2024-01-01", "2024-01-01", "2024-01-01",
            "2024-03-03", "2024-02-29", "2024-03-31", "2024-06-30", "2024-12-31", "4", "29", "60", "60", "60", "7", "29", "91", "182", "366",
            (4d / 7).ToString("R", CultureInfo.InvariantCulture), "1", (60d / 91).ToString("R", CultureInfo.InvariantCulture), (60d / 182).ToString("R", CultureInfo.InvariantCulture), (60d / 366).ToString("R", CultureInfo.InvariantCulture),
            "", "龙", "二十", "正月", "20", "1", "2024", "工作日"
        };
        Assert.Equal(expected, row);
        Assert.Equal("C41_DayOfWeek", data.Columns[40]);
        Assert.Equal("C45_DayOfYear", data.Columns[44]);
    }

    /// <summary>简单表保留1900和2100完整公历年, 不能套用农历范围限制.</summary>
    [Theory]
    [InlineData(1900)]
    [InlineData(2100)]
    public void SimpleCalendarAllowsGregorianEdges(int year)
    {
        var data = ScaffoldCalendar.Generate(new("simple", year, year));
        Assert.Equal(365, data.Rows.Count);
        Assert.Equal($"{year}-01-01", data.Rows[0][0]);
        Assert.Equal($"{year}-12-31", data.Rows[^1][0]);
    }

    /// <summary>固定数据必须脱离调用线程区域设置, 调用者无法改坏下次schema.</summary>
    [Fact]
    public void OutputIsCultureIndependentAndSchemaCannotBeMutatedAcrossCalls()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var data = ScaffoldCalendar.Generate(new("full", 2024, 2024));
            Assert.Equal("0.14285714285714285", data.Rows[0][50]);
            Assert.Equal("Jan", data.Rows[0][15]);
            data.Columns[0] = "坏列";
            Assert.Equal("C01_Dates", ScaffoldCalendar.Generate(new("full", 2024, 2024)).DateColumn);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>非法名称与null覆盖不能绕过严格业务校验.</summary>
    [Fact]
    public void RejectsInvalidOverrideNamesAndTableNames()
    {
        Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, "")));
        Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides: [null!])));
        Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides: [new("2024-01-01", true, "坏\n名称")])));
        Assert.Throws<AppException>(() => ScaffoldCalendar.Generate(new("full", 2024, 2024, Overrides: [new("2024-01-01", true, null!)])));
    }

    private static Dictionary<string, string> Row(string date) => Values(ScaffoldCalendar.Generate(new("full", int.Parse(date[..4], CultureInfo.InvariantCulture), int.Parse(date[..4], CultureInfo.InvariantCulture))), date);
    private static Dictionary<string, string> Values(ScaffoldCalendarData data, string date) => data.Columns.Zip(data.Rows.Single(row => row[0] == date)).ToDictionary(pair => pair.First, pair => pair.Second);
}
