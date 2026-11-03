/**
 * FilePath    : pbi-utils\web\src\features\scaffold\calendarFields.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 固定日期字段语义与离线草稿默认方案, 已有目标映射始终由宿主提供.
 */
import type { CalendarCapability, CalendarFieldDefinition, ScaffoldCalendarOptions } from "./contracts"
const ids = [
    "Dates",
    "Day",
    "WeekDay",
    "WeekCNS1",
    "WeekCNS3",
    "WeekENS1",
    "WeekENS3",
    "WeekEN",
    "WeekIndex",
    "WeekNumber",
    "Week",
    "YearWeek",
    "Month",
    "MonthM",
    "MonthCN",
    "MonthENS3",
    "MonthEN",
    "YearMonthM",
    "YearMonthUS",
    "YearMonth",
    "Quarter",
    "QuarterQ",
    "YearQuarterQ",
    "YearQuarter",
    "HalfOfYearCN",
    "HalfOfYearEN",
    "YearHalf",
    "Year",
    "FY00",
    "FY",
    "StartOfWeek",
    "StartOfMonth",
    "StartOfQuarter",
    "StartOfHalfYear",
    "StartOfYear",
    "EndOfWeek",
    "EndOfMonth",
    "EndOfQuarter",
    "EndOfHalfYear",
    "EndOfYear",
    "DayOfWeek",
    "DayOfMonth",
    "DayOfQuarter",
    "DayOfHalfYear",
    "DayOfYear",
    "DaysOfWeek",
    "DaysOfMonth",
    "DaysOfQuarter",
    "DaysOfHalfYear",
    "DaysOfYear",
    "ProgressOfWeek",
    "ProgressOfMonth",
    "ProgressOfQuarter",
    "ProgressOfHalfYear",
    "ProgressOfYear",
    "Holiday",
    "Animal",
    "LunarDateCN",
    "LunarMonthCN",
    "LunarDate",
    "LunarMonth",
    "LunarYear",
    "Status",
] as const
const zh = [
    "日期键",
    "日",
    "星期序号 (周一为0)",
    "中文星期单字",
    "中文星期",
    "英文星期首字母",
    "英文星期缩写",
    "英文星期全名",
    "连续周序号",
    "年内周数 (周一开始)",
    "周标签",
    "年周标签",
    "月份",
    "月份标签",
    "中文月份",
    "英文月份缩写",
    "英文月份全名",
    "年月份标签",
    "英文年月",
    "年月数值",
    "季度",
    "季度标签",
    "年季度标签",
    "年季度数值",
    "中文半年",
    "英文半年",
    "年半年标签",
    "公历年",
    "两位财年标签",
    "财年标签",
    "周开始日",
    "月开始日",
    "季度开始日",
    "半年开始日",
    "年开始日",
    "周结束日",
    "月结束日",
    "季度结束日",
    "半年结束日",
    "年结束日",
    "周内第几天",
    "月内第几天",
    "季度内第几天",
    "半年内第几天",
    "年内第几天",
    "一周天数",
    "月份天数",
    "季度天数",
    "半年天数",
    "全年天数",
    "周进度",
    "月进度",
    "季度进度",
    "半年进度",
    "年进度",
    "节日或覆盖名称 (非法定假期)",
    "农历生肖",
    "农历日中文",
    "农历月中文",
    "农历日数值",
    "农历月数值",
    "农历年",
    "工作日状态",
]
const en = [
    "Date key",
    "Day",
    "Weekday (Monday is 0)",
    "Chinese weekday initial",
    "Chinese weekday",
    "English weekday initial",
    "Short English weekday",
    "English weekday",
    "Continuous week index",
    "Week of year (Monday first)",
    "Week label",
    "Year-week label",
    "Month number",
    "Month label",
    "Chinese month",
    "Short English month",
    "English month",
    "Year-month label",
    "English year-month",
    "Numeric year-month",
    "Quarter number",
    "Quarter label",
    "Year-quarter label",
    "Numeric year-quarter",
    "Chinese half-year",
    "English half-year",
    "Year-half label",
    "Calendar year",
    "Two-digit fiscal year",
    "Fiscal year label",
    "Start of week",
    "Start of month",
    "Start of quarter",
    "Start of half-year",
    "Start of year",
    "End of week",
    "End of month",
    "End of quarter",
    "End of half-year",
    "End of year",
    "Day of week",
    "Day of month",
    "Day of quarter",
    "Day of half-year",
    "Day of year",
    "Days in week",
    "Days in month",
    "Days in quarter",
    "Days in half-year",
    "Days in year",
    "Week progress",
    "Month progress",
    "Quarter progress",
    "Half-year progress",
    "Year progress",
    "Festival or override name (not statutory leave)",
    "Lunar zodiac",
    "Chinese lunar day",
    "Chinese lunar month",
    "Lunar day",
    "Lunar month",
    "Lunar year",
    "Workday status",
]
/** 离线默认目录只服务新草稿; 页面接收到宿主目录后使用宿主元数据. */
export const calendarFields: CalendarFieldDefinition[] = ids.map((id, index) => ({
    id,
    number: index + 1,
    description: zh[index]!,
    dataType:
        index === 0 || (index >= 30 && index < 40)
            ? "DateTime"
            : [1, 2, 8, 9, 12, 19, 20, 23, 27].includes(index) || (index >= 40 && index < 50) || (index >= 59 && index < 62)
              ? "Int64"
              : index >= 50 && index < 55
                ? "Double"
                : "String",
    group:
        index < 12
            ? "dayWeek"
            : index < 30
              ? "monthYear"
              : index < 40
                ? "boundaries"
                : index < 50
                  ? "counts"
                  : index < 55
                    ? "progress"
                    : index < 62
                      ? "lunar"
                      : "workday",
    requiresLunar: index >= 55 && index < 62,
}))
export const recommendedFields = [
    "Dates",
    "Day",
    "WeekDay",
    "WeekCNS3",
    "WeekNumber",
    "YearWeek",
    "Month",
    "MonthCN",
    "YearMonthM",
    "YearMonth",
    "Quarter",
    "YearQuarterQ",
    "YearQuarter",
    "Year",
    "Status",
]
export const simpleFields = ["Dates", "Day", "MonthM", "YearMonthM", "FY00"]
/** 按稳定目录排序, 始终保留日期键, 锁定列不会改变调用方的通用方案. */
export function effectiveFields(selected: readonly string[], locked: { field: string; reason: string }[] = []): string[] {
    const wanted = new Set(["Dates", ...selected, ...locked.map((item) => item.field)])
    return ids.filter((id) => wanted.has(id))
}
/** 固定原编号, 筛选中间字段不会重排后续输出名. */
export function calendarColumnName(field: string, numbered: boolean): string {
    const index = ids.indexOf(field as (typeof ids)[number])
    return numbered && index >= 0 ? `C${String(index + 1).padStart(2, "0")}_${field}` : field
}
/** 仅标准语义名与固定编号名随当前选项覆盖, 非标准人工别名和历史错误编号保持; 不据名称授予生成归属. */
export function calendarColumnOutputName(field: string, existingName: string, numbered: boolean): string {
    return existingName === field || existingName === calendarColumnName(field, true) ? calendarColumnName(field, numbered) : existingName
}
/** 字段说明翻译不改变输出名称与固定日期业务文本. */
export function calendarFieldDescription(field: string, english: boolean): string {
    const index = ids.indexOf(field as (typeof ids)[number])
    return (english ? en[index] : zh[index]) ?? field
}
/** 农历范围只由实际输出语义决定, Status 独立于农历. */
export function calendarNeedsLunar(fields: readonly string[]): boolean {
    return fields.some((id) => calendarFields.find((field) => field.id === id)?.requiresLunar)
}
/** 显式选择优先, 否则按当前 simple/full 快捷方案取得字段集合, 不回写草稿. */
export function selectedCalendarFields(options: ScaffoldCalendarOptions): string[] {
    return options.fields ?? (options.kind === "full" ? [...ids] : [...simpleFields])
}
/** 已有目标仅在明确指定编号方式时更新标准日期键, 其余名称保持; 新表使用当前默认命名. */
export function calendarDateColumn(options: ScaffoldCalendarOptions, capability?: CalendarCapability): string {
    const existing = capability?.mode === "existing" ? capability.dateColumn || capability.columns.find((column) => column.field === "Dates")?.name : undefined
    if (existing) return typeof options.numberedColumns === "boolean" ? calendarColumnOutputName("Dates", existing, options.numberedColumns) : existing
    return calendarColumnName("Dates", options.numberedColumns !== false)
}
