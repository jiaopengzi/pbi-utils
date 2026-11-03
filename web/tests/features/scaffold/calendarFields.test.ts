/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\calendarFields.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证固定字段编号, 用户方案与宿主锁定分离及工作日批量合并预算.
 */
import { expect, it } from "vitest"
import {
    calendarFields,
    recommendedFields,
    simpleFields,
    calendarColumnName,
    calendarDateColumn,
    effectiveFields,
    calendarNeedsLunar,
} from "@/features/scaffold/calendarFields"
import { previewWorkdays, mergeWorkdays, workdayBudget } from "@/features/scaffold/workdayImport"
import { createConfigurationDraft } from "@/features/configuration/draft"
import { readFileSync } from "node:fs"
import { resolve } from "node:path"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import { workdayImportMessage, workdaySourceLines } from "@/features/scaffold/workdayImport"
import { setLocale, t } from "@/i18n"

it("真实宿主解析错误精确翻译并保留原行号, 未知诊断不改写", () => {
    setLocale("en-us")
    try {
        expect(workdayImportMessage("第 17 行: 日期无效、重复、超出范围或名称无效", t)).toContain("Row 17: A date is invalid")
        expect(workdayImportMessage("第 9 行: 引用字段缺少闭合引号", t)).toContain("Row 9: A quoted field")
        expect(workdayImportMessage("第 3 行: 未知新错误", t)).toBe("第 3 行: 未知新错误")
        expect(workdayImportMessage("WORKDAY_DUPLICATE", t)).toContain("duplicate override dates")
    } finally {
        setLocale("zh-cn")
    }
})

it("预览保留CSV/TSV的原始空行和引号日期行号", () => {
    expect(workdaySourceLines('\uFEFFdate,isWorkday,name\r\n\r\n"2025-01-01",true,公司', "csv").get("2025-01-01")).toBe(3)
    expect(workdaySourceLines("date\tisWorkday\tname\n2025-01-02\tfalse\t", "tsv").get("2025-01-02")).toBe(2)
})
it("显式浏览器模拟的方案仅内存保存, 导入标记模拟并整批拒绝重复行", () => {
    const mock = new ScaffoldMock()
    expect(mock.handle("calendar.savePreferences", { fields: ["Dates", "Status"], numberedColumns: false })).toMatchObject({ isSimulation: true })
    expect(mock.handle("calendar.preferences", {})).toMatchObject({ fields: ["Dates", "Status"], numberedColumns: false, isSimulation: true })
    expect(
        mock.handle("calendar.parseWorkdays", { text: 'date,isWorkday,name\n2025-01-01,true,"公司,一部"', delimiter: "csv", startYear: 2025, endYear: 2025 }),
    ).toMatchObject({ overrides: [{ date: "2025-01-01", isWorkday: true, name: "公司,一部" }], isSimulation: true })
    expect(() =>
        mock.handle("calendar.parseWorkdays", {
            text: "date,isWorkday,name\n2025-01-01,true,\n2025-01-01,false,",
            delimiter: "csv",
            startYear: 2025,
            endYear: 2025,
        }),
    ).toThrow("第 3 行")
})

it("日期代码模拟明确标记simulation且沿用调用方上下文基线", () => {
    const mock = new ScaffoldMock()
    const program = mock.handle("calendar.preview", {
        context: { kind: "model", targetId: "target", baselineId: "baseline" },
        calendar: { startYear: 2020, endYear: 2025 },
        locale: "zh-cn",
    })
    expect(program).toMatchObject({ isSimulation: true, contextBaseline: "baseline", generatorVersion: "calendar-m-simulation" })
    expect(program).toHaveProperty("functionExpression", expect.stringContaining("Browser simulation"))
})

it("前端离线ID和快捷方案与后端权威目录逐项一致", () => {
    const backendDirectory = readFileSync(resolve("../src/PbiUtils.Application/Calendar/ScaffoldCalendarFields.cs"), "utf-8")
    const backendIds = [...backendDirectory.match(/string\[\] Ids\s*=\s*\[([\s\S]+?)\];/)![1]!.matchAll(/"([A-Za-z0-9]+)"/g)].map((match) => match[1])
    expect(calendarFields.map((field) => field.id)).toEqual(backendIds)
    expect(backendDirectory).toContain(`Recommended { get; } = Array.AsReadOnly([${recommendedFields.map((id) => `"${id}"`).join(", ")}])`)
    expect(backendDirectory).toContain(`Simple { get; } = Array.AsReadOnly([${simpleFields.map((id) => `"${id}"`).join(", ")}])`)
})

