/**
 * FilePath    : pbi-utils\web\tests\features\configuration\draft.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证目录初始化的必需字段、目标锁与用户已有草稿边界.
 */
import { expect, it } from "vitest"
import { configurationRequest, configurationSections, createConfigurationDraft, initializeConfigurationCatalog } from "@/features/configuration/draft"
import { calendarIssue } from "@/features/scaffold/validationCalendar"
import type { ModelConfigurationCatalog, ScaffoldPeriodOptions } from "@/features/scaffold/contracts"

/** 提供真实 existing 形状的最小目录, 不授予生成归属或模型写权限. */
function catalog(fields: string[], lockedFields = [{ field: "Dates", reason: "existingReference" }]): ModelConfigurationCatalog {
    return {
        tables: [],
        defaultDateTable: null,
        defaultDateColumn: null,
        defaultMeasureTable: null,
        calendarCapabilities: [{ tableName: "Calendar", mode: "existing", fields, columns: [], lockedFields }],
    }
}

it("模型日期与期间独立提交, 新建仍组合提交且关闭段保留草稿", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.calendarEnabled = true
    draft.periodsEnabled = true
    draft.periods.folder = "保留期间草稿"
    expect(configurationRequest(draft, "zh-cn", "calendar")).toMatchObject({ calendar: draft.calendar, periods: null })
    expect(configurationRequest(draft, "zh-cn", "periods")).toMatchObject({ calendar: null, periods: draft.periods })
    expect(configurationSections(draft, "zh-cn")).toMatchObject({ calendar: draft.calendar, periods: draft.periods })
    draft.periodsEnabled = false
    expect(configurationRequest(draft, "zh-cn", "periods").periods).toBeNull()
    expect(draft.periods.folder).toBe("保留期间草稿")
})

it("已有表无可识别字段时草稿仍包含界面必需日期键, 换新表名无需重新选择方案", () => {
    const draft = createConfigurationDraft("zh-cn")
    initializeConfigurationCatalog(draft, catalog([]))
    expect(draft.calendar.fields).toEqual(["Dates"])
    draft.calendar.tableName = "Audit_Calendar"
    expect(calendarIssue(draft.calendar)).toBeUndefined()
})

it("目录初始字段缺少锁定字段时将其保留在提交草稿, 不修改宿主目录", () => {
    const draft = createConfigurationDraft("zh-cn")
    const details = catalog(["Year"], [{ field: "StartOfMonth", reason: "existingReference" }])
    initializeConfigurationCatalog(draft, details)
    expect(draft.calendar.fields).toEqual(["Dates", "Year", "StartOfMonth"])
    expect(details.calendarCapabilities?.[0]?.fields).toEqual(["Year"])
    expect(calendarIssue(draft.calendar)).toBeUndefined()
})

it.each([["Dates", "Status"], ["UnknownField"], ["Dates", "Dates"]].map((fields) => ({ fields })))(
    "目录初始化不覆盖已经编辑的字段草稿 $fields",
    ({ fields }) => {
        const draft = createConfigurationDraft("zh-cn")
        draft.calendar.fields = [...fields]
        initializeConfigurationCatalog(draft, catalog(["Dates", "Year"]))
        expect(draft.calendar.fields).toEqual(fields)
        expect(calendarIssue(draft.calendar)?.field).toBe(fields.includes("Status") ? undefined : "fields")
    },
)

it("已有日期草稿编辑后重新绑定目录不恢复原字段方案", () => {
    const draft = createConfigurationDraft("zh-cn")
    initializeConfigurationCatalog(draft, catalog(["Dates", "Year"]))
    draft.calendar.fields = ["Dates", "Status"]
    initializeConfigurationCatalog(draft, catalog(["Dates", "Year"]))
    expect(draft.calendar.fields).toEqual(["Dates", "Status"])
})

it("新建模板切换仍合并新目标锁, 不覆盖用户主动选择", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.calendar.fields = ["Dates", "Status"]
    const details = catalog([], [{ field: "StartOfMonth", reason: "templateReference" }])
    details.calendarCapabilities![0]!.mode = "new"
    initializeConfigurationCatalog(draft, details)
    expect(draft.calendar.fields).toEqual(["Dates", "StartOfMonth", "Status"])
})

it("宿主已核验时间智能配置回填真实选择, 后续目录刷新保留当前编辑", () => {
    const draft = createConfigurationDraft("zh-cn")
    const periods: ScaffoldPeriodOptions = {
        dateTable: "日期",
        dateColumn: "Date",
        measureTable: "指标",
        baseMeasures: ["销售额"],
        templates: ["YTD"],
        folder: "已配置目录",
        numberDigits: 3,
        percentDigits: 1,
    }
    const details = { ...catalog([]), periods } as ModelConfigurationCatalog & { periods: ScaffoldPeriodOptions }
    initializeConfigurationCatalog(draft, details)
    expect(draft.periodsEnabled).toBe(true)
    expect(draft.periods).toEqual(periods)
    draft.periods.templates = []
    draft.periods.folder = "当前编辑"
    initializeConfigurationCatalog(draft, details)
    expect(draft.periods.templates).toEqual([])
    expect(draft.periods.folder).toBe("当前编辑")
    expect(periods.templates).toEqual(["YTD"])
})
