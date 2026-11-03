// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendarFields.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 日期字段唯一语义目录、固定编号与当前快捷选集.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.Calendar;

/// <summary>稳定日期字段定义, 排序辅助列只在同时输出时绑定.</summary>
public sealed record ScaffoldCalendarField(string Id, int Number, string DataType, string Group, string Description, string? SortBy, bool RequiresLunar);

/// <summary>日期字段目录不依赖界面语言或调用者可变集合.</summary>
public static class ScaffoldCalendarFields
{
    private static readonly string[] Ids =
    [
        "Dates", "Day", "WeekDay", "WeekCNS1", "WeekCNS3", "WeekENS1", "WeekENS3", "WeekEN", "WeekIndex", "WeekNumber", "Week", "YearWeek",
        "Month", "MonthM", "MonthCN", "MonthENS3", "MonthEN", "YearMonthM", "YearMonthUS", "YearMonth", "Quarter", "QuarterQ", "YearQuarterQ", "YearQuarter",
        "HalfOfYearCN", "HalfOfYearEN", "YearHalf", "Year", "FY00", "FY", "StartOfWeek", "StartOfMonth", "StartOfQuarter", "StartOfHalfYear", "StartOfYear",
        "EndOfWeek", "EndOfMonth", "EndOfQuarter", "EndOfHalfYear", "EndOfYear", "DayOfWeek", "DayOfMonth", "DayOfQuarter", "DayOfHalfYear", "DayOfYear",
        "DaysOfWeek", "DaysOfMonth", "DaysOfQuarter", "DaysOfHalfYear", "DaysOfYear", "ProgressOfWeek", "ProgressOfMonth", "ProgressOfQuarter", "ProgressOfHalfYear", "ProgressOfYear",
        "Holiday", "Animal", "LunarDateCN", "LunarMonthCN", "LunarDate", "LunarMonth", "LunarYear", "Status"
    ];
    private static readonly string[] Descriptions =
    [
        "公历日期", "月内日号", "星期序号（周一为0）", "中文星期简写", "中文星期", "英文星期首字母", "英文星期缩写", "英文星期", "自1900年起周序号", "年内周数", "周数标签", "年周标签",
        "月份序号", "月份标签", "中文月份", "英文月份缩写", "英文月份", "年月标签", "英文年月", "年月排序键", "季度序号", "季度标签", "年季度标签", "年季度排序键",
        "中文半年", "半年标签", "年半年标签", "公历年份", "两位年标签", "财年标识", "周首日", "月首日", "季首日", "半年首日", "年首日",
        "周末日", "月末日", "季末日", "半年末日", "年末日", "周内第几天", "月内第几天", "季内第几天", "半年内第几天", "年内第几天",
        "每周天数", "本月天数", "本季天数", "本半年天数", "本年天数", "本周进度", "本月进度", "本季进度", "本半年进度", "本年进度",
        "传统节日或覆盖名称（非法定假期）", "生肖", "中文农历日", "中文农历月（含闰月）", "农历日号", "农历月份", "农历年份", "工作日状态"
    ];

    /// <summary>固定原目录顺序, 序号不会随选择重新排列.</summary>
    public static IReadOnlyList<ScaffoldCalendarField> All { get; } = Array.AsReadOnly(Ids.Select((id, i) => new ScaffoldCalendarField(
        id, i + 1, Type(i + 1), Group(i + 1), Descriptions[i], Sort(id), i is >= 55 and <= 61)).ToArray());
    /// <summary>新方案推荐的15个语义字段.</summary>
    public static IReadOnlyList<string> Recommended { get; } = Array.AsReadOnly(["Dates", "Day", "WeekDay", "WeekCNS3", "WeekNumber", "YearWeek", "Month", "MonthCN", "YearMonthM", "YearMonth", "Quarter", "YearQuarterQ", "YearQuarter", "Year", "Status"]);
    /// <summary>参考简单日期表五种语义.</summary>
    public static IReadOnlyList<string> Simple { get; } = Array.AsReadOnly(["Dates", "Day", "MonthM", "YearMonthM", "FY00"]);

    /// <summary>按精确稳定ID查找, 不从编号或翻译猜测.</summary>
    public static ScaffoldCalendarField Get(string id) => All.FirstOrDefault(field => field.Id == id)
        ?? throw new AppException("INVALID_CALENDAR_FIELDS", "日期字段标识无效");

    /// <summary>新列使用原序号, 旧列由宿主提供实际映射.</summary>
    public static string ColumnName(string field, bool numbered)
    {
        var item = Get(field);
        return numbered ? "C" + item.Number.ToString("00", CultureInfo.InvariantCulture) + "_" + item.Id : item.Id;
    }

    /// <summary>simple/full 提供默认字段选集, 全部输出统一按当前编号偏好命名且要求日期键.</summary>
    public static IReadOnlyList<ScaffoldCalendarColumn> Resolve(ScaffoldCalendarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Kind is not ("simple" or "full")) throw new AppException("INVALID_CALENDAR_KIND", "日期表类型必须为 simple 或 full");
        var fields = options.Fields ?? (options.Kind == "simple" ? [.. Simple] : Ids);
        if (fields.Length == 0 || !fields.Contains("Dates", StringComparer.Ordinal) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new AppException("INVALID_CALENDAR_FIELDS", "日期字段必须包含Dates且不得重复");
        foreach (var field in fields) _ = Get(field);
        return Array.AsReadOnly(All.Where(field => fields.Contains(field.Id, StringComparer.Ordinal))
            .Select(field => new ScaffoldCalendarColumn(ColumnName(field.Id, options.NumberedColumns ?? true), field.Id)).ToArray());
    }

    private static string Type(int index) => index switch
    {
        1 or >= 31 and <= 40 => "DateTime",
        2 or 3 or 9 or 10 or 13 or 20 or 21 or 24 or 28 or >= 41 and <= 50 or >= 60 and <= 62 => "Int64",
        >= 51 and <= 55 => "Double",
        _ => "String"
    };
    private static string Group(int index) => index switch
    {
        <= 12 => "dayWeek",
        <= 30 => "monthYear",
        <= 40 => "boundaries",
        <= 50 => "counts",
        <= 55 => "progress",
        <= 62 => "lunar",
        _ => "workday"
    };
    private static string? Sort(string id) => id switch
    {
        "WeekCNS1" or "WeekCNS3" or "WeekENS3" or "WeekEN" => "WeekDay",
        "Week" => "WeekNumber",
        "MonthM" or "MonthCN" or "MonthENS3" or "MonthEN" => "Month",
        "YearMonthM" or "YearMonthUS" => "YearMonth",
        "QuarterQ" => "Quarter",
        "YearQuarterQ" => "YearQuarter",
        "LunarDateCN" => "LunarDate",
        _ => null
    };
}
