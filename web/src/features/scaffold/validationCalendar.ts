/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validationCalendar.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 日期表和时间智能的纯参数、字段映射与预算校验.
 */
import type { ScaffoldCalendarOptions } from "./contracts"
import type { ScaffoldValidationInput, ScaffoldValidationIssue } from "./validationTypes"
import { calendarColumnOutputName, calendarDateColumn, calendarFields, calendarNeedsLunar, selectedCalendarFields } from "./calendarFields"
import { workdayBudget } from "./workdayImport"
import { absent, blank, controls, date, integer, issue, nameKey, objectName, text } from "./validationRules"
/** 日期配置与覆盖按当前启用类型检查, 所有列表错误使用一开始的行号定位. */
export function calendarIssue(calendar: ScaffoldCalendarOptions | null): ScaffoldValidationIssue | undefined {
    if (absent(calendar)) return undefined
    if (calendar.kind !== "simple" && calendar.kind !== "full") return issue("calendar", "kind", "CalendarKind")
    if (!objectName(calendar.tableName)) return issue("calendar", "tableName", "Name")
    if (
        calendar.fields !== undefined &&
        (!Array.isArray(calendar.fields) ||
            !calendar.fields.includes("Dates") ||
            new Set(calendar.fields).size !== calendar.fields.length ||
            calendar.fields.some((id) => !calendarFields.some((field) => field.id === id)))
    )
        return issue("calendar", "fields", "CalendarFields")
    if (calendar.numberedColumns !== undefined && typeof calendar.numberedColumns !== "boolean") return issue("calendar", "fields", "Boolean")
    if (
        calendar.workdays !== undefined &&
        (!Array.isArray(calendar.workdays) ||
            calendar.workdays.length > 7 ||
            new Set(calendar.workdays).size !== calendar.workdays.length ||
            calendar.workdays.some((day) => !integer(day, 0, 6)))
    )
        return issue("calendar", "workdays", "CalendarWorkdays")
    const lunar = calendar.fields ? calendarNeedsLunar(calendar.fields) : calendar.kind === "full"
    const min = lunar ? 2000 : 1900,
        max = lunar ? 2099 : 2100
    if (!integer(calendar.startYear, min, max)) return issue("calendar", "startYear", "Range", undefined, { min, max })
    if (!integer(calendar.endYear, min, max)) return issue("calendar", "endYear", "Range", undefined, { min, max })
    if (calendar.startYear > calendar.endYear) return issue("calendar", "endYear", "YearOrder")
    if (absent(calendar.overrides)) return undefined
    if (!Array.isArray(calendar.overrides)) return issue("calendar", "overrides", "Count", undefined, { min: 0, max: 10000 })
    if (calendar.overrides.length > 10000) return issue("calendar", "overrides", "CalendarBudget")
    const dates = new Set<string>()
    for (const [index, item] of calendar.overrides.entries()) {
        const row = index + 1
        if (!item || !Number.isFinite(date(item.date))) return issue("calendar", "overrideDate", "Date", row)
        const year = Number(item.date.slice(0, 4))
        if (year < calendar.startYear || year > calendar.endYear) return issue("calendar", "overrideDate", "OverrideRange", row)
        if (dates.has(item.date)) return issue("calendar", "overrideDate", "Duplicate", row)
        dates.add(item.date)
        if (typeof item.isWorkday !== "boolean") return issue("calendar", "overrideIsWorkday", "Boolean", row)
        if (
            typeof item.name !== "string" ||
            item.name.length > 200 ||
            controls.test(item.name) ||
            item.name.includes("\uFEFF") ||
            /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(item.name)
        )
            return issue("calendar", "overrideName", "WorkdayName", row)
    }
    if (workdayBudget(calendar.overrides)) return issue("calendar", "overrides", "CalendarBudget")
    return undefined
}