it("新草稿使用推荐15和固定编号, 简单5保持原目录位置", () => {
    expect(calendarFields).toHaveLength(63)
    expect(recommendedFields).toHaveLength(15)
    expect(simpleFields).toEqual(["Dates", "Day", "MonthM", "YearMonthM", "FY00"])
    expect(calendarFields.map((item) => item.number)).toEqual(Array.from({ length: 63 }, (_, index) => index + 1))
    expect(calendarColumnName("Status", true)).toBe("C63_Status")
    expect(calendarColumnName("MonthM", false)).toBe("MonthM")
    const draft = createConfigurationDraft("zh-cn")
    expect(draft.calendar.fields).toEqual(recommendedFields)
    expect(draft.calendar.numberedColumns).toBe(true)
    expect(draft.calendar.workdays).toEqual([0, 1, 2, 3, 4])
    expect(calendarNeedsLunar(["Dates", "Status"])).toBe(false)
    expect(calendarNeedsLunar(["Holiday"])).toBe(true)
})
it("快捷字段方案也使用当前日期键命名, 编号选项不因未显式传fields而失效", () => {
    const options = { ...createConfigurationDraft("zh-cn").calendar, fields: undefined }
    expect(calendarDateColumn({ ...options, kind: "simple" })).toBe("C01_Dates")
    expect(calendarDateColumn({ ...options, kind: "full", numberedColumns: false })).toBe("Dates")
})
it.each(["Dates", "C01_Dates", "业务日期"])("已有日期键 %s 在未指定编号方式时保持名称, 显式切换才更新标准名", (name) => {
    const options = { ...createConfigurationDraft("zh-cn").calendar, fields: undefined, numberedColumns: undefined }
    const capability = {
        tableName: options.tableName,
        mode: "existing" as const,
        fields: ["Dates"],
        columns: [{ field: "Dates", name }],
        lockedFields: [],
        dateColumn: name,
    }
    expect(calendarDateColumn(options, capability)).toBe(name)
    expect(calendarDateColumn({ ...options, numberedColumns: true }, capability)).toBe(name === "Dates" ? "C01_Dates" : name)
    expect(calendarDateColumn({ ...options, numberedColumns: false }, capability)).toBe(name === "C01_Dates" ? "Dates" : name)
})
it("锁定集合只加入目标有效字段, 不回写用户通用方案且不重新编号", () => {
    const selected = ["Status"]
    expect(effectiveFields(selected, [{ field: "StartOfMonth", reason: "关系" }])).toEqual(["Dates", "StartOfMonth", "Status"])
    expect(selected).toEqual(["Status"])
})
it("导入预览区分新增相同冲突, 默认保留, 显式替换且不修改输入", () => {
    const existing = [{ date: "2025-01-01", isWorkday: false, name: "旧" }]
    const incoming = [existing[0]!, { date: "2025-01-02", isWorkday: true, name: "新增" }]
    expect(previewWorkdays(existing, incoming).map((row) => row.state)).toEqual(["same", "added"])
    const conflict = [{ ...existing[0]!, isWorkday: true, name: "新" }]
    expect(previewWorkdays(existing, conflict)[0]?.state).toBe("conflict")
    expect(mergeWorkdays(existing, conflict, false)).toEqual(existing)
    expect(mergeWorkdays(existing, conflict, true)).toEqual(conflict)
    expect(existing[0]?.name).toBe("旧")
})
it("导入最终数量和规范化UTF8预算整批拒绝", () => {
    const rows = Array.from({ length: 10001 }, (_, index) => ({ date: String(index), isWorkday: true, name: "" }))
    expect(workdayBudget(rows)).toBe("count")
    expect(workdayBudget([{ date: "2025-01-01", isWorkday: true, name: "中".repeat(180000) }])).toBe("bytes")
    expect(() => mergeWorkdays([], rows, false)).toThrow("WORKDAY_COUNT")
})
it("最终预算使用宿主White_Space修剪, 导入不静默折叠已有重复覆盖", () => {
    const rows = Array.from({ length: 2200 }, (_, index) => ({
        date: new Date(Date.UTC(2020, 0, index + 1)).toISOString().slice(0, 10),
        isWorkday: true,
        name: " ".repeat(190) + "a",
    }))
    expect(new TextEncoder().encode(JSON.stringify(rows)).length).toBeGreaterThan(512 * 1024)
    expect(workdayBudget(rows)).toBeUndefined()
    const duplicate = [
        { date: "2025-01-01", isWorkday: true, name: "一" },
        { date: "2025-01-01", isWorkday: false, name: "二" },
    ]
    expect(() => mergeWorkdays(duplicate, [{ date: "2025-01-02", isWorkday: true, name: "" }], false)).toThrow("WORKDAY_DUPLICATE")
    expect(duplicate).toHaveLength(2)
})
