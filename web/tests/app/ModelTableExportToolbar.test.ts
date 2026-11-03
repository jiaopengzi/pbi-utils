/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableExportToolbar.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证真实导出工具栏挂载锁后解锁的原生与ARIA状态、输入身份及抽样草稿.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ElCheckbox, ElInputNumber, ElSelect, ElSwitch } from "element-plus"
import ModelTableExportToolbar from "@/app/ModelTableExportToolbar.vue"

/** 只挂载真实工具栏与Element Plus, 不请求宿主或模拟业务导出成功. */
function render(locked: boolean) {
    return mount(ModelTableExportToolbar, {
        props: {
            selectedCount: 1,
            locked,
            busy: false,
            exportAvailable: true,
            canDelete: false,
            deleting: false,
            validLimit: true,
            opening: false,
            format: "xlsx",
            includeLog: true,
            scope: "sample",
            rowLimit: 100,
        },
        attachTo: document.body,
    })
}

it("初始locked为true的行数输入解锁后原生与ARIA一致, 重锁再解锁不替换节点或丢值", async () => {
    const wrapper = render(true)
    try {
        const input = wrapper.findComponent(ElInputNumber).get("input"),
            element = input.element
        expect(element.disabled).toBe(true)
        expect(input.attributes("aria-disabled")).toBe("true")
        await wrapper.setProps({ locked: false })
        expect(element.disabled).toBe(false)
        expect(input.attributes("aria-disabled")).toBe("false")
        expect(wrapper.findComponent(ElInputNumber).get("input").element).toBe(element)
        expect(element.value).toBe("100")
        await wrapper.setProps({ locked: true })
        expect(element.disabled).toBe(true)
        expect(input.attributes("aria-disabled")).toBe("true")
        await wrapper.setProps({ locked: false })
        expect(element.disabled).toBe(false)
        expect(input.attributes("aria-disabled")).toBe("false")
        expect(wrapper.findComponent(ElInputNumber).get("input").element).toBe(element)
        expect(element.value).toBe("100")
    } finally {
        wrapper.unmount()
    }
})

it("摘要选项使用同一导出行中的switch, 锁定不切换且CSV往返保留开关值", async () => {
    const wrapper = render(false)
    try {
        const summary = wrapper.findComponent(ElSwitch)
        expect(summary.exists()).toBe(true)
        expect(wrapper.findComponent(ElCheckbox).exists()).toBe(false)
        expect(summary.get('[role="switch"]').attributes("aria-label")).toBe("添加导出摘要页")
        expect(summary.element.closest(".export-options")).not.toBeNull()
        expect(summary.props("modelValue")).toBe(true)
        await summary.trigger("click")
        expect(wrapper.emitted("update:includeLog")?.at(-1)).toEqual([false])
        await wrapper.setProps({ includeLog: false, locked: true })
        const events = wrapper.emitted("update:includeLog")?.length
        await summary.trigger("click")
        expect(wrapper.emitted("update:includeLog")?.length).toBe(events)
        await wrapper.setProps({ locked: false, format: "csv" })
        expect(wrapper.findComponent(ElSwitch).exists()).toBe(false)
        expect(wrapper.text()).toContain("每张表一个 CSV")
        wrapper.findComponent(ElSelect).vm.$emit("update:modelValue", "xlsx")
        await wrapper.setProps({ format: "xlsx" })
        expect(wrapper.findComponent(ElSwitch).props("modelValue")).toBe(false)
    } finally {
        wrapper.unmount()
    }
})

it("真实行数草稿在临时锁往返后仍保留, 解锁输入继续向工具栏发送所选数值", async () => {
    const wrapper = render(false)
    try {
        const input = wrapper.findComponent(ElInputNumber).get("input"),
            element = input.element
        element.value = "25"
        await input.trigger("input")
        expect(wrapper.emitted("update:rowLimit")?.at(-1)).toEqual([25])
        await wrapper.setProps({ locked: true })
        expect(element.value).toBe("25")
        await wrapper.setProps({ locked: false })
        expect(wrapper.findComponent(ElInputNumber).get("input").element).toBe(element)
        expect(element.value).toBe("25")
        expect(input.attributes("aria-disabled")).toBe("false")
        element.focus()
        expect(document.activeElement).toBe(element)
        element.value = "42"
        await input.trigger("input")
        expect(wrapper.emitted("update:rowLimit")?.at(-1)).toEqual([42])
    } finally {
        wrapper.unmount()
    }
})
