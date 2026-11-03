// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendar.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 生成随包离线日期表.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using PbiUtils.Core;
using PbiUtils.Application.Models;

namespace PbiUtils.Application.Calendar;

/// <summary>生成连续公历日期和固定农历字段.</summary>
public static class ScaffoldCalendar
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] WeekNames = ["一", "二", "三", "四", "五", "六", "日"];
    private static readonly string[] MonthNames = ["一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月"];
    private static readonly string[] LunarMonths = ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "腊月"];
    private static readonly string[] LunarDays = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十", "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十", "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];
    private static readonly string[] Animals = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];
    private static readonly Lazy<byte[]> LunarData = new(ReadLunarData);
    private static readonly Lazy<LunarMonth[]> LunarMonthsData = new(ReadLunarMonths);

    /// <summary>固定资源版本进入归属注解, 运行时不生成农历规则.</summary>
    public const string LunarResourceVersion = "lunar-years-1999-2099-v1";

    /// <summary>返回已校验固定年规则的独立副本.</summary>
    public static byte[] GetLunarYearData() => (byte[])LunarData.Value.Clone();

    /// <summary>返回完整年份的所选字段, 按当前字段目录与编号偏好命名.</summary>
    public static ScaffoldCalendarData Generate(ScaffoldCalendarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var columns = ScaffoldCalendarFields.Resolve(options);
        var result = GenerateFields(options, columns.Select(column => column.Field).ToArray());
        return result with { Columns = [.. columns.Select(column => column.Name)], DateColumn = columns.First(column => column.Field == "Dates").Name };
    }

    /// <summary>按精确字段语义生成确定性日期行, 同一语义的多个已有别名可重复输出.</summary>
    /// <param name="options">明确日期范围、工作周及逐日覆盖.</param>
    /// <param name="fields">实际输出的稳定语义序列, 重复语义用于保留已有别名.</param>
    /// <returns>各语义的完整确定性日期行和类型.</returns>
    /// <exception cref="AppException">日期范围、字段或工作日配置无效.</exception>
    public static ScaffoldCalendarData GenerateFields(ScaffoldCalendarOptions options, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fields);
        var definitions = fields.Select(ScaffoldCalendarFields.Get).ToArray();
        ValidateOptions(options, definitions);
        var lunar = definitions.Any(field => field.RequiresLunar);
        var first = new DateOnly(options.StartYear, 1, 1);
        var last = new DateOnly(options.EndYear, 12, 31);
        var overrides = ValidateOverrides(options.Overrides, first, last);
        var workdays = ValidateWorkdays(options.Workdays);
        var rows = new List<string[]>(last.DayNumber - first.DayNumber + 1);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var workday = overrides.GetValueOrDefault(date);
            var row = lunar ? FullRow(date, workday, workdays) : GregorianRow(date);
            rows.Add([.. definitions.Select(field => !lunar && field.Id == "Status" ? WorkdayStatus(date, workday, workdays) : row[field.Number - 1])]);
        }
        return new([.. fields], [.. definitions.Select(field => field.DataType)], rows, fields.FirstOrDefault(field => field == "Dates") ?? fields[0]);
    }

    /// <summary>生成器与M编译共用完整输入校验; 纯公历字段允许1900至2100.</summary>
    public static void ValidateOptions(ScaffoldCalendarOptions options, IReadOnlyList<ScaffoldCalendarField>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModelNames.Validate(options.TableName);
        if (options.Kind is not ("simple" or "full")) throw new AppException("INVALID_CALENDAR_KIND", "日期表类型必须为 simple 或 full");
        fields ??= ScaffoldCalendarFields.Resolve(options).Select(column => ScaffoldCalendarFields.Get(column.Field)).ToArray();
        if (fields.Count == 0) throw new AppException("INVALID_CALENDAR_FIELDS", "日期字段不能为空");
        var lunar = fields.Any(field => field.RequiresLunar);
        if (options.StartYear < (lunar ? 2000 : 1900) || options.EndYear > (lunar ? 2099 : 2100) || options.StartYear > options.EndYear)
            throw new AppException("INVALID_DATE_RANGE", lunar ? "农历日期字段年份需为 2000–2099" : "公历日期字段年份需为 1900–2100");
        _ = ValidateWorkdays(options.Workdays);
        _ = ValidateOverrides(options.Overrides, new(options.StartYear, 1, 1), new(options.EndYear, 12, 31));
    }

    /// <summary>每周工作日为周一0至周日6的唯一集合, 空集合表示每周全部休息.</summary>
    public static int[] ValidateWorkdays(int[]? values)
    {
        if (values is null) return [0, 1, 2, 3, 4];
        if (values.Length > 7 || values.Any(value => value is < 0 or > 6) || values.Distinct().Count() != values.Length)
            throw new AppException("INVALID_WORKDAYS", "每周工作日必须为0至6且不得重复");
        return [.. values.Order()];
    }

    /// <summary>预先验证覆盖, 简单表也不静默吞掉非法输入.</summary>
    private static Dictionary<DateOnly, WorkdayOverride> ValidateOverrides(WorkdayOverride[]? values, DateOnly first, DateOnly last)
    {
        var result = new Dictionary<DateOnly, WorkdayOverride>();
        if (values is null) return result;
        _ = WorkdayImport.ValidateFinal(values, first.Year, last.Year);
        if (values.Length > 10000 || values.Length > last.DayNumber - first.DayNumber + 1)
            throw new AppException("INVALID_WORKDAY_OVERRIDE", "工作日覆盖超过生成日期数量或10000条上限");
        foreach (var value in values)
        {
            if (value is null || !DateOnly.TryParseExact(value.Date, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date)
                || date < first || date > last || value.Name is null || value.Name.Length > 200 || value.Name.Any(char.IsControl)
                || !result.TryAdd(date, value))
                throw new AppException("INVALID_WORKDAY_OVERRIDE", "工作日覆盖日期无效、重复、超出范围或名称无效");
        }
        return result;
    }

    /// <summary>公历字段独立于农历资源; 周一统一为周起点, 首个不足整周仍是公历第1周.</summary>
    private static string[] GregorianRow(DateOnly date)
    {
        var weekday = ((int)date.DayOfWeek + 6) % 7;
        var year = new DateOnly(date.Year, 1, 1);
        var month = new DateOnly(date.Year, date.Month, 1);
        var quarterNumber = (date.Month - 1) / 3 + 1;
        var quarter = new DateOnly(date.Year, (quarterNumber - 1) * 3 + 1, 1);
        var halfNumber = date.Month < 7 ? 1 : 2;
        var half = new DateOnly(date.Year, halfNumber == 1 ? 1 : 7, 1);
        var week = date.AddDays(-weekday);
        var yearEnd = year.AddYears(1).AddDays(-1);
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var quarterEnd = quarter.AddMonths(3).AddDays(-1);
        var halfEnd = half.AddMonths(6).AddDays(-1);
        var weekNumber = (date.DayOfYear - 1 + ((int)year.DayOfWeek + 6) % 7) / 7 + 1;
        var dayQuarter = date.DayNumber - quarter.DayNumber + 1;
        var dayHalf = date.DayNumber - half.DayNumber + 1;
        var daysQuarter = quarterEnd.DayNumber - quarter.DayNumber + 1;
        var daysHalf = halfEnd.DayNumber - half.DayNumber + 1;
        var daysYear = yearEnd.DayOfYear;
        return
        [
            D(date), N(date.Day), N(weekday), WeekNames[weekday], "星期" + WeekNames[weekday], date.ToString("dddd", English)[..1], date.ToString("ddd", English), date.ToString("dddd", English),
            N((date.DayNumber - new DateOnly(1900, 1, 1).DayNumber) / 7 + 1), N(weekNumber), $"W{weekNumber:00}", $"{date.Year:0000}W{weekNumber:00}",
            N(date.Month), $"M{date.Month:00}", MonthNames[date.Month - 1], date.ToString("MMM", English), date.ToString("MMMM", English), $"{date.Year:0000}M{date.Month:00}", date.ToString("MMM-yy", English), N(date.Year * 100 + date.Month),
            N(quarterNumber), $"Q{quarterNumber}", $"{date.Year:0000}Q{quarterNumber}", N(date.Year * 100 + quarterNumber), halfNumber == 1 ? "上半年" : "下半年", $"H{halfNumber}", $"{date.Year:0000}H{halfNumber}", N(date.Year), $"FY{date.Year % 100:00}", "FY",
            D(week), D(month), D(quarter), D(half), D(year), D(week.AddDays(6)), D(monthEnd), D(quarterEnd), D(halfEnd), D(yearEnd),
            N(weekday + 1), N(date.Day), N(dayQuarter), N(dayHalf), N(date.DayOfYear), "7", N(monthEnd.Day), N(daysQuarter), N(daysHalf), N(daysYear),
            R((weekday + 1) / 7d), R(date.Day / (double)monthEnd.Day), R(dayQuarter / (double)daysQuarter), R(dayHalf / (double)daysHalf), R(date.DayOfYear / (double)daysYear)
        ];
    }

    /// <summary>完整输出在公历字段之后附加真实农历和工作日状态.</summary>
    private static string[] FullRow(DateOnly date, WorkdayOverride? workday, int[] workdays)
    {
        var months = LunarMonthsData.Value;
        var low = 0;
        var high = months.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (months[middle].Start <= date) low = middle;
            else high = middle - 1;
        }
        var lunar = months[low];
        var lunarDay = date.DayNumber - lunar.Start.DayNumber + 1;
        // 农历节日只作为名称, 从不据此推算法定放假或调休.
        var holiday = workday is not null ? workday.Name : LunarFestival(lunar.Month, lunarDay, lunar.Leap);
        return [.. GregorianRow(date), holiday, Animals[(lunar.Year - 4) % 12], LunarDays[lunarDay - 1],
            (lunar.Leap ? "闰" : "") + LunarMonths[lunar.Month - 1], N(lunarDay), N(lunar.Month), N(lunar.Year), WorkdayStatus(date, workday, workdays)];
    }

    /// <summary>工作日状态只依赖显式每周集合和逐日覆盖, 不推算法定调休.</summary>
    private static string WorkdayStatus(DateOnly date, WorkdayOverride? workday, int[] workdays)
    {
        var defaultWorkday = workdays.Contains(((int)date.DayOfWeek + 6) % 7);
        var defaultWeek = workdays.SequenceEqual([0, 1, 2, 3, 4]);
        return workday is null ? (defaultWorkday ? "工作日" : defaultWeek ? "周末" : "休息日")
            : workday.IsWorkday ? (defaultWorkday ? "工作日" : "补班") : "假期";
    }

    private static string LunarFestival(int month, int day, bool leap) => leap ? "" : (month, day) switch
    {
        (1, 1) => "春节",
        (1, 15) => "元宵节",
        (5, 5) => "端午节",
        (7, 7) => "七夕",
        (8, 15) => "中秋节",
        (9, 9) => "重阳节",
        (12, 8) => "腊八节",
        _ => ""
    };
    private static string D(DateOnly value) => value.ToString("yyyy-MM-dd", Invariant);
    private static string N(int value) => value.ToString(Invariant);
    private static string R(double value) => value.ToString("R", Invariant);

    /// <summary>只加载固定摘要年规则, 不调用运行时农历算法或网络.</summary>
    private static byte[] ReadLunarData()
    {
        using var stream = typeof(ScaffoldCalendar).Assembly.GetManifestResourceStream("PbiUtils.Scaffolding.Lunar.Years.v1")
            ?? throw new AppException("LUNAR_RESOURCE_INVALID", "随包农历资源缺失");
        var bytes = new byte[315];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals("93E54D4A49576BEF30BA1C595013DFFC0A7465BF46B995DD3372E4C2B48F3432", StringComparison.Ordinal))
            throw new AppException("LUNAR_RESOURCE_INVALID", "随包农历资源校验失败");
        return bytes;
    }

    /// <summary>年定义展开为1249个月边界, 公历年初正确落入农历上年.</summary>
    private static LunarMonth[] ReadLunarMonths()
    {
        var bytes = LunarData.Value;
        var firstYear = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var years = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        var start = new DateOnly(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)), bytes[10], bytes[11]);
        var result = new List<LunarMonth>();
        for (var i = 0; i < years; i++)
        {
            var offset = 12 + i * 3;
            var rule = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
            var leapAfter = (rule >> 13) & 15;
            for (var sequential = 1; sequential <= (leapAfter == 0 ? 12 : 13); sequential++)
            {
                var leap = leapAfter != 0 && sequential == leapAfter + 1;
                var month = sequential - (leapAfter != 0 && sequential > leapAfter ? 1 : 0);
                result.Add(new(start, firstYear + i, month, leap));
                start = start.AddDays(29 + ((rule >> (sequential - 1)) & 1));
            }
        }
        return [.. result];
    }

    private sealed record LunarMonth(DateOnly Start, int Year, int Month, bool Leap);
}
