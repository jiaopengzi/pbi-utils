/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\ScaffoldInteractionDetails.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证辅助表独立折叠与日期业务禁用、临时锁和迟到事件的交互边界.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ElCheckbox, ElCheckboxGroup } from "element-plus"
import TemplateQueries from "@/features/scaffold/TemplateQueries.vue"
import CalendarFieldPicker from "@/features/scaffold/CalendarFieldPicker.vue"
import CalendarTableFields from "@/features/scaffold/CalendarTableFields.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import templateQueries from "@/features/scaffold/templateQueries.json"
import { createConfigurationDraft } from "@/features/configuration/draft"

it("辅助表默认全部折叠, 独立展开与关闭且锁定时保持原状态", async () => {
    const wrapper = mount(TemplateQueries, { props: { queries: templateQueries, modelValue: [], groups: {}, disabled: false } })
    try {
        const toggles = wrapper.findAll("button[aria-expanded]")
        expect(toggles).toHaveLength(4)
        expect(toggles.every((button) => button.attributes("aria-expanded") === "false")).toBe(true)
        expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(0)
        await toggles[0]!.trigger("click")
        await toggles[1]!.trigger("click")
        expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(2)
        expect(toggles.slice(0, 2).every((button) => button.attributes("aria-expanded") === "true")).toBe(true)
        await toggles[0]!.trigger("click")
        expect(toggles[0]!.attributes("aria-expanded")).toBe("false")
        expect(toggles[1]!.attributes("aria-expanded")).toBe("true")
        await wrapper.setProps({ disabled: true })
        await toggles[1]!.trigger("click")
        expect(toggles[1]!.attributes("aria-expanded")).toBe("true")
        await wrapper.setProps({ disabled: false })
        expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it("日期关闭保持原生禁用和业务禁用外观, 再开启保留字段与编号", async () => {
    const draft = createConfigurationDraft("zh-cn")
    const before = JSON.stringify(draft.calendar)
    const wrapper = mount(CalendarTableFields, {
        props: { calendar: draft.calendar, periods: draft.periods, calendarEnabled: false, disabled: false },
    })
    try {
        const picker = wrapper.getComponent(CalendarFieldPicker)
        expect(picker.findAll('input[type="checkbox"]').every((input) => (input.element as HTMLInputElement).disabled)).toBe(true)
        expect(picker.findAll(".operation-lock")).toHaveLength(0)
        picker.findAllComponents(ElCheckboxGroup)[0]!.vm.$emit("update:modelValue", ["Dates"])
        picker.findAllComponents(ElCheckbox)[0]!.vm.$emit("update:modelValue", false)
        expect(JSON.stringify(draft.calendar)).toBe(before)
        expect(picker.emitted("change")).toBeUndefined()
        await wrapper.setProps({ calendarEnabled: true })
        expect(JSON.stringify(draft.calendar)).toBe(before)
        expect(picker.findAll('input[type="checkbox"]').some((input) => !(input.element as HTMLInputElement).disabled)).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it("临时锁只稳定原本可编辑字段, 必需字段与不存在的恢复方案保持业务禁用", () => {
    const draft = createConfigurationDraft("zh-cn")
    const wrapper = mount(CalendarFieldPicker, {
        props: { calendar: draft.calendar, preferenceFields: ["Dates", "Year"], disabled: true, operationLocked: true },
    })
    try {
        const choices = wrapper.findAll(".calendar-fields__choice")
        expect(choices[0]!.classes()).not.toContain("operation-lock")
        expect(choices[1]!.classes()).toContain("operation-lock")
        expect(wrapper.findAll("button").at(-1)!.classes()).not.toContain("operation-lock")
        expect(wrapper.findAll(".el-checkbox-group.operation-lock")).toHaveLength(0)
    } finally {
        wrapper.unmount()
    }
})
