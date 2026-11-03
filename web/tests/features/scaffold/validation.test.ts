/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\validation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证新建基础与启用配置的精确字段定位, 独立跳过和跨步骤依赖边界.
 */
import { afterEach, expect, it } from "vitest"
import { setLocale, t } from "@/i18n"
import { scaffoldMessages } from "@/i18n/scaffoldMessages"
import { validateScaffold, validateScaffoldBase, type ScaffoldValidationInput } from "@/features/scaffold/validation"
import type { ScaffoldCalendarOptions, ScaffoldPeriodOptions, TemplateMeasureDefinition, UdfSelection } from "@/features/scaffold/contracts"

it("显式字段公历不受旧full限制, 农历/重复/缺键/工作周和最终覆盖预算可定位", () => {
    const state = input()
    state.calendar = { ...calendar(), kind: "full", fields: ["Dates", "Status"], numberedColumns: false, workdays: [0, 2, 6], startYear: 1900, endYear: 2100 }
    expect(validateScaffold(state)).toBeUndefined()
    state.calendar.fields = ["Dates", "LunarYear"]
    expect(validateScaffold(state)?.field).toBe("startYear")
    state.calendar.fields = ["Dates", "Dates"]
    expect(validateScaffold(state)?.field).toBe("fields")
    state.calendar.fields = ["Status"]
    expect(validateScaffold(state)?.field).toBe("fields")
    state.calendar.fields = ["Dates"]
    state.calendar.workdays = [1, 1]
    expect(validateScaffold(state)?.field).toBe("workdays")
    state.calendar.workdays = []
    state.calendar.overrides = Array.from({ length: 10001 }, () => ({ date: "2025-01-01", isWorkday: true, name: "" }))
    expect(validateScaffold(state)?.key).toBe("scaffold.validationCalendarBudget")
})
it("工作日名称与宿主按200个UTF16字符及无控制/BOM一致检查", () => {
    const state = input()
    state.calendar = { ...calendar(), overrides: [{ date: "2025-01-01", isWorkday: true, name: "中".repeat(200) }] }
    expect(validateScaffold(state)).toBeUndefined()
    state.calendar.overrides[0]!.name = "换\n行"
    expect(validateScaffold(state)?.field).toBe("overrideName")
    state.calendar.overrides[0]!.name = "隐藏\uFEFF字符"
    expect(validateScaffold(state)?.field).toBe("overrideName")
})
it("显式新日期列使用实际编号开关, 去年同周算法不额外强迫输出辅助字段", () => {
    const state = input()
    state.calendar = { ...calendar(), fields: ["Dates"], numberedColumns: false }
    state.periods = { ...periods(), dateTable: state.calendar.tableName, dateColumn: "Dates", templates: ["PYWTD"] }
    state.periodDefinitions = [{ id: "PYWTD", group: "Week", label: "PYWTD", description: "", dependencies: [] }]
    expect(validateScaffold(state)).toBeUndefined()
    state.calendar.fields = ["Dates", "WeekNumber", "Year"]
    expect(validateScaffold(state)).toBeUndefined()
})

