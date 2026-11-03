/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\CalendarStep.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证51项平铺分组, 自动中间依赖和真实日期字段选择.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { nextTick } from "vue"
import { ElOption } from "element-plus"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import CalendarTableFields from "@/features/scaffold/CalendarTableFields.vue"
import PeriodsFields from "@/features/scaffold/PeriodsFields.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import type { ScaffoldCalendarOptions, ScaffoldMethods, ScaffoldPeriodOptions } from "@/features/scaffold/contracts"
import CalendarFieldPicker from "@/features/scaffold/CalendarFieldPicker.vue"
import UiTabs from "@/ui/UiTabs.vue"
import { createConfigurationDraft } from "@/features/configuration/draft"
import { shallowRef } from "vue"
import { scaffoldValidationKey } from "@/features/scaffold/validationContext"
import type { ScaffoldValidationIssue } from "@/features/scaffold/validation"
it.each(["calendar", "periods"])("%s 独立表单默认保留新建开关, 模型显式隐藏仍保留字段", async (section) => {
    const draft = createConfigurationDraft("zh-cn")
    const props = { calendar: draft.calendar, periods: draft.periods, calendarEnabled: true, disabled: false }
    const wrapper =
        section === "calendar"
            ? mount(CalendarTableFields, { props })
            : mount(PeriodsFields, { props: { ...props, periodsEnabled: true, definitions: [], measures: [] } })
    try {
        expect(wrapper.findAll('[data-optional] input[role="switch"]')).toHaveLength(1)
        await wrapper.setProps({ showEnableSwitch: false })
        expect(wrapper.find("[data-optional]").exists()).toBe(false)
        expect(wrapper.find(".scaffold-grid").exists()).toBe(true)
    } finally {
        wrapper.unmount()
    }
})
it("日期和期间业务页签保留草稿, 隐藏字段校验自动路由并保留覆盖绝对分页", async () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.calendar.overrides = Array.from({ length: 101 }, () => ({ date: "2024-01-01", isWorkday: true, name: "保留" }))
    const issue = shallowRef<ScaffoldValidationIssue>()
    const wrapper = mount(CalendarStep, {
        props: {
            calendar: draft.calendar,
            periods: draft.periods,
            calendarEnabled: true,
            periodsEnabled: true,
            disabled: false,
            definitions: [],
            measures: [],
        },
        global: { provide: { [scaffoldValidationKey as symbol]: issue }, stubs: { ElDatePicker: { template: "<input />" } } },
    })
    const tabs = wrapper.findComponent(UiTabs)
    expect(tabs.exists()).toBe(true)
    await flushPromises()
    expect(wrapper.findAll('[data-optional] input[role="switch"]')).toHaveLength(2)
    const [calendarTab, periodsTab] = tabs.props("items").map(([id]) => id)
    expect(tabs.props("modelValue")).toBe(calendarTab)
    tabs.vm.$emit("update:modelValue", periodsTab)
    await nextTick()
    expect(tabs.props("modelValue")).toBe(periodsTab)
    expect(wrapper.get('[data-calendar-panel="calendar"]').attributes("style")).toContain("display: none")
    expect(wrapper.get('[data-calendar-panel="periods"]').attributes("style") ?? "").not.toContain("display: none")
    draft.periods.folder = "保留期间目录"
    issue.value = { step: "calendar", field: "overrideName", row: 75, key: "scaffold.validationWorkdayName" }
    await nextTick()
    expect(tabs.props("modelValue")).toBe(calendarTab)
    expect(wrapper.get('[data-scaffold-field="overrideName"][data-scaffold-row="75"] input').attributes("aria-invalid")).toBe("true")
    issue.value = { step: "calendar", field: "periodFolder", key: "scaffold.validationName" }
    await nextTick()
    expect(tabs.props("modelValue")).toBe(periodsTab)
    expect(draft.periods.folder).toBe("保留期间目录")
    expect(draft.calendar.overrides[74]?.name).toBe("保留")
    await wrapper.setProps({ disabled: true })
    tabs.vm.$emit("update:modelValue", calendarTab)
    await nextTick()
    expect(tabs.props("modelValue")).toBe(periodsTab)
    wrapper.unmount()
})
it.each(["new", "existing"] as const)("%s 内置表取消编号后期间改绑Dates并移除裁掉的旧列选项", async (mode) => {
    const draft = createConfigurationDraft("zh-cn")
    draft.periods.dateTable = draft.calendar.tableName
    draft.periods.dateColumn = "C01_Dates"
    const wrapper = mount(CalendarStep, {
        props: {
            calendar: draft.calendar,
            periods: draft.periods,
            calendarEnabled: true,
            periodsEnabled: true,
            disabled: false,
            definitions: [],
            measures: [],
            details: {
                tables: [
                    {
                        name: draft.calendar.tableName,
                        columns: [
                            { name: "C01_Dates", dataType: "DateTime" },
                            { name: "C32_StartOfMonth", dataType: "DateTime" },
                        ],
                        measures: [],
                        isDate: true,
                    },
                ],
                defaultDateTable: draft.calendar.tableName,
                defaultDateColumn: "C01_Dates",
                defaultMeasureTable: null,
                calendarCapabilities: [
                    {
                        tableName: draft.calendar.tableName,
                        mode,
                        fields: ["Dates", "StartOfMonth"],
                        columns: [
                            { field: "Dates", name: "C01_Dates" },
                            { field: "StartOfMonth", name: "C32_StartOfMonth" },
                        ],
                        lockedFields: [],
                        dateColumn: "C01_Dates",
                    },
                ],
            },
        },
    })
    draft.calendar.numberedColumns = false
    await nextTick()
    expect(draft.periods.dateColumn).toBe("Dates")
    const options = wrapper.findAllComponents(ElOption).map((option) => option.props("value"))
    expect(options).toContain("Dates")
    expect(options).not.toContain("C01_Dates")
    expect(options).not.toContain("C32_StartOfMonth")
    wrapper.unmount()
})
it("大导入覆盖只挂载50行, 校验第75行自动翻页且保留绝对行号", async () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.calendar.overrides = Array.from({ length: 101 }, (_, index) => ({
        date: new Date(Date.UTC(2025, 0, index + 1)).toISOString().slice(0, 10),
        isWorkday: true,
        name: "",
    }))
    const issue = shallowRef<ScaffoldValidationIssue>()
    const wrapper = mount(CalendarStep, {
        props: {
            calendar: draft.calendar,
            periods: draft.periods,
            calendarEnabled: true,
            periodsEnabled: false,
            disabled: false,
            definitions: [],
            measures: [],
        },
        global: {
            provide: { [scaffoldValidationKey as symbol]: issue },
            stubs: { ElDatePicker: { template: "<input />" } },
        },
    })
    expect(wrapper.findAll("tbody tr")).toHaveLength(50)
    issue.value = { step: "calendar", field: "overrideDate", row: 75, key: "scaffold.validationDate" }
    await nextTick()
    expect(wrapper.find('[data-scaffold-row="75"] input').attributes("aria-invalid")).toBe("true")
    expect(wrapper.find('[data-scaffold-row="1"]').exists()).toBe(false)
    wrapper.unmount()
})
it("迟到偏好不覆盖已编辑年份, 主动编号变更更新实际时间智能日期键", async () => {
    let resolveRead!: (value: unknown) => void
    const request = vi.fn((method, params) => ({
        promise:
            method === "calendar.preferences"
                ? new Promise((resolve) => {
                      resolveRead = resolve
                  })
                : Promise.resolve(params),
    }))
    const draft = createConfigurationDraft("zh-cn")
    draft.calendarEnabled = true
    draft.periods.dateTable = draft.calendar.tableName
    draft.periods.dateColumn = "C01_Dates"
    const wrapper = mount(CalendarStep, {
        props: {
            calendar: draft.calendar,
            periods: draft.periods,
            calendarEnabled: true,
            periodsEnabled: true,
            disabled: false,
            definitions: [],
            measures: [],
            bridge: { request } as never,
        },
    })
    await flushPromises()
    draft.calendar.startYear--
    resolveRead({ fields: ["Dates", "LunarYear"], numberedColumns: false })
    await flushPromises()
    expect(draft.calendar.fields).not.toContain("LunarYear")
    wrapper.findComponent(CalendarFieldPicker).vm.$emit("change", ["Dates", "Status"], ["Dates", "Status"], false)
    await flushPromises()
    expect(draft.periods.dateColumn).toBe("Dates")
    expect(request.mock.calls.filter((call) => call[0] === "calendar.savePreferences")).toHaveLength(1)
    wrapper.unmount()
})
it("合法上次方案跨入口用于新草稿, 已有目标回读不写入偏好", async () => {
    const request = vi.fn(() => ({ promise: Promise.resolve({ fields: ["Dates", "Status"], numberedColumns: false }) }))
    const bridge = { request } as never
    const first = createConfigurationDraft("zh-cn"),
        second = createConfigurationDraft("zh-cn")
    const one = mount(CalendarStep, {
        props: {
            calendar: first.calendar,
            periods: first.periods,
            calendarEnabled: true,
            periodsEnabled: false,
            disabled: false,
            definitions: [],
            measures: [],
            bridge,
        },
    })
    await flushPromises()
    expect(first.calendar.fields).toEqual(["Dates", "Status"])
    one.unmount()
    const two = mount(CalendarStep, {
        props: {
            calendar: second.calendar,
            periods: second.periods,
            calendarEnabled: true,
            periodsEnabled: false,
            disabled: false,
            definitions: [],
            measures: [],
            bridge,
            details: {
                tables: [],
                defaultDateTable: null,
                defaultDateColumn: null,
                defaultMeasureTable: null,
                calendarCapabilities: [
                    {
                        tableName: second.calendar.tableName,
                        mode: "existing",
                        fields: ["Dates", "Day"],
                        columns: [{ field: "Dates", name: "日期" }],
                        lockedFields: [{ field: "Day", reason: "已有列" }],
                    },
                ],
            },
        },
    })
    await flushPromises()
    expect(second.calendar.fields).not.toEqual(["Dates", "Status"])
    expect(request).toHaveBeenCalledTimes(1)
    two.unmount()
})
it("六组平铺51项, 组选不删除其他组, 依赖可见且跳过保留工作日覆盖", async () => {
    const definitions = new ScaffoldMock().handle("scaffold.templates", {}) as ScaffoldMethods["scaffold.templates"]["result"]
    const calendar: ScaffoldCalendarOptions = {
        kind: "simple",
        startYear: 2024,
        endYear: 2025,
        tableName: "MyCalendar",
        overrides: [{ date: "2024-02-10", isWorkday: false, name: "override" }],
    }
    const periods: ScaffoldPeriodOptions = {
        dateTable: "MyCalendar",
        dateColumn: "",
        measureTable: "Measures",
        baseMeasures: ["Revenue"],
        templates: ["D3"],
        folder: "Periods",
        numberDigits: 2,
        percentDigits: 2,
    }
    const wrapper = mount(CalendarStep, {
        props: { calendar, periods, calendarEnabled: true, periodsEnabled: true, disabled: false, definitions: definitions.periods, measures: [] },
    })
    expect(wrapper.findAll(".scaffold-period-group .el-checkbox-button")).toHaveLength(51)
    expect(wrapper.findAll(".scaffold-period-group").map((group) => group.findAll(".el-checkbox-button").length)).toEqual([7, 10, 10, 10, 10, 4])
    expect(wrapper.text()).toContain("D1")
    expect(wrapper.findAllComponents(ElOption).map((option) => option.props("value"))).toContain("C01_Dates")
    await wrapper.findAll(".scaffold-period-group")[1]!.findAll("button")[0]!.trigger("click")
    expect(periods.templates).toContain("D3")
    expect(periods.templates.filter((id) => id.startsWith("W"))).toHaveLength(10)
    calendar.kind = "full"
    await nextTick()
    await wrapper.setProps({ calendar: { ...calendar } })
    expect(wrapper.findAllComponents(ElOption).map((option) => option.props("value"))).toContain("C01_Dates")
    await wrapper.setProps({ calendarEnabled: false, periodsEnabled: false })
    expect(calendar.overrides).toHaveLength(1)
    expect(wrapper.findAll(".el-checkbox-button").every((button) => button.classes().includes("is-disabled"))).toBe(true)
    wrapper.unmount()
})
