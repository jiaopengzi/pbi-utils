/**
 * FilePath    : pbi-utils\web\tests\ui\UiDialogScroll.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 真实Element Plus正文复用时的按需滚动重置、关闭失效和实例隔离.
 */
import { afterEach, expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { defineComponent, h } from "vue"
import { ElDialog } from "element-plus"
import UiDialog from "@/ui/UiDialog.vue"
const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))
/** 获取当前实例的真实滚动正文, 不以window滚动或模拟事件替代DOM事实. */
function body(title: string): HTMLElement {
    return document.querySelector<HTMLElement>(`[role="dialog"][aria-label="${title}"] .el-dialog__body`)!
}
async function fixture(key?: string, title = "Details") {
    const wrapper = mount(UiDialog, {
        attachTo: document.body,
        props: { modelValue: true, title, scrollResetKey: key },
        slots: { default: "<p>Long document</p>" },
    })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    return wrapper
}
it("关闭后复用相同正文打开另一身份时同时回到顶部和最左", async () => {
    const wrapper = await fixture("package-a"),
        original = body("Details")
    original.scrollTop = 650
    original.scrollLeft = 48
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true, scrollResetKey: "package-b" })
    await flushPromises()
    expect(body("Details")).toBe(original)
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
})
it("保持打开的身份变更也重置, 普通重渲染及迟到opened不打断当前阅读", async () => {
    const wrapper = await fixture("package-a"),
        content = body("Details")
    content.scrollTop = 500
    content.scrollLeft = 36
    await wrapper.setProps({ busy: true, title: "Translated" })
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([500, 36])
    await wrapper.setProps({ scrollResetKey: "package-b" })
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([0, 0])
    content.scrollTop = 80
    content.scrollLeft = 15
    wrapper.findComponent(ElDialog).vm.$emit("opened")
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([80, 15])
    expect(wrapper.emitted("opened")).toBeDefined()
})
it("关闭可撤销尚未渲染的重置, 再打开同一身份仍从起点阅读", async () => {
    const wrapper = await fixture("package-a"),
        content = body("Details")
    content.scrollTop = 400
    content.scrollLeft = 20
    const changed = wrapper.setProps({ scrollResetKey: "package-b" }),
        closed = wrapper.setProps({ modelValue: false })
    await Promise.all([changed, closed])
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([400, 20])
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([0, 0])
})
it("仅重置启用该能力的当前实例, 其他弹窗继续保留各自位置", async () => {
    const first = await fixture(undefined, "Form"),
        second = await fixture("package-a", "Package")
    const form = body("Form"),
        documentBody = body("Package")
    form.scrollTop = 330
    form.scrollLeft = 12
    documentBody.scrollTop = 440
    documentBody.scrollLeft = 23
    await second.setProps({ scrollResetKey: "package-b" })
    await flushPromises()
    expect([form.scrollTop, form.scrollLeft]).toEqual([330, 12])
    expect([documentBody.scrollTop, documentBody.scrollLeft]).toEqual([0, 0])
    await first.setProps({ modelValue: false })
    await first.setProps({ modelValue: true })
    await flushPromises()
    expect([form.scrollTop, form.scrollLeft]).toEqual([330, 12])
})

it("同一应用同时打开的两个文档弹窗只重置对应实例", async () => {
    const wrapper = mount(
        defineComponent({
            props: { secondKey: { type: String, required: true } },
            setup(props) {
                return () =>
                    h("div", [
                        h(UiDialog, { modelValue: true, title: "First", scrollResetKey: "first" }),
                        h(UiDialog, { modelValue: true, title: "Second", scrollResetKey: props.secondKey }),
                    ])
            },
        }),
        { attachTo: document.body, props: { secondKey: "second-a" } },
    )
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const first = body("First"),
        second = body("Second")
    first.scrollTop = 240
    first.scrollLeft = 11
    second.scrollTop = 360
    second.scrollLeft = 22
    await wrapper.setProps({ secondKey: "second-b" })
    await flushPromises()
    expect([first.scrollTop, first.scrollLeft]).toEqual([240, 11])
    expect([second.scrollTop, second.scrollLeft]).toEqual([0, 0])
})
it("卸载使未执行的重置失效, 不操作已移交的正文引用", async () => {
    const wrapper = await fixture("package-a"),
        content = body("Details")
    content.scrollTop = 230
    content.scrollLeft = 19
    const changed = wrapper.setProps({ scrollResetKey: "package-b" })
    wrapper.unmount()
    await changed
    await flushPromises()
    expect([content.scrollTop, content.scrollLeft]).toEqual([230, 19])
})
