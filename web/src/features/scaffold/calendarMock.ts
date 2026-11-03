/**
 * FilePath    : pbi-utils\web\src\features\scaffold\calendarMock.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟的日期偏好、代码预览和有界覆盖解析.
 */
import type { CalendarPreferences, CalendarCodeProgram, CalendarPreviewContext, WorkdayOverride } from "./contracts"
import { calendarFields, effectiveFields, recommendedFields } from "./calendarFields"
import { dateMillis } from "@/features/sample/presentation"
import { workdayBudget } from "./workdayImport"
import { copy } from "./mockValue"
/** 仅处理日期模拟, 未知方法保持明确失败. */
export class CalendarMock {
    private preferences: CalendarPreferences = { fields: [...recommendedFields], numberedColumns: true, isSimulation: true }
    /** 使用与宿主相同的输入形状返回明确模拟结果, 非法输入抛出原错误. */
    handle(method: string, params: Record<string, unknown>): unknown {
        if (method === "calendar.preferences") return copy(this.preferences)
        if (method === "calendar.preview") {
            const context = params.context as CalendarPreviewContext
            if (!context || (context.kind !== "model" && context.kind !== "template")) throw new Error("INVALID_CALENDAR_PREVIEW_CONTEXT")
            return {
                functionName: "PbiUtilsCalendar",
                functionExpression:
                    "// Browser simulation: the production host generates the actual calendar function.\n(StartYear as number, EndYear as number, Workdays as list, Overrides as list) as table =>\n    #table(type table [Dates = date], {})",
                invocationExpression: `// Browser simulation only\nlet\n    Source = PbiUtilsCalendar(${Number((params.calendar as { startYear: number }).startYear)}, ${Number((params.calendar as { endYear: number }).endYear)}, {}, {})\nin\n    Source`,
                inputDigest: "0".repeat(64),
                contextBaseline: context.kind === "model" ? context.baselineId : context.templateBaseline,
                generatorVersion: "calendar-m-simulation",
                isSimulation: true,
            } satisfies CalendarCodeProgram
        }
        if (method === "calendar.savePreferences") {
            const fields = params.fields as string[]
            if (
                !Array.isArray(fields) ||
                !fields.includes("Dates") ||
                new Set(fields).size !== fields.length ||
                fields.some((id) => !calendarFields.some((field) => field.id === id)) ||
                typeof params.numberedColumns !== "boolean"
            )
                throw new Error("INVALID_CALENDAR_PREFERENCES")
            this.preferences = { fields: effectiveFields(fields), numberedColumns: params.numberedColumns, isSimulation: true }
            return copy(this.preferences)
        }
        if (method === "calendar.parseWorkdays") return { overrides: this.parseWorkdays(params), isSimulation: true }
        throw new Error("UNKNOWN_METHOD")
    }
    /** 显式浏览器模拟的有界CSV/TSV解析; 不冒充真实宿主或磁盘读取. */
    private parseWorkdays(params: Record<string, unknown>): WorkdayOverride[] {
        const text = String(params.text ?? "").replace(/^\uFEFF/, "")
        if (new TextEncoder().encode(String(params.text ?? "")).length > 2 * 1024 * 1024) throw new Error("第 1 行: 原文超过 2 MiB")
        const separator = params.delimiter === "tsv" ? "\t" : ","
        const rows: { cells: string[]; line: number }[] = []
        let cells: string[] = [],
            cell = "",
            quoted = false,
            closed = false,
            line = 1
        /** 保存非空行, 保留原始行号用于整批错误. */
        function finish(): void {
            cells.push(cell)
            if (cells.some((value) => value.trim())) rows.push({ cells, line })
            cells = []
            cell = ""
            closed = false
        }
        for (let index = 0; index < text.length; index++) {
            const char = text[index]!
            if (quoted) {
                if (char === '"') {
                    if (text[index + 1] === '"') {
                        cell += '"'
                        index++
                    } else {
                        quoted = false
                        closed = true
                    }
                } else if (char === "\r" || char === "\n") throw new Error(`第 ${line} 行: 字段不允许换行`)
                else cell += char
            } else if (char === separator) {
                cells.push(cell)
                cell = ""
                closed = false
            } else if (char === "\r" || char === "\n") {
                finish()
                if (char === "\r" && text[index + 1] === "\n") index++
                line++
            } else if (char === '"') {
                if (cell || closed) throw new Error(`第 ${line} 行: 引号无效`)
                quoted = true
            } else if (closed) {
                if (char !== " ") throw new Error(`第 ${line} 行: 闭合引号后内容无效`)
            } else cell += char
        }
        if (quoted) throw new Error(`第 ${line} 行: 引号未闭合`)
        if (cell || cells.length || closed) finish()
        const headers = [
            ["date", "Date", "日期"],
            ["isWorkday", "IsWorkday", "是否工作日"],
            ["name", "Name", "名称"],
        ]
        if (!rows[0] || rows[0].cells.length !== 3 || rows[0].cells.some((value, index) => !headers[index]!.includes(value.trim())))
            throw new Error("第 1 行: 表头必须为 date,isWorkday,name")
        const seen = new Set<string>()
        const values = rows.slice(1).map((row) => {
            const [date = "", flag = "", name = ""] = row.cells.map((value) => value.trim())
            const year = Number(date.slice(0, 4))
            if (
                row.cells.length !== 3 ||
                !Number.isFinite(dateMillis(date)) ||
                year < Number(params.startYear) ||
                year > Number(params.endYear) ||
                seen.has(date) ||
                name.length > 200 ||
                /[\p{Cc}\uFEFF]/u.test(name)
            )
                throw new Error(`第 ${row.line} 行: 日期、重复项或名称无效`)
            seen.add(date)
            if (!["true", "false", "1", "0", "是", "否", "工作日", "休息日"].includes(flag)) throw new Error(`第 ${row.line} 行: 工作标志无效`)
            return { date, isWorkday: ["true", "1", "是", "工作日"].includes(flag), name }
        })
        if (workdayBudget(values)) throw new Error(`第 ${rows.at(-1)?.line ?? 1} 行: 最终覆盖超过预算`)
        return values.toSorted((a, b) => a.date.localeCompare(b.date))
    }
}