/** 构造已由宿主选定模板及目录的有效基础输入, 默认跳过所有可选配置. */
function input(): ScaffoldValidationInput {
    return {
        template: { id: "builtin:blank", name: "blank", source: "builtin", baseline: "a".repeat(64), description: "", warnings: [] },
        projectName: "My project",
        output: "C:/host-selected",
        sample: null,
        measures: null,
        calendar: null,
        periods: null,
        udfs: null,
        details: {
            tables: [
                {
                    name: "Calendar",
                    columns: [
                        { name: "Date", dataType: "DateTime" },
                        { name: "Text", dataType: "String" },
                    ],
                    measures: [],
                    isDate: true,
                },
                { name: "Measures", columns: [], measures: ["Revenue"], isDate: false },
            ],
            defaultDateTable: "Calendar",
            defaultDateColumn: "Date",
            defaultMeasureTable: "Measures",
        },
        periodDefinitions: [{ id: "AC", group: "Day", label: "AC", description: "", dependencies: [] }],
    }
}
/** 构造完整六字段度量值, 用于逐字段定位而不借用生产默认值. */
function measure(): TemplateMeasureDefinition {
    return { name: "Custom", value: "text", dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" }
}
/** 构造可单独启用的日期配置. */
function calendar(): ScaffoldCalendarOptions {
    return { kind: "simple", startYear: 2024, endYear: 2025, tableName: "NewCalendar", overrides: [] }
}
/** 构造绑定已读模板元数据的时间智能配置. */
function periods(): ScaffoldPeriodOptions {
    return {
        dateTable: "Calendar",
        dateColumn: "Date",
        measureTable: "Measures",
        baseMeasures: ["Revenue"],
        templates: ["AC"],
        folder: "Periods",
        numberDigits: 2,
        percentDigits: 2,
    }
}
/** 构造单函数稳定包选择, 真实网络和依赖身份由宿主复核. */
function udf(): UdfSelection {
    return { source: "daxlib", packageId: "Test.Library", version: "1.0.0", functions: ["Test.Fn"], baseline: null }
}

afterEach(() => setLocale("zh-cn"))

it("基础项就绪即可创建, 不要求访问任意可选步骤", () => {
    expect(validateScaffoldBase(input())).toBeUndefined()
    expect(validateScaffold(input())).toBeUndefined()
})
it.each([
    "",
    " ",
    "x".repeat(101),
    "CON",
    "con.txt",
    "PRN",
    "AUX",
    "NUL",
    "COM1.pbip",
    "lpt9",
    ".",
    "..",
    "project.",
    "project ",
    " project",
    "a/b",
    "a\\b",
    "a:b",
    'a"b',
    "a?b",
    "a|b",
    "a\u0000b",
    "a\u0085b",
])("基础项目名称完整匹配宿主单目录边界: %s", (projectName) => {
    expect(validateScaffoldBase({ ...input(), projectName })).toMatchObject({ step: "template", field: "projectName" })
})
it.each(["COM0", "COM10", "LPT0", "我的工程", "x".repeat(100), "a.b"])("合法目录名称不被设备名近似或长度边界误拦: %s", (projectName) => {
    expect(validateScaffoldBase({ ...input(), projectName })).toBeUndefined()
})
it("模板不可用、基线无效和未选目录分别定位基础字段", () => {
    expect(validateScaffoldBase({ ...input(), template: undefined })).toMatchObject({ step: "template", field: "templateId" })
    expect(validateScaffoldBase({ ...input(), template: { ...input().template!, baseline: "invalid" } })).toMatchObject({ field: "templateId" })
    expect(validateScaffoldBase({ ...input(), output: "" })).toMatchObject({ step: "template", field: "output" })
})

it.each([
    ["productCount", 0],
    ["productCount", 1.1],
    ["productCount", NaN],
    ["productCount", 10001],
    ["storeCount", 0],
    ["storeCount", Infinity],
    ["inventoryCycle", 4],
    ["inventoryCycle", 181],
    ["startDate", "2024-02-30"],
    ["endDate", ""],
    ["endDate", "2024-02-29"],
    ["seed", 2147483648],
    ["seed", -2147483649],
    ["seed", 1.5],
] as const)("模拟数据错误定位实际字段 %s=%s", (field, value) => {
    const state = input()
    state.sample = {
        locale: "zh-cn",
        productCount: 1,
        storeCount: 1,
        inventoryCycle: 5,
        startDate: "2024-01-01",
        endDate: "2024-03-01",
        [field]: value,
    } as typeof state.sample
    expect(validateScaffold(state)).toMatchObject({ step: "sample", field })
    state.sample = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("模拟数据60天与Int32种子边界接受, 结束年份偏移边界单独定位", () => {
    const state = input()
    state.sample = {
        locale: "en-us",
        productCount: 10000,
        storeCount: 10000,
        inventoryCycle: 180,
        startDate: "2024-01-01",
        endDate: "2024-03-01",
        seed: -2147483648,
    }
    expect(validateScaffold(state)).toBeUndefined()
    state.sample.seed = 2147483647
    expect(validateScaffold(state)).toBeUndefined()
    state.sample.startDate = "0060-01-01"
    state.sample.endDate = "0060-12-31"
    expect(validateScaffold(state)).toMatchObject({ step: "sample", field: "endDate" })
})

it.each([
    ["name", ""],
    ["name", "x".repeat(201)],
    ["name", "a\u007fb"],
    ["dataCategory", "Date"],
    ["value", "界".repeat(10923)],
    ["value", "a\u0001"],
    ["description", "界".repeat(2731)],
    ["displayFolder", "界".repeat(67)],
] as const)("模板度量值逐行定位 %s", (field, value) => {
    const state = input()
    state.measures = [measure(), { ...measure(), name: "Other", [field]: value }]
    expect(validateScaffold(state)).toMatchObject({ step: "measures", field, row: 2 })
    state.measures = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("度量值大小写重复指向后出现行, 关闭行仍保留后端配置校验", () => {
    const state = input()
    state.measures = [measure(), { ...measure(), name: "CUSTOM", enabled: false }]
    expect(validateScaffold(state)).toMatchObject({ step: "measures", field: "name", row: 2 })
    state.measures = [{ ...measure(), enabled: false, name: "" }]
    expect(validateScaffold(state)).toMatchObject({ field: "name", row: 1 })
    state.measures = [{ ...measure(), value: "x".repeat(32768), description: "\r\n\t", displayFolder: "界".repeat(66) }]
    expect(validateScaffold(state)).toBeUndefined()
})

it.each([
    ["kind", "unknown"],
    ["tableName", ""],
    ["startYear", NaN],
    ["startYear", 1899],
    ["endYear", 2101],
    ["endYear", 2023],
] as const)("日期设置错误定位 %s", (field, value) => {
    const state = input()
    state.calendar = { ...calendar(), [field]: value }
    expect(validateScaffold(state)).toMatchObject({ step: "calendar", field })
    state.calendar = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("复杂日期离线边界、真实日期、重复覆盖和UTF8说明均定位到具体行", () => {
    const state = input()
    state.calendar = { ...calendar(), kind: "full", startYear: 1999 }
    expect(validateScaffold(state)).toMatchObject({ field: "startYear" })
    state.calendar.startYear = 2000
    state.calendar.endYear = 2099
    expect(validateScaffold(state)).toBeUndefined()
    state.calendar.overrides = [
        { date: "2000-02-29", isWorkday: true, name: "" },
        { date: "2099-02-29", isWorkday: false, name: "" },
    ]
    expect(validateScaffold(state)).toMatchObject({ field: "overrideDate", row: 2 })
    state.calendar.overrides[1]!.date = "2000-02-29"
    expect(validateScaffold(state)).toMatchObject({ field: "overrideDate", row: 2 })
    state.calendar.overrides[1]!.date = "2099-01-01"
    state.calendar.overrides[1]!.name = "界".repeat(201)
    expect(validateScaffold(state)).toMatchObject({ field: "overrideName", row: 2 })
})

it.each([
    ["dateTable", "", "periodDateTable"],
    ["dateColumn", "", "periodDateColumn"],
    ["measureTable", "", "periodMeasureTable"],
    ["baseMeasures", [], "periodBaseMeasures"],
    ["templates", ["unknown"], "periodTemplates"],
    ["folder", "", "periodFolder"],
    ["folder", "界".repeat(67), "periodFolder"],
    ["numberDigits", 1.5, "periodNumberDigits"],
    ["percentDigits", 9, "periodPercentDigits"],
] as const)("时间智能独立开启时定位 %s", (field, value, expected) => {
    const state = input()
    state.periods = { ...periods(), [field]: value } as ScaffoldPeriodOptions
    expect(validateScaffold(state)).toMatchObject({ step: "calendar", field: expected })
    state.periods = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("时间智能显式空模板保留有效绑定以清空输出, 关闭段仍跳过旧草稿", () => {
    const state = input()
    state.periods = { ...periods(), templates: [] }
    expect(validateScaffold(state)).toBeUndefined()
    state.periods.dateColumn = "Missing"
    expect(validateScaffold(state)).toMatchObject({ field: "periodDateColumn" })
    state.periods = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("已读元数据能确定失效绑定, 未读元数据不猜测模型是否存在", () => {
    const state = input()
    state.periods = periods()
    state.periods.dateTable = "Missing"
    expect(validateScaffold(state)).toMatchObject({ field: "periodDateTable" })
    state.periods.dateTable = "Calendar"
    state.periods.dateColumn = "Text"
    expect(validateScaffold(state)).toMatchObject({ field: "periodDateColumn" })
    state.periods.dateColumn = "Date"
    state.periods.measureTable = "Missing"
    expect(validateScaffold(state)).toMatchObject({ field: "periodMeasureTable" })
    state.periods.measureTable = "Measures"
    state.periods.baseMeasures = ["Missing"]
    expect(validateScaffold(state)).toMatchObject({ field: "periodBaseMeasures" })
    state.details = undefined
    expect(validateScaffold(state)).toBeUndefined()
})
it("时间智能只依赖启用日历及度量值, 关闭后不沿用隐藏草稿作为有效身份", () => {
    const state = input()
    state.periods = periods()
    state.calendar = calendar()
    state.measures = [measure()]
    state.periods.dateTable = "NewCalendar"
    state.periods.dateColumn = "C01_Dates"
    state.periods.baseMeasures = ["Custom"]
    expect(validateScaffold(state)).toBeUndefined()
    state.calendar = null
    expect(validateScaffold(state)).toMatchObject({ field: "periodDateTable" })
    state.periods.dateTable = "Calendar"
    state.periods.dateColumn = "Date"
    state.measures = null
    expect(validateScaffold(state)).toMatchObject({ field: "periodBaseMeasures" })
})

it.each([
    ["source", "https://example.com"],
    ["packageId", "../package"],
    ["version", "^1.0.0"],
    ["functions", []],
] as const)("UDF选择错误定位 %s 并要求重新选择", (field, value) => {
    const state = input()
    state.udfs = [{ ...udf(), [field]: value } as UdfSelection]
    expect(validateScaffold(state)).toMatchObject({ step: "udfs", field, row: 1 })
    state.udfs = null
    expect(validateScaffold(state)).toBeUndefined()
})
it("UDF空集合、重复包、自定义来源基线和本地基线有确定定位", () => {
    const state = input()
    state.udfs = []
    expect(validateScaffold(state)).toMatchObject({ step: "udfs", field: "udfs" })
    state.udfs = [udf(), { ...udf(), packageId: "TEST.LIBRARY" }]
    expect(validateScaffold(state)).toMatchObject({ field: "packageId", row: 2 })
    state.udfs = [{ ...udf(), source: "source:" + "1".repeat(32) }]
    expect(validateScaffold(state)).toMatchObject({ field: "sourceBaseline", row: 1 })
    state.udfs[0]!.sourceBaseline = "a".repeat(64)
    expect(validateScaffold(state)).toBeUndefined()
    state.udfs = [{ ...udf(), source: "local", packageId: "2".repeat(32) }]
    expect(validateScaffold(state)).toMatchObject({ field: "baseline", row: 1 })
    state.udfs[0]!.baseline = "b".repeat(64)
    expect(validateScaffold(state)).toBeUndefined()
})
it("明确固定的预发行直接通过校验, 单函数与多函数共用精确版本策略", () => {
    const state = input()
    state.udfs = [{ ...udf(), version: "1.0.0-beta.1" }]
    expect(validateScaffold(state)).toBeUndefined()
    state.udfs = [{ ...udf(), version: "1.0.0-beta.1", functions: ["Test.Fn", "Test.Other"] }]
    expect(validateScaffold(state)).toBeUndefined()
})

it.each(["1.0.0-beta", "1.0.0-beta+build.1"])("本地函数自身的预发行版本无需远端包许可: %s", (version) => {
    const state = input()
    state.udfs = [{ ...udf(), source: "local", packageId: "2".repeat(32), baseline: "b".repeat(64), version }]
    expect(validateScaffold(state)).toBeUndefined()
    state.udfs.push({ ...udf(), packageId: "Other.Library", functions: ["Other.Fn"] })
    expect(validateScaffold(state)).toBeUndefined()
})

it("自定义远端来源的精确预发行与官方来源共用自动依赖规则", () => {
    const state = input()
    state.udfs = [{ ...udf(), source: "source:" + "1".repeat(32), sourceBaseline: "a".repeat(64), version: "1.0.0-beta" }]
    expect(validateScaffold(state)).toBeUndefined()
})
it("完整参数纯校验不改写草稿, 所有issue文案双语完整且不泄漏身份值", () => {
    const state = input()
    state.sample = { locale: "zh-cn", productCount: 0, storeCount: 1, inventoryCycle: 5, startDate: "2024-01-01", endDate: "2024-03-01" }
    const before = JSON.stringify(state),
        issue = validateScaffold(state)!
    for (const locale of ["zh-cn", "en-us"] as const) {
        setLocale(locale)
        expect(t(issue.key, issue.params)).not.toBe(issue.key)
        const keys = Object.keys(scaffoldMessages[locale]).filter((key) => key.startsWith("scaffold.validation"))
        expect(keys.length).toBeGreaterThanOrEqual(25)
        for (const key of keys) expect(t(key)).not.toBe(key)
    }
    expect(JSON.stringify(state)).toBe(before)
    expect(JSON.stringify(issue)).not.toContain("host-selected")
})

it("宿主允许省略的覆盖与版本使用默认值, 来源null和省略不被误判为身份冲突", () => {
    const state = input()
    state.calendar = { ...calendar(), overrides: null as never }
    state.udfs = [
        { ...udf(), version: undefined as never, sourceBaseline: null },
        { ...udf(), packageId: "Other.Library", functions: ["Other.Fn"] },
    ]
    expect(validateScaffold(state)).toBeUndefined()
})

it("列表上限、逐日布尔值和新增期间名称长度均保留确定字段", () => {
    const state = input()
    state.measures = Array.from({ length: 129 }, (_, index) => ({ ...measure(), name: `Measure${index}` }))
    expect(validateScaffold(state)).toMatchObject({ step: "measures", field: "measures" })
    state.measures = null
    state.calendar = { ...calendar(), overrides: [{ date: "2024-01-01", name: "", isWorkday: null as never }] }
    expect(validateScaffold(state)).toMatchObject({ step: "calendar", field: "overrideIsWorkday", row: 1 })
    state.calendar.overrides = Array.from({ length: 36601 }, () => ({ date: "2024-01-01", name: "", isWorkday: true }))
    expect(validateScaffold(state)).toMatchObject({ field: "overrides" })
    state.calendar = null
    state.periods = { ...periods(), baseMeasures: ["Revenue", "REVENUE"] }
    expect(validateScaffold(state)).toMatchObject({ field: "periodBaseMeasures" })
    state.periods.baseMeasures = ["x".repeat(200)]
    expect(validateScaffold(state)).toMatchObject({ field: "periodBaseMeasures", key: "scaffold.validationPeriodName" })
    state.periods = null
    state.udfs = [{ ...udf(), functions: Array.from({ length: 129 }, (_, index) => `Fn${index}`) }]
    expect(validateScaffold(state)).toMatchObject({ step: "udfs", field: "functions", row: 1 })
})

it("名称保留合法标点与Unicode, 不按多字符大小写展开或JavaScript特有空白改写身份", () => {
    const state = input()
    state.projectName = "\ufeffMyProject"
    state.measures = [
        { ...measure(), name: "ß" },
        { ...measure(), name: "SS" },
        { ...measure(), name: "A[B]'C" },
    ]
    expect(validateScaffold(state)).toBeUndefined()
})
