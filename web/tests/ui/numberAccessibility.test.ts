/**
 * FilePath    : pbi-utils\web\tests\ui\numberAccessibility.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 真实数字控件切换禁用及范围后维持ARIA一致, 保留输入身份、焦点、草稿和原有更新事件.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { defineComponent } from "vue"
import { ElInputNumber } from "element-plus"
import { vNumberAccessibility } from "@/ui/numberAccessibility"

/** 原组件与同一个共享指令一起挂载, 不替换输入实现或模拟其原生禁用行为. */
const Harness = defineComponent({
    components: { ElInputNumber },
    directives: { numberAccessibility: vNumberAccessibility },
    props: { disabled: Boolean, min: Number, max: Number, modelValue: Number, ariaLabel: String },
    emits: ["update:modelValue"],
    template: `<ElInputNumber v-number-accessibility :model-value="modelValue" :disabled="disabled" :min="min" :max="max" :aria-label="ariaLabel" @update:model-value="$emit('update:modelValue', $event)" />`,
})

it("原生disabled与aria-disabled双向同步, simple/full年份范围同步到aria", async () => {
    const wrapper = mount(Harness, { props: { modelValue: 2026, disabled: true, min: 1900, max: 2100, ariaLabel: "年份" }, attachTo: document.body })
    try {
        const input = wrapper.get("input"),
            element = input.element
        expect(input.element.disabled).toBe(true)
        expect(input.attributes("aria-disabled")).toBe("true")
        await wrapper.setProps({ disabled: false, min: 2000, max: 2099 })
        expect(wrapper.get("input").element).toBe(element)
        expect(input.element.disabled).toBe(false)
        expect(input.attributes("aria-disabled")).toBe("false")
        expect(input.attributes("aria-valuemin")).toBe("2000")
        expect(input.attributes("aria-valuemax")).toBe("2099")
        expect(element.value).toBe("2026")
        element.focus()
        await wrapper.setProps({ ariaLabel: "Year" })
        expect(document.activeElement).toBe(element)
        expect(input.attributes("aria-label")).toBe("Year")
        await wrapper.setProps({ disabled: true, min: 1900, max: 2100 })
        expect(wrapper.get("input").element).toBe(element)
        expect(input.element.disabled).toBe(true)
        expect(input.attributes("aria-disabled")).toBe("true")
        expect(input.attributes("aria-valuemin")).toBe("1900")
        expect(input.attributes("aria-valuemax")).toBe("2100")
    } finally {
        wrapper.unmount()
    }
})

it("共享同步不重置真实未提交草稿, 解锁后继续发送原组件数值更新", async () => {
    const wrapper = mount(Harness, { props: { modelValue: 100, disabled: false, min: 1, max: 2147483647, ariaLabel: "每表行数" }, attachTo: document.body })
    try {
        const input = wrapper.get("input"),
            element = input.element
        element.value = "25"
        await input.trigger("input")
        expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([25])
        expect(element.value).toBe("25")
        await wrapper.setProps({ disabled: true })
        expect(element.disabled).toBe(true)
        expect(input.attributes("aria-disabled")).toBe("true")
        expect(element.value).toBe("25")
        await wrapper.setProps({ disabled: false })
        expect(wrapper.get("input").element).toBe(element)
        expect(element.disabled).toBe(false)
        expect(input.attributes("aria-disabled")).toBe("false")
        expect(element.value).toBe("25")
        element.value = "42"
        await input.trigger("input")
        expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([42])
    } finally {
        wrapper.unmount()
    }
})
