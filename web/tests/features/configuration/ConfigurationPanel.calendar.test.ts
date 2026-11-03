/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.calendar.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用真实模型配置表单验证日期字段连续切换、宿主锁、预览及应用参数一致性.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import { setLocale } from "@/i18n"
import * as messageUi from "@/ui/message"
import { ElCheckbox, ElOption } from "element-plus"
import { BridgeClient, type ConfigurationContext, type Methods, type Transport } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import PeriodsFields from "@/features/scaffold/PeriodsFields.vue"
import UiTabs from "@/ui/UiTabs.vue"
import CalendarFieldPicker from "@/features/scaffold/CalendarFieldPicker.vue"
import { calendarColumnName, calendarFields, recommendedFields, simpleFields } from "@/features/scaffold/calendarFields"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import type { CalendarPreferences } from "@/features/scaffold/contracts"

type HostRequest = { id: string; method: keyof Methods; params: Record<string, unknown> }
const fullFields = calendarFields.map((field) => field.id)
const mounted: { wrapper: VueWrapper; bridge: BridgeClient }[] = []

afterEach(() => {
    for (const { wrapper, bridge } of mounted.splice(0)) {
        wrapper.unmount()
        bridge.dispose()
    }
    vi.useRealTimers()
    setLocale("zh-cn")
    vi.restoreAllMocks()
})

/** 返回已有全量日期表及宿主指定保护字段, 列别名用于验证显示不受新字段编号影响. */
function calendarContext(protectedFields: string[]): ConfigurationContext {
    const columns = calendarFields.map((field) => ({ field: field.id, name: field.id === "Dates" ? "原日期键" : calendarColumnName(field.id, true) }))
    return {
        targetId: "calendar-host-target",
        project: { ...previewProject, projectId: "calendar-project", baselineId: "calendar-baseline-1" },
        details: {
            tables: [
                {
                    name: "Calendar",
                    columns: columns.map((column, index) => ({ ...column, dataType: calendarFields[index]!.dataType })),
                    measures: [],
                    isDate: true,
                },
                { name: "Sales", columns: [], measures: ["Revenue"], isDate: false },
            ],
            defaultDateTable: "Calendar",
            defaultDateColumn: "原日期键",
            defaultMeasureTable: null,
            calendarFields,
            calendarCapabilities: [
                {
                    tableName: "Calendar",
                    mode: "existing",
                    fields: [...fullFields],
                    columns,
                    dateColumn: "原日期键",
                    lockedFields: protectedFields.map((field) => ({ field, reason: "existingReference" })),
                },
            ],
        },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
        isSimulation: true,
    }
}

/** 只控制宿主响应与提交门闩, 所有勾选、草稿、预览失效及应用行为由真实组件执行. */
class CalendarTransport implements Transport {
    context: ConfigurationContext
    previews: Methods["calendar.preview"]["params"][] = []
    preferences: CalendarPreferences[] = []
    applications: Methods["configuration.apply"]["params"][] = []
    holdPreview = false
    heldPreview?: { request: HostRequest; result: unknown }
    holdApply = false
    noChanges = false
    heldApply?: HostRequest
    private listeners = new Set<(event: MessageEvent) => void>()

    /** protectedFields 是宿主核定锁集合, 不在测试中重新实现后端依赖扫描. */
    constructor(protectedFields: string[], initialFields: string[] = fullFields) {
        this.context = calendarContext(protectedFields)
        this.context.details.calendarCapabilities![0]!.fields = initialFields
    }

    /** 订阅真实 BridgeClient 的协议入口. */
    addEventListener(_type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.add(listener)
    }

    /** 页面结束时释放同一协议入口. */
    removeEventListener(_type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.delete(listener)
    }