/** 时间智能单独启用即可使用模板实际日期, 只从已读元数据和启用的新增配置判断确定失效的绑定. */
export function periodsIssue(
    input: Pick<ScaffoldValidationInput, "periods" | "calendar" | "measures" | "details" | "periodDefinitions">,
): ScaffoldValidationIssue | undefined {
    const { periods, calendar, measures, details, periodDefinitions } = input
    if (absent(periods)) return undefined
    for (const [value, field] of [
        [periods.dateTable, "periodDateTable"],
        [periods.dateColumn, "periodDateColumn"],
        [periods.measureTable, "periodMeasureTable"],
    ] as const)
        if (!objectName(value)) return issue("calendar", field, "Name")
    if (!Array.isArray(periods.baseMeasures) || periods.baseMeasures.length < 1 || periods.baseMeasures.length > 128)
        return issue("calendar", "periodBaseMeasures", "Count", undefined, { min: 1, max: 128 })
    const names = new Set<string>()
    for (const value of periods.baseMeasures) {
        if (!objectName(value)) return issue("calendar", "periodBaseMeasures", "Name")
        const key = nameKey(value)
        if (names.has(key)) return issue("calendar", "periodBaseMeasures", "Duplicate")
        names.add(key)
    }
    if (!Array.isArray(periods.templates) || periods.templates.length > 51) return issue("calendar", "periodTemplates", "Count", undefined, { min: 0, max: 51 })
    if (
        periods.templates.some(
            (value) =>
                typeof value !== "string" ||
                blank(value) ||
                value.length > 100 ||
                controls.test(value) ||
                (periodDefinitions !== undefined && !periodDefinitions.some((item) => item.id === value)),
        )
    )
        return issue("calendar", "periodTemplates", "PeriodTemplate")
    if (!objectName(periods.folder)) return issue("calendar", "periodFolder", "Name")
    if (!text(periods.folder, 200)) return issue("calendar", "periodFolder", "Text", undefined, { max: 200 })
    if (!integer(periods.numberDigits, 0, 8)) return issue("calendar", "periodNumberDigits", "Range", undefined, { min: 0, max: 8 })
    if (!integer(periods.percentDigits, 0, 8)) return issue("calendar", "periodPercentDigits", "Range", undefined, { min: 0, max: 8 })
    if (periods.baseMeasures.some((name) => periods.templates.some((id) => 4 + id.length + name.length > 200)))
        return issue("calendar", "periodBaseMeasures", "PeriodName")
    if (details) {
        const table = details.tables.find((item) => nameKey(item.name) === nameKey(periods.dateTable))
        const generated = !!calendar && nameKey(calendar.tableName) === nameKey(periods.dateTable)
        if (!table && !generated) return issue("calendar", "periodDateTable", "Binding")
        const capability = details.calendarCapabilities?.find((item) => nameKey(item.tableName) === nameKey(periods.dateTable))
        const actualColumn =
            (!generated || capability?.mode === "existing") &&
            table?.columns.some((column) => {
                const mapping = generated ? capability?.columns.find((item) => item.name === column.name) : undefined
                if (mapping && !selectedCalendarFields(calendar!).includes(mapping.field)) return false
                const output = mapping ? calendarColumnOutputName(mapping.field, column.name, calendar!.numberedColumns !== false) : column.name
                return nameKey(output) === nameKey(periods.dateColumn) && column.dataType === "DateTime"
            })
        const generatedColumn = generated && periods.dateColumn === calendarDateColumn(calendar!, capability)
        if (!actualColumn && !generatedColumn) return issue("calendar", "periodDateColumn", "Binding")
        if (
            !details.tables.some((item) => nameKey(item.name) === nameKey(periods.measureTable)) &&
            !(measures !== null && nameKey(periods.measureTable) === "MEASURE") &&
            !(calendar && nameKey(calendar.tableName) === nameKey(periods.measureTable))
        )
            return issue("calendar", "periodMeasureTable", "Binding")
        const available = new Set(
            [...details.tables.flatMap((item) => item.measures), ...(measures?.filter((item) => item.enabled).map((item) => item.name) ?? [])].map(nameKey),
        )
        if (periods.baseMeasures.some((name) => !available.has(nameKey(name)))) return issue("calendar", "periodBaseMeasures", "Binding")
    }
    return undefined
}
