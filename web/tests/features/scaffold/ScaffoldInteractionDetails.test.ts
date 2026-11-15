/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\ScaffoldInteractionDetails.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证辅助表独立折叠与日期业务禁用、临时锁和迟到事件的交互边界.
 */
import { expect, it } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import { markRaw, reactive } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import MeasuresEditor from "@/features/scaffold/MeasuresEditor.vue"
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
        expect((wrapper.get(".scaffold-grid").element.parentElement as HTMLElement).style.display).toBe("none")
        const element = picker.element
        expect(picker.findAll('input[type="checkbox"]').every((input) => (input.element as HTMLInputElement).disabled)).toBe(true)
        expect(picker.findAll(".operation-lock")).toHaveLength(0)
        picker.findAllComponents(ElCheckboxGroup)[0]!.vm.$emit("update:modelValue", ["Dates"])
        picker.findAllComponents(ElCheckbox)[0]!.vm.$emit("update:modelValue", false)
        expect(JSON.stringify(draft.calendar)).toBe(before)
        expect(picker.emitted("change")).toBeUndefined()
        await wrapper.setProps({ calendarEnabled: true })
        expect((wrapper.get(".scaffold-grid").element.parentElement as HTMLElement).style.display).toBe("")
        expect(wrapper.getComponent(CalendarFieldPicker).element).toBe(element)
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

it("辅助表整体折叠保留草稿, 分组校验错误自动展开", async () => {
    const wrapper = mount(TemplateQueries, { props: { queries: templateQueries, modelValue: ["Hour24"], groups: { Hour24: "model" }, disabled: false } })
    try {
        const details = wrapper.get("details")
        expect((details.element as HTMLDetailsElement).open).toBe(false)
        await wrapper.setProps({ groups: { Hour24: "   " } })
        expect((details.element as HTMLDetailsElement).open).toBe(true)
        expect(wrapper.get('[data-template-group="Hour24"] input').attributes("aria-invalid")).toBe("true")
        await details.get("summary").trigger("click")
        expect((details.element as HTMLDetailsElement).open).toBe(true)
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("新建模板度量值关闭后保留同一编辑器与草稿, 模型场景始终显示", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const draft = reactive(createConfigurationDraft("zh-cn"))
    draft.measuresEnabled = true
    draft.measures = [{ name: "原名称", value: "未提交长值", dataCategory: "Uncategorized", enabled: true, description: "保留", displayFolder: "模板" }]
    const wrapper = mount(ConfigurationFields, {
        props: { draft, bridge, step: "measures", visited: new Set(["measures"]), idPrefix: "test", disabled: false, locked: false, definitions: [] },
    })
    try {
        const editor = wrapper.getComponent(MeasuresEditor)
        const input = editor.get('input[aria-label="值 1"]')
        await input.setValue("修改后的草稿")
        draft.measuresEnabled = false
        await flushPromises()
        const content = wrapper.get('[data-scaffold-field="measures"]').element.parentElement as HTMLElement
        expect(content.style.display).toBe("none")
        expect((input.element as HTMLInputElement).disabled).toBe(true)
        draft.measuresEnabled = true
        await flushPromises()
        expect(content.style.display).toBe("")
        expect(wrapper.getComponent(MeasuresEditor).get('input[aria-label="值 1"]').element).toBe(input.element)
        expect(draft.measures[0]?.value).toBe("修改后的草稿")
        await wrapper.setProps({ modelConfiguration: true })
        expect(wrapper.get("#section-test-measures").find("[data-optional]").exists()).toBe(false)
        expect(content.style.display).toBe("")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
