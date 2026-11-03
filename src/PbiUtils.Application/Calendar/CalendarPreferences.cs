// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\CalendarPreferences.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 两入口共用日期字段方案偏好, 不保存目标、日期范围或工作日覆盖.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;
using PbiUtils.Application.Calendar;

namespace PbiUtils.Application.Calendar;

/// <summary>只记录用户主动选择的通用字段方案, 目标强制列不属于偏好.</summary>
public sealed record CalendarPreferences(string[] Fields, bool NumberedColumns, string? Warning = null, bool IsSimulation = false);

/// <summary>由宿主绑定独立用户目录的日期方案存储边界.</summary>
public interface ICalendarPreferencesStore
{
    /// <summary>读取已存方案, 损坏或未知文件保留并返回带警告的默认方案.</summary>
    CalendarPreferences Read();

    /// <summary>严格验证字段并原子保存, 提交前取消且提交后排空.</summary>
    CalendarPreferences Save(string[] fields, bool numberedColumns, CancellationToken token, Action? committing = null);
}

/// <summary>日期字段偏好共享用例, 与模型写入授权和目标配置相互独立.</summary>
public sealed class CalendarPreferencesService(ICalendarPreferencesStore store, ILogger<CalendarPreferencesService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<CalendarPreferencesService>.Instance;

    /// <summary>返回全新推荐方案数组, 调用方修改不会污染其他草稿.</summary>
    public static CalendarPreferences Default(string? warning = null) => new([.. ScaffoldCalendarFields.Recommended], true, warning);

    /// <summary>读取宿主权威方案并保留警告和模拟标志.</summary>
    public CalendarPreferences Read() => BusinessLog.Run(log, "Calendar.Preferences", store.Read);

    /// <summary>只保存通用方案, 不接收表名、年份、模型身份或工作日数据.</summary>
    public CalendarPreferences Save(string[] fields, bool numberedColumns, CancellationToken token, Action? committing = null) => BusinessLog.Run(log, "Calendar.PreferencesSave", () =>
    {
        var normalized = NormalizeFields(fields);
        token.ThrowIfCancellationRequested();
        return store.Save(normalized, numberedColumns, token, committing);
    });

    /// <summary>精确验证稳定字段ID及日期键, 输出唯一目录顺序且不补选目标依赖.</summary>
    public static string[] NormalizeFields(IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0 || fields.Count > ScaffoldCalendarFields.All.Count
            || !fields.Contains("Dates", StringComparer.Ordinal) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count
            || fields.Any(id => !ScaffoldCalendarFields.All.Any(field => string.Equals(field.Id, id, StringComparison.Ordinal))))
            throw new AppException("INVALID_CALENDAR_FIELDS", "日期方案必须包含 Dates, 字段不得重复且必须来自支持目录");
        var selected = fields.ToHashSet(StringComparer.Ordinal);
        return [.. ScaffoldCalendarFields.All.Where(field => selected.Contains(field.Id)).Select(field => field.Id)];
    }
}