    /** 快照请求避免后续草稿修改改变已发送证据, 未声明能力明确拒绝. */
    postMessage(message: unknown): void {
        const request = JSON.parse(JSON.stringify(message)) as HostRequest
        switch (request.method) {
            case "scaffold.templates":
                this.reply(request, new ScaffoldMock().handle(request.method, {}))
                break
            case "configuration.context":
                this.reply(request, this.context)
                break
            case "calendar.preferences":
                this.reply(request, { fields: ["Dates", "Status"], numberedColumns: false })
                break
            case "calendar.savePreferences":
                this.preferences.push(request.params as unknown as CalendarPreferences)
                this.reply(request, request.params)
                break
            case "calendar.preview": {
                const params = request.params as unknown as Methods["calendar.preview"]["params"]
                this.previews.push(params)
                const result = {
                    functionName: "BoundCalendar",
                    functionExpression: "(StartYear as number) => #table({}, {})",
                    invocationExpression: "BoundCalendar(2026)",
                    inputDigest: "a".repeat(64),
                    contextBaseline: params.context.kind === "model" ? params.context.baselineId : params.context.templateBaseline,
                    generatorVersion: "calendar-test",
                    isSimulation: true,
                }
                if (this.holdPreview) this.heldPreview = { request, result }
                else this.reply(request, result)
                break
            }
            case "configuration.apply":
                this.applications.push(request.params as unknown as Methods["configuration.apply"]["params"])
                this.heldApply = request
                if (!this.holdApply) this.completeApply()
                break
            case "request.cancel":
                this.reply(request, { accepted: true })
                break
            default:
                throw new Error(`Unexpected calendar test method: ${request.method}`)
        }
    }

    /** 结束独立只读预览, 用于验证预览等待期间表单仍允许主动选择和应用. */
    completePreview(): void {
        if (!this.heldPreview) throw new Error("No held calendar preview")
        this.reply(this.heldPreview.request, this.heldPreview.result)
        this.heldPreview = undefined
    }

    /** 用同一目标和新基线返回全量能力目录, 验证回读不会以目录覆盖已编辑草稿. */
    completeApply(): void {
        if (!this.heldApply) throw new Error("No held calendar application")
        this.context = {
            ...this.context,
            noChanges: this.noChanges,
            project: { ...this.context.project, baselineId: `calendar-baseline-${this.applications.length + 1}` },
        }
        this.reply(this.heldApply, this.context)
        this.heldApply = undefined
    }

    /** 仅返回显式模拟响应, 不把字段数量作为真实模型写入证据. */
    private reply(request: HostRequest, result: unknown): void {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data: { protocolVersion: 1, id: request.id, result } }))
    }
}

/** 挂载真实模型页表单和品牌控件, 只替换与本任务无关的代码编辑器绘制. */
async function harness(protectedFields = ["Dates"], initialFields = fullFields) {
    vi.useFakeTimers()
    const transport = new CalendarTransport(protectedFields, initialFields)
    const bridge = new BridgeClient(transport)
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: markRaw(bridge), project: transport.context.project, section: "calendar", active: true },
        global: { stubs: { CodeEditor: { props: ["modelValue"], template: "<pre>{{ modelValue }}</pre>" } } },
    })
    mounted.push({ wrapper, bridge })
    await flushPromises()
    const picker = wrapper.findComponent(CalendarFieldPicker)
    const calendar = wrapper.findComponent(CalendarStep).props("calendar")
    /** 固定业务文案定位真实可见按钮, 缺少动作时让用例明确失败. */
    function button(label: string) {
        const result = wrapper.findAll("button").find((item) => item.text() === label)
        if (!result) throw new Error(`Missing calendar action: ${label}`)
        return result
    }
    /** 通过真实 Element Plus checkbox 值定位可操作控件, 不直接触发组件私有处理器. */
    function field(id: string) {
        const result = picker.findAllComponents(ElCheckbox).find((item) => item.props("value") === id)
        if (!result) throw new Error(`Missing calendar field: ${id}`)
        return result.get("input")
    }
    /** 等待只读预览防抖并比较发送参数, 通用偏好必须与目标锁分离. */
    async function expectSelection(fields: string[], preferenceFields: string[]) {
        expect(calendar.fields).toEqual(fields)
        expect(picker.findAll(".calendar-fields__choice input:checked")).toHaveLength(fields.length)
        expect(transport.preferences.at(-1)).toEqual({ fields: preferenceFields, numberedColumns: true })
        await vi.advanceTimersByTimeAsync(300)
        await flushPromises()
        expect(transport.previews.at(-1)).toMatchObject({
            context: { kind: "model", targetId: transport.context.targetId, baselineId: transport.context.project.baselineId },
            calendar: { fields, numberedColumns: true },
        })
        expect(button("应用日期表").attributes("disabled")).toBeUndefined()
    }
    /** 只发一次配置请求并核对字段和基线, 同目标回读后仍保留同一个草稿对象. */
    async function apply(fields: string[]) {
        const baseline = transport.context.project.baselineId
        await button("应用日期表").trigger("click")
        await flushPromises()
        expect(transport.applications.at(-1)).toMatchObject({
            targetId: transport.context.targetId,
            baselineId: baseline,
            section: "calendar",
            request: { calendar: { fields }, periods: null, measures: null, sample: null, udfs: null },
        })
        expect(wrapper.findComponent(CalendarStep).props("calendar")).toBe(calendar)
        expect(calendar.fields).toEqual(fields)
    }
    return { wrapper, transport, picker, calendar, button, field, expectSelection, apply }
}

