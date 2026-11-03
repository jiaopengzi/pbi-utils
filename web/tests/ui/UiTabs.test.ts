/**
 * FilePath    : pbi-utils\web\tests\ui\UiTabs.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证真实 Element Plus 页签的键盘切换、整组临时锁和动态选项状态.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import UiTabs from "@/ui/UiTabs.vue"

const items = [
    ["new", "新建"],
    ["sample", "示例"],
]

it("普通鼠标及方向键可以切换页签, 动态标签和选项顺序不重置当前选择", async () => {
    const wrapper = mount(UiTabs, { props: { modelValue: "new", items }, attachTo: document.body })
    await flushPromises()
    await wrapper.get("#tab-sample").trigger("click")
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["sample"])
    await wrapper.setProps({ modelValue: "sample" })
    const selected = wrapper.get("#tab-sample").element
    await wrapper.setProps({
        items: [
            ["sample", "Sample data"],
            ["new", "New"],
        ],
    })
    await flushPromises()
    expect(wrapper.get("#tab-sample").element).toBe(selected)
    expect(wrapper.get("#tab-sample").attributes("aria-selected")).toBe("true")
    expect(wrapper.get("#tab-sample").text()).toBe("Sample data")
    await wrapper.get("#tab-sample").trigger("keydown", { key: "ArrowRight", code: "ArrowRight" })
    await flushPromises()
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["new"])
    wrapper.unmount()
})

it("聚焦页签后整组进入在途锁, 鼠标和方向及激活键不会切换或抛错, Tab 仍可离开", async () => {
    const errors = vi.fn()
    const wrapper = mount(UiTabs, {
        props: { modelValue: "sample", items },
        attachTo: document.body,
        global: { config: { errorHandler: errors } },
    })
    await flushPromises()
    ;(wrapper.get("#tab-sample").element as HTMLElement).focus()
    await wrapper.setProps({ disabled: true })
    await wrapper.get("#tab-new").trigger("click")
    for (const key of ["ArrowRight", "ArrowLeft", "ArrowUp", "ArrowDown", "Enter", " "])
        wrapper
            .get("#tab-sample")
            .element.dispatchEvent(new KeyboardEvent("keydown", { key, code: key === " " ? "Space" : key, bubbles: true, cancelable: true }))
    await flushPromises()
    expect(errors).not.toHaveBeenCalled()
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    expect(wrapper.get("#tab-sample").attributes("aria-selected")).toBe("true")
    for (const tab of wrapper.findAll('[role="tab"]')) expect(tab.attributes("tabindex")).toBe("-1")
    const leave = new KeyboardEvent("keydown", { key: "Tab", code: "Tab", bubbles: true, cancelable: true })
    wrapper.get("#tab-sample").element.dispatchEvent(leave)
    expect(leave.defaultPrevented).toBe(false)
    await wrapper.setProps({ disabled: false })
    await wrapper.get("#tab-sample").trigger("keydown", { key: "ArrowLeft", code: "ArrowLeft" })
    await flushPromises()
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["new"])
    wrapper.unmount()
})
