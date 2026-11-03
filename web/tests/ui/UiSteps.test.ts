/**
 * FilePath    : pbi-utils\web\tests\ui\UiSteps.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证品牌步骤条当前步骤语义、真实按钮与键盘导航及临时锁, 不以单测替代浏览器颜色和命中验收.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import UiSteps from "@/ui/UiSteps.vue"

const items = [
    ["template", "模板"],
    ["sample", "模拟数据"],
    ["measures", "模板度量值"],
    ["calendar", "日期与时间智能"],
    ["udfs", "UDF"],
]
const wrappers: ReturnType<typeof mount>[] = []
afterEach(() => {
    for (const wrapper of wrappers) wrapper.unmount()
    wrappers.length = 0
})

/** 挂载真实 Element Plus 步骤条并登记清理, 保留浏览器级按钮和焦点语义. */
function render(modelValue = "sample", disabled = false) {
    const wrapper = mount(UiSteps, { props: { modelValue, items, disabled }, attachTo: document.body })
    wrappers.push(wrapper)
    return wrapper
}

it("当前步骤只有一个可达焦点及aria-current, 访问较后步骤不将前序可选项标成完成", async () => {
    const wrapper = render("udfs")
    await flushPromises()
    expect(wrapper.findAll('[aria-current="step"]')).toHaveLength(1)
    expect(wrapper.get('[aria-current="step"]').attributes("data-step")).toBe("udfs")
    expect(wrapper.findAll('button[tabindex="0"]')).toHaveLength(1)
    expect(wrapper.findAll(".el-step__head.is-success, .el-step__head.is-finish")).toHaveLength(0)
    expect(wrapper.findAll(".el-step__head.is-wait")).toHaveLength(4)
    for (const item of items) {
        const button = wrapper.get(`[data-step="${item[0]}"]`)
        expect(button.element.tagName).toBe("BUTTON")
        expect(button.attributes("type")).toBe("button")
        expect(button.attributes("aria-controls")).toBe(`section-${item[0]}`)
    }
})

it("鼠标可以往返选择任意步骤, 标签变化保留同一按钮和当前身份", async () => {
    const wrapper = render()
    await ["udfs", "template", "calendar", "measures", "sample"].reduce(
        (queue, id) =>
            queue.then(async () => {
                await wrapper.get(`[data-step="${id}"]`).trigger("click")
                expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([id])
                await wrapper.setProps({ modelValue: id })
                expect(wrapper.get(`[data-step="${id}"]`).attributes("aria-current")).toBe("step")
            }),
        Promise.resolve(),
    )
    const selected = wrapper.get('[data-step="sample"]').element
    await wrapper.setProps({ items: items.map(([id]) => [id!, `English ${id}`]) })
    expect(wrapper.get('[data-step="sample"]').element).toBe(selected)
    expect(wrapper.get('[data-step="sample"]').text()).toBe("English sample")
    expect(wrapper.get('[data-step="sample"]').attributes("aria-current")).toBe("step")
})

it("方向与首尾键移动步骤及焦点并截断外层导航, 首尾不会越界", async () => {
    const wrapper = render("template")
    const outer = vi.fn()
    const container = document.createElement("div")
    wrapper.element.parentElement!.append(container)
    container.append(wrapper.element)
    container.addEventListener("keydown", outer)
    const cases = [
        ["template", "ArrowLeft", "template"],
        ["template", "ArrowRight", "sample"],
        ["sample", "ArrowDown", "measures"],
        ["measures", "ArrowUp", "sample"],
        ["sample", "End", "udfs"],
        ["udfs", "ArrowRight", "udfs"],
        ["udfs", "Home", "template"],
    ] as const
    await cases.reduce(
        (queue, [current, key, target]) =>
            queue.then(async () => {
                await wrapper.setProps({ modelValue: current })
                const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true })
                wrapper.get(`[data-step="${current}"]`).element.dispatchEvent(event)
                await flushPromises()
                expect(event.defaultPrevented).toBe(true)
                expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([target])
                expect(document.activeElement).toBe(wrapper.get(`[data-step="${target}"]`).element)
            }),
        Promise.resolve(),
    )
    expect(outer).not.toHaveBeenCalled()
    container.removeEventListener("keydown", outer)
    container.remove()
})

it("在途锁不改变当前身份或移除焦点, 阻止鼠标键盘切换并允许Tab离开", async () => {
    const wrapper = render("measures")
    const current = wrapper.get('[data-step="measures"]')
    ;(current.element as HTMLButtonElement).focus()
    await wrapper.setProps({ disabled: true })
    expect(wrapper.attributes("aria-disabled")).toBe("true")
    expect(document.activeElement).toBe(current.element)
    expect(current.attributes("aria-current")).toBe("step")
    for (const button of wrapper.findAll("button")) {
        expect(button.attributes("aria-disabled")).toBe("true")
        button.element.dispatchEvent(new MouseEvent("click", { bubbles: true }))
    }
    for (const key of ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "Enter", " "])
        current.element.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }))
    await flushPromises()
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    expect(current.attributes("aria-current")).toBe("step")
    const leave = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true })
    current.element.dispatchEvent(leave)
    expect(leave.defaultPrevented).toBe(false)
    await wrapper.setProps({ disabled: false })
    await wrapper.get('[data-step="udfs"]').trigger("click")
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["udfs"])
})