it("已有模板日期表映射为空时显示与草稿均保留日期键, 改新表名可直接应用", async () => {
    const { wrapper, transport, picker, calendar, apply } = await harness(["Dates"], [])
    expect(picker.findAll(".calendar-fields__choice input:checked")).toHaveLength(1)
    expect(calendar.fields).toEqual(["Dates"])
    await wrapper.get('[data-scaffold-field="tableName"] input').setValue("Audit_Calendar")
    await apply(["Dates"])
    expect(transport.applications).toHaveLength(1)
    expect(transport.applications[0]!.request.calendar?.tableName).toBe("Audit_Calendar")
})

it("已有63字段连续切换简单5、推荐15、全量及单项, 预览和应用只发送当前字段", async () => {
    const { wrapper, transport, picker, calendar, button, field, expectSelection, apply } = await harness()
    expect(calendar.fields).toEqual(fullFields)
    expect(calendar.numberedColumns).toBe(true)
    expect(picker.text()).toContain("原日期键")
    expect(field("Dates").attributes("disabled")).toBeDefined()
    expect(field("LunarYear").attributes("disabled")).toBeUndefined()
    transport.holdPreview = true
    await button("简单 5 字段").trigger("click")
    await expectSelection(simpleFields, simpleFields)
    expect(transport.heldPreview).toBeDefined()
    expect(wrapper.attributes("aria-busy")).toBe("false")
    expect(field("Year").attributes("disabled")).toBeUndefined()
    transport.completePreview()
    transport.holdPreview = false
    await flushPromises()
    await apply(simpleFields)
    await button("推荐 15 字段").trigger("click")
    await expectSelection(recommendedFields, recommendedFields)
    await apply(recommendedFields)
    await button("全部字段").trigger("click")
    await expectSelection(fullFields, fullFields)
    await apply(fullFields)
    await field("LunarYear").setValue(false)
    const selected = fullFields.filter((id) => id !== "LunarYear")
    await expectSelection(selected, selected)
    transport.holdApply = true
    await apply(selected)
    expect(wrapper.attributes("aria-busy")).toBe("true")
    expect(field("Year").attributes("disabled")).toBeDefined()
    expect(button("简单 5 字段").attributes("disabled")).toBeDefined()
    expect(transport.applications).toHaveLength(4)
    transport.completeApply()
    await flushPromises()
    expect(wrapper.attributes("aria-busy")).toBe("false")
    expect(calendar.fields).toEqual(selected)
    expect(field("Year").attributes("disabled")).toBeUndefined()
})

it("模型引用字段在快捷方案和单项取消后保持锁定, 通用偏好不包含目标附加字段", async () => {
    const { calendar, button, field, expectSelection, apply } = await harness(["Dates", "StartOfMonth"])
    await button("简单 5 字段").trigger("click")
    const simpleWithRequired = [...simpleFields, "StartOfMonth"]
    await expectSelection(simpleWithRequired, simpleFields)
    expect(field("StartOfMonth").attributes("disabled")).toBeDefined()
    await apply(simpleWithRequired)
    await button("推荐 15 字段").trigger("click")
    const recommendedWithRequired = [...recommendedFields.slice(0, -1), "StartOfMonth", "Status"]
    await expectSelection(recommendedWithRequired, recommendedFields)
    await field("Day").setValue(false)
    const selected = recommendedWithRequired.filter((id) => id !== "Day")
    await expectSelection(
        selected,
        recommendedFields.filter((id) => id !== "Day"),
    )
    await apply(selected)
    expect(calendar.fields).toEqual(selected)
    expect(field("StartOfMonth").attributes("disabled")).toBeDefined()
})

