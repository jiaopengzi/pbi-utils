/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.activation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型配置无需段落开关, 旧草稿保留并独立应用, 只读和在途锁仍生效.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import type { BridgeClient, ConfigurationContext, Methods } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import MeasuresEditor from "@/features/scaffold/MeasuresEditor.vue"
import { createConfigurationDraft, type ConfigurationOperation } from "@/features/configuration/draft"
import { configurationDrafts } from "@/features/configuration/session"
import { bindTemplateMeasureDraft } from "@/features/configuration/templateMeasureDraft"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"

const mounted: VueWrapper[] = []
afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount()
    vi.useRealTimers()
})

/** 以宿主边界替身提供目标和只读目录, 字段、锁及提交校验均运行真实组件. */
async function harness(operation: ConfigurationOperation = "measures", state = "editable", cached = true) {
    vi.useFakeTimers()
    const context: ConfigurationContext = {
        targetId: "activation-target",
        project: { ...structuredClone(previewProject), hasPendingChanges: state === "pending" },
        details: {
            tables: [
                { name: "Dates", columns: [{ name: "Date", dataType: "DateTime" }], measures: [], isDate: true },
                { name: "Sales", columns: [], measures: ["Revenue"], isDate: false },
            ],
            defaultDateTable: "Dates",
            defaultDateColumn: "Date",
            defaultMeasureTable: "Sales",
        },
        canWrite: state !== "readonly",
        canGenerateSample: false,
        canUseMeasureConfig: false,
        measureConfiguration: {
            mode: "tmdl",
            measures: [{ name: "SourceFact", value: "1", enabled: true, dataCategory: "Uncategorized", description: "宿主原值", displayFolder: "" }],
        },
        measureConfigurationRevision: null,
        configMeasures: null,
        measureConfigUnavailableReason: "layout",
        isCreatedTarget: false,
        isSimulation: true,
    }
    const applications: Methods["configuration.apply"]["params"][] = []
    const request = vi.fn((method: string, params: unknown) => {
        let result: unknown
        if (method === "scaffold.templates") result = new ScaffoldMock().handle(method, {})
        else if (method === "configuration.context") result = structuredClone(context)
        else if (method === "calendar.preferences") result = { fields: ["Dates"], numberedColumns: true }
        else if (method === "udf.sources") result = { items: [] }
        else if (method === "configuration.apply") {
            applications.push(JSON.parse(JSON.stringify(params)) as Methods["configuration.apply"]["params"])
            result = structuredClone(context)
        } else if (method === "request.cancel") result = { accepted: true }
        else throw new Error(`Unexpected activation request: ${method}`)
        return { id: `${method}-${request.mock.calls.length}`, promise: Promise.resolve(result) }
    })
    const bridge = markRaw({ request } as unknown as BridgeClient)
    const draft = createConfigurationDraft("zh-cn")
    // 缓存表达已从宿主读取后发生的未保存编辑, 先建立来源基线再写入用户旧草稿.
    bindTemplateMeasureDraft(draft, context, [])
    draft.measures = [{ name: "SavedDraft", value: "1", enabled: false, dataCategory: "Uncategorized", description: "保留说明", displayFolder: "" }]
    draft.periods = { ...draft.periods, dateTable: "Dates", dateColumn: "Date", measureTable: "Sales", baseMeasures: ["Revenue"], templates: ["D1"] }
    draft.udfs = [{ source: "local", packageId: "a".repeat(32), functions: ["Saved.Function"], baseline: "b".repeat(64), version: "1.0.0" }]
    if (cached) configurationDrafts(bridge).set(context.targetId, draft)
    const wrapper = mount(ConfigurationPanel, {
        props: {
            bridge,
            project: context.project,
            section: operation === "periods" ? "calendar" : operation,
            active: true,
            externalLocked: state === "external",
        },
        global: { stubs: { CodeEditor: { props: ["modelValue"], template: "<pre>{{ modelValue }}</pre>" } } },
    })
    mounted.push(wrapper)
    await flushPromises()
    if (operation === "periods") await wrapper.get('[role="tab"][id$="-periods"]').trigger("click")
    const labels = { measures: "写入模板度量值", calendar: "应用日期表", periods: "应用时间智能", udfs: "导入所选 UDF", sample: "应用模拟数据" }
    const button = wrapper.findAll("button").find((item) => item.text() === labels[operation])!
    return { wrapper, draft, applications, button, bridge, context }
}

