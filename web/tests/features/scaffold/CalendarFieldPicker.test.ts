/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\CalendarFieldPicker.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证全部字段可见, 快捷方案保留目标锁但不污染通用方案, 混合历史别名完整显示.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import CalendarFieldPicker from "@/features/scaffold/CalendarFieldPicker.vue"
import { ElCheckboxGroup } from "element-plus"
it("63字段平铺, 保留旧别名和锁定原因, 快捷清空不污染方案", async () => {
    const wrapper = mount(CalendarFieldPicker, {
        props: {
            calendar: {
                kind: "simple",
                startYear: 2025,
                endYear: 2025,
                tableName: "Calendar",
                overrides: [],
                fields: ["Dates", "Day", "Status"],
                numberedColumns: true,
            },
            preferenceFields: ["Dates", "Day"],
            disabled: false,
            capability: {
                tableName: "Calendar",
                mode: "existing",
                fields: ["Dates", "Day"],
                columns: [
                    { field: "Dates", name: "日期" },
                    { field: "Dates", name: "C01_Dates" },
                ],
                lockedFields: [{ field: "Day", reason: "保留用户引用" }],
            },
        },
    })
    expect(wrapper.findAll(".calendar-fields__choice")).toHaveLength(63)
    expect(wrapper.text()).toContain("日期 / C01_Dates")
    expect(wrapper.text()).toContain("保留用户引用")
    await wrapper.findAll("button")[3]!.trigger("click")
    expect(wrapper.emitted("change")?.[0]).toEqual([["Dates", "Day"], ["Dates"], true])
    wrapper.unmount()
})
it("单字段增删只把主动差异合并到通用方案, 既有锁列不污染", async () => {
    const wrapper = mount(CalendarFieldPicker, {
        props: {
            calendar: { kind: "simple", startYear: 2025, endYear: 2025, tableName: "Calendar", overrides: [], fields: ["Dates", "Day"] },
            preferenceFields: ["Dates", "Month"],
            disabled: false,
            capability: { tableName: "Calendar", mode: "existing", fields: ["Dates", "Day"], columns: [], lockedFields: [{ field: "Day", reason: "已存在" }] },
        },
    })
    wrapper.findAllComponents(ElCheckboxGroup).at(-1)!.vm.$emit("update:modelValue", ["Dates", "Day", "Status"])
    expect(wrapper.emitted("change")?.[0]).toEqual([["Dates", "Day", "Status"], ["Dates", "Month", "Status"], true])
    wrapper.unmount()
})

it("已有标准字段随编号选择覆盖显示, 人工别名保持且提示完整覆盖语义", async () => {
    const calendar = { kind: "simple", startYear: 2025, endYear: 2025, tableName: "Calendar", overrides: [], fields: ["Dates", "Year"], numberedColumns: false }
    const wrapper = mount(CalendarFieldPicker, {
        props: {
            calendar,
            preferenceFields: ["Dates", "Year"],
            disabled: false,
            capability: {
                tableName: "Calendar",
                mode: "existing",
                fields: ["Dates", "Year"],
                columns: [
                    { field: "Dates", name: "C01_Dates" },
                    { field: "Year", name: "自定义年" },
                ],
                lockedFields: [],
            },
        },
    })
    try {
        expect(wrapper.text()).toContain("已有标准日期列按当前编号选择覆盖")
        expect(wrapper.text()).not.toContain("新增字段加编号")
        expect(
            wrapper
                .findAll(".calendar-fields__choice")
                .find((field) => field.text().includes("日期"))
                ?.text(),
        ).not.toContain("C01_Dates")
        expect(wrapper.text()).toContain("自定义年")
        await wrapper.setProps({ calendar: { ...calendar, numberedColumns: true } })
        expect(wrapper.text()).toContain("C01_Dates")
        expect(wrapper.text()).toContain("自定义年")
    } finally {
        wrapper.unmount()
    }
})

it("日期搜索匹配双语含义和技术名, 筛选期间修改保留隐藏选择与必选列", async () => {
    const calendar = { kind: "simple", startYear: 2025, endYear: 2025, tableName: "Calendar", overrides: [], fields: ["Dates", "Day", "Year"] }
    const wrapper = mount(CalendarFieldPicker, { props: { calendar, preferenceFields: calendar.fields, disabled: false } })
    try {
        const search = wrapper.get('input[aria-label="搜索日期字段"]')
        await search.setValue("C02_Day")
        expect(wrapper.findAll(".calendar-fields__choice").filter((item) => (item.element as HTMLElement).style.display !== "none")).toHaveLength(1)
        await wrapper.get('.calendar-fields__choice input[value="Day"]').setValue(false)
        expect(wrapper.emitted("change")?.at(-1)?.[0]).toEqual(["Dates", "Year"])
        await search.setValue("星期")
        expect(wrapper.findAll(".calendar-fields__choice").filter((item) => (item.element as HTMLElement).style.display !== "none").length).toBeGreaterThan(1)
        await search.setValue("weekday")
        expect(
            wrapper
                .findAll(".calendar-fields__choice")
                .some((item) => (item.element as HTMLElement).style.display !== "none" && item.text().includes("WeekDay")),
        ).toBe(true)
        await search.setValue("不存在的字段")
        expect(wrapper.text()).toContain("没有匹配的日期字段")
        expect(wrapper.emitted("change")).toHaveLength(1)
        await search.setValue("")
        expect(wrapper.findAll(".calendar-fields__choice").filter((item) => (item.element as HTMLElement).style.display !== "none")).toHaveLength(63)
        expect((wrapper.get('input[value="Dates"]').element as HTMLInputElement).disabled).toBe(true)
    } finally {
        wrapper.unmount()
    }
})