it("日期和时间智能动作位于各自页签, 独立提交并保留另一段无效草稿", async () => {
    const { wrapper, transport, calendar, button } = await harness()
    const step = wrapper.getComponent(CalendarStep)
    const draft = wrapper.getComponent(ConfigurationFields).props("draft")
    Object.assign(draft.periods, {
        dateTable: "Calendar",
        dateColumn: "原日期键",
        measureTable: "Sales",
        baseMeasures: ["Revenue"],
        templates: ["D1"],
    })
    const periods = draft.periods
    periods.folder = ""
    expect(wrapper.find(".scaffold-path").exists()).toBe(false)
    expect(
        wrapper
            .get('[data-calendar-panel="calendar"]')
            .findAll("button")
            .some((item) => item.text() === "应用日期表"),
    ).toBe(true)
    expect(
        wrapper
            .get('[data-calendar-panel="periods"]')
            .findAll("button")
            .some((item) => item.text() === "应用时间智能"),
    ).toBe(true)
    await button("应用日期表").trigger("click")
    await flushPromises()
    expect(transport.applications[0]?.request).toMatchObject({ calendar, periods: null, measures: null })
    expect(periods.folder).toBe("")

    calendar.tableName = "未提交日期表"
    calendar.startYear = 2099
    calendar.numberedColumns = false
    periods.folder = "保留时间智能目录"
    draft.measures = [{ name: "未提交度量", value: "1", enabled: true, description: "", displayFolder: "", dataCategory: "Uncategorized" }]
    const tabs = step.getComponent(UiTabs)
    tabs.vm.$emit("update:modelValue", tabs.props("items")[1]![0])
    await flushPromises()
    const available = step
        .getComponent(PeriodsFields)
        .findAllComponents(ElOption)
        .map((item) => item.props("value"))
    expect(available).toContain("原日期键")
    expect(available).not.toContain("未提交日期表")
    expect(available).not.toContain("未提交度量")
    expect(periods.dateColumn).toBe("原日期键")
    await button("应用时间智能").trigger("click")
    await flushPromises()
    expect(transport.applications[1]?.request).toMatchObject({ calendar: null, periods, measures: null, sample: null, udfs: null })
    expect(calendar.startYear).toBe(2099)
    expect(calendar.tableName).toBe("未提交日期表")
    expect(step.props("calendarEnabled")).toBe(true)
    expect(wrapper.find('[data-calendar-panel="periods"] [data-optional]').exists()).toBe(false)
    expect(button("应用时间智能").attributes("disabled")).toBeUndefined()
    expect(periods.folder).toBe("保留时间智能目录")
    await wrapper.setProps({ externalLocked: true })
    expect(button("应用时间智能").attributes("disabled")).toBeDefined()
    expect(wrapper.get('[data-scaffold-field="periodFolder"] input').attributes("disabled")).toBeDefined()
})

it("日期实际应用成功各提示一次提交结果和刷新警告, 无变更及切语言不重放旧警告", async () => {
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const { wrapper, transport, button } = await harness()
    const warning = "日期表数据尚未刷新；请在 Power BI Desktop 显式刷新对应日期表。"
    transport.context = {
        ...transport.context,
        project: {
            ...transport.context.project,
            baselineId: "calendar-warning-baseline",
            projectPath: "model://calendar-warning-target",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            warnings: [warning, warning],
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "当前连接模型已更新；请在 Power BI Desktop 保存报表。" },
        },
    }
    // 当前目标身份不能在应用预检查时切换; 先接受宿主权威快照再发起同目标操作.
    await wrapper.setProps({ project: transport.context.project })
    await flushPromises()
    sent.mockClear()
    await button("应用日期表").trigger("click")
    await flushPromises()
    expect(sent.mock.calls.filter((call) => call[1] === "success").map(([message]) => message)).toEqual([
        "当前连接模型已更新；请在 Power BI Desktop 保存报表。",
    ])
    expect(sent.mock.calls.filter((call) => call[1] === "warning").map(([message]) => message)).toEqual([warning])
    const count = sent.mock.calls.length
    setLocale("en-us")
    await flushPromises()
    expect(sent.mock.calls).toHaveLength(count)
    transport.noChanges = true
    await button("Apply calendar table").trigger("click")
    await flushPromises()
    expect(sent.mock.calls.filter((call) => call[1] === "warning").map(([message]) => message)).toEqual([warning])
    expect(sent.mock.calls.at(-1)).toEqual(["Configuration unchanged", "success"])
})