it.each(["measures", "calendar", "periods", "udfs"] as const)("旧草稿关闭的 %s 在模型页直接可编辑并独立应用, 不显示段落开关", async (operation) => {
    const { wrapper, draft, applications, button } = await harness(operation)
    expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
    expect(wrapper.find('section[id$="-udfs"] [data-optional]').exists()).toBe(false)
    expect(wrapper.find("[data-calendar-panel] [data-optional]").exists()).toBe(false)
    expect(wrapper.find('section[id$="-sample"] [data-optional]').exists()).toBe(true)
    expect([draft.measuresEnabled, draft.calendarEnabled, draft.periodsEnabled, draft.udfsEnabled]).toEqual([true, true, true, true])
    expect(draft.sampleEnabled).toBe(false)
    if (operation === "measures") {
        const row = wrapper.get(".scaffold-measures tbody tr")
        expect(row.get("input").attributes("disabled")).toBeUndefined()
        expect((row.get('input[role="switch"]').element as HTMLInputElement).checked).toBe(false)
        await row.get("input").setValue("EditedDraft")
    } else if (operation === "udfs") expect(wrapper.getComponent(UdfStep).props("disabled")).toBe(false)
    else expect(wrapper.getComponent(CalendarStep).props(operation === "calendar" ? "calendarEnabled" : "periodsEnabled")).toBe(true)
    if (operation !== "calendar") draft.calendar.endYear = draft.calendar.startYear - 1
    if (operation !== "measures") draft.measures[0]!.name = ""
    await button.trigger("click")
    await flushPromises()
    expect(applications).toHaveLength(1)
    const request = applications[0]!.request
    for (const section of ["sample", "measures", "calendar", "periods", "udfs"] as const) expect(request[section] === null).toBe(section !== operation)
    if (operation === "measures") expect(request.measures).toMatchObject([{ name: "EditedDraft", enabled: false, description: "保留说明" }])
    expect(draft.udfs[0]?.functions).toEqual(["Saved.Function"])
})

it("新模型的模板度量直接启用, 每行启用开关仍保留", async () => {
    const { wrapper, button } = await harness("measures", "editable", false)
    expect(wrapper.getComponent(MeasuresEditor).props("disabled")).toBe(false)
    expect(wrapper.findAll('.scaffold-measures input[role="switch"]').length).toBeGreaterThan(0)
    expect(button.attributes("disabled")).toBeUndefined()
})

it.each(
    (["measures", "calendar", "periods", "udfs"] as const).flatMap((operation) => ["readonly", "pending", "external"].map((state) => ({ operation, state }))),
)("模型 $operation 默认启用仍服从 $state 限制", async ({ operation, state }) => {
    const { wrapper, applications, button } = await harness(operation, state)
    const fields = {
        measures: ".scaffold-measures tbody tr input",
        calendar: '[data-scaffold-field="tableName"] input',
        periods: '[data-scaffold-field="periodFolder"] input',
        udfs: '[data-test="udf-source"] input',
    }
    expect(wrapper.get(fields[operation]).attributes("disabled")).toBeDefined()
    expect(button.attributes("disabled")).toBeDefined()
    await button.trigger("click")
    await flushPromises()
    expect(applications).toHaveLength(0)
})

it("模型四段草稿在同目标刷新、页面重挂、换目标再切回时保留, 段内 false 不被启用归一覆盖", async () => {
    const fixture = await harness("calendar")
    const { draft, bridge, context } = fixture
    draft.calendar.numberedColumns = false
    draft.calendar.overrides = [{ date: "2026-10-01", isWorkday: false, name: "保留覆盖" }]
    draft.periods.templates = []
    draft.periods.folder = "保留期间目录"
    const firstProject = { ...context.project }
    fixture.wrapper.unmount()
    mounted.splice(mounted.indexOf(fixture.wrapper), 1)
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge, project: firstProject, section: "calendar", active: true },
        global: { stubs: { CodeEditor: { props: ["modelValue"], template: "<pre>{{ modelValue }}</pre>" } } },
    })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.getComponent(ConfigurationFields).props("draft")).toBe(draft)
    context.project = { ...firstProject, baselineId: "same-target-refreshed" }
    await wrapper.setProps({ project: context.project })
    await flushPromises()
    expect(wrapper.getComponent(ConfigurationFields).props("draft")).toBe(draft)
    context.targetId = "other-target"
    context.project = { ...firstProject, projectId: "other-project", baselineId: "other-baseline" }
    await wrapper.setProps({ project: context.project })
    await flushPromises()
    const other = wrapper.getComponent(ConfigurationFields).props("draft")
    expect(other).not.toBe(draft)
    expect(other.calendar.overrides).toEqual([])
    other.periods.folder = "其他目标目录"
    context.targetId = "activation-target"
    context.project = { ...firstProject, baselineId: "first-target-returned" }
    await wrapper.setProps({ project: context.project })
    await flushPromises()
    expect(wrapper.getComponent(ConfigurationFields).props("draft")).toBe(draft)
    expect([draft.measuresEnabled, draft.calendarEnabled, draft.periodsEnabled, draft.udfsEnabled]).toEqual([true, true, true, true])
    expect(draft.measures[0]?.enabled).toBe(false)
    expect(draft.calendar.numberedColumns).toBe(false)
    expect(draft.calendar.overrides).toEqual([{ date: "2026-10-01", isWorkday: false, name: "保留覆盖" }])
    expect(draft.periods).toMatchObject({ templates: [], folder: "保留期间目录" })
    expect(draft.udfs[0]).toMatchObject({ functions: ["Saved.Function"] })
})
