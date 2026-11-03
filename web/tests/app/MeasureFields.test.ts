/**
 * FilePath    : pbi-utils\web\tests\app\MeasureFields.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证度量值格式和文件夹下拉的真实键盘选择、自定义输入、双语及批量锁定守卫.
 */
import { afterEach, expect, it } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { reactive } from "vue"
import { ElAutocomplete } from "element-plus"
import type { MeasureChange } from "@/bridge/client"
import MeasureFields from "@/app/MeasureFields.vue"
import { setLocale } from "@/i18n"

afterEach(() => setLocale("zh-cn"))

it.each([
    ["zh-cn", "格式字符串", "货币 (区域设置)", "中日期 (同长日期)", "真/假"],
    ["en-us", "Format string", "Currency (locale)", "Medium date (same as long date)", "True/False"],
] as const)("%s 下按本地名称查找并选择预定义格式, 只将官方格式标识写入草稿", async (locale, label, currencyLabel, mediumDateLabel, booleanLabel) => {
    setLocale(locale)
    const { fields, wrapper, suggestions } = await setup()
    try {
        const input = wrapper.get(`input[aria-label="${label}"]`)
        await input.setValue(currencyLabel)
        await input.trigger("focus")
        await input.trigger("input")
        await flushPromises()
        const currency = suggestions(label).find((option) => option.text().includes(currencyLabel))!
        expect(currency).toBeDefined()
        await currency.trigger("click")
        expect(fields.formatString).toBe("Currency")
        await input.trigger("blur")
        await input.trigger("focus")
        await flushPromises()
        expect(suggestions(label).some((option) => option.text().includes(booleanLabel))).toBe(true)
        await suggestions(label)
            .find((option) => option.text().includes(mediumDateLabel))!
            .trigger("click")
        expect(fields.formatString).toBe("Medium Date")
    } finally {
        wrapper.unmount()
    }
})

/** 挂载真实表单并以父级对象接收即时草稿, 提供关联列表查询, 调用方负责 unmount. */
async function setup(options: { batch?: boolean; disabled?: boolean; enabled?: string[]; formatString?: string; displayFolder?: string } = {}) {
    const fields = reactive<Required<Omit<MeasureChange, "action" | "table" | "name">>>({
        expression: "1",
        description: "",
        formatString: options.formatString ?? "0.00",
        displayFolder: options.displayFolder ?? "财务\\收入",
        isHidden: false,
    })
    const wrapper = mount(MeasureFields, {
        attachTo: document.body,
        props: {
            modelValue: fields,
            displayFolders: ["财务\\收入;经营\\收入", "财务\\成本", "财务\\收入"],
            batch: options.batch,
            disabled: options.disabled,
            enabled: options.enabled,
        },
    })
    /** 按无障碍名称找到真实输入所属建议列表, 不读取组件内部状态. */
    function suggestions(label: string) {
        const root = wrapper.get(`input[aria-label="${label}"]`).element.closest('[role="combobox"]')!
        const list = document.getElementById(root.getAttribute("aria-owns")!)!
        return new DOMWrapper(list).findAll('[role="option"]')
    }
    await flushPromises()
    return { fields, wrapper, suggestions }
}

it("格式聚焦列出常用预设, 键盘选择整数且常规写入空串, 不把标签当格式", async () => {
    const { fields, wrapper, suggestions } = await setup()
    try {
        const input = wrapper.get('input[aria-label="格式字符串"]')
        await input.trigger("focus")
        await flushPromises()
        const options = suggestions("格式字符串")
        for (const expected of ["常规", "#,##0", "0.00", "yyyy-mm-dd", "0.00E+00", "hh:nn:ss", "True/False"]) {
            expect(options.some((option) => option.text().includes(expected))).toBe(true)
        }
        await input.trigger("keydown", { key: "ArrowDown", code: "ArrowDown" })
        await input.trigger("keydown", { key: "ArrowDown", code: "ArrowDown" })
        await input.trigger("keydown", { key: "Enter", code: "Enter" })
        await flushPromises()
        expect(fields.formatString).toBe("0")
        await input.trigger("blur")
        await flushPromises()
        await input.trigger("focus")
        await flushPromises()
        await suggestions("格式字符串")
            .find((option) => option.text() === "常规")!
            .trigger("click")
        expect(fields.formatString).toBe("")
    } finally {
        wrapper.unmount()
    }
})

it("任意自定义格式与多文件夹路径无需确认即进入草稿, 输入后筛选建议, Escape 保留输入", async () => {
    const { fields, wrapper, suggestions } = await setup()
    try {
        const format = wrapper.get('input[aria-label="格式字符串"]')
        const folder = wrapper.get('input[aria-label="显示文件夹"]')
        const customFormat = '#,##0.000;(#,##0.000);"-"'
        const customFolder = "新目录\\指标;财务\\成本"
        await format.setValue(customFormat)
        await folder.setValue(customFolder)
        expect(fields.formatString).toBe(customFormat)
        expect(fields.displayFolder).toBe(customFolder)
        expect(format.attributes("maxlength")).toBe("2000")
        expect(folder.attributes("maxlength")).toBe("2000")
        await folder.setValue("收入")
        await flushPromises()
        expect(suggestions("显示文件夹").map((option) => option.text())).toEqual(["财务\\收入", "经营\\收入"])
        await folder.trigger("keydown", { key: "Escape", code: "Escape" })
        expect(fields.displayFolder).toBe("收入")
    } finally {
        wrapper.unmount()
    }
})

it("现有文件夹下拉含去重祖先与多位置, 可选择后清空, 模型目录更新会生效", async () => {
    const { fields, wrapper, suggestions } = await setup()
    try {
        const folder = wrapper.get('input[aria-label="显示文件夹"]')
        await folder.trigger("focus")
        await flushPromises()
        expect(new Set(suggestions("显示文件夹").map((option) => option.text()))).toEqual(new Set(["财务", "财务\\收入", "财务\\成本", "经营", "经营\\收入"]))
        await suggestions("显示文件夹")
            .find((option) => option.text() === "财务\\成本")!
            .trigger("click")
        expect(fields.displayFolder).toBe("财务\\成本")
        const control = wrapper.findAllComponents(ElAutocomplete).find((node) => node.get("input").attributes("aria-label") === "显示文件夹")!
        await control.get(".el-input").trigger("mouseenter")
        await flushPromises()
        await control.get(".el-input__clear").trigger("click")
        expect(fields.displayFolder).toBe("")
        await wrapper.setProps({ displayFolders: ["更新\\目录"] })
        await folder.trigger("blur")
        await flushPromises()
        await folder.trigger("focus")
        await flushPromises()
        expect(suggestions("显示文件夹").map((option) => option.text())).toEqual(["更新", "更新\\目录"])
    } finally {
        wrapper.unmount()
    }
})

it("切语言更新打开的预设标签和占位, 保留输入节点及自定义原文", async () => {
    const { fields, wrapper, suggestions } = await setup({ formatString: '0.0" custom"' })
    try {
        const input = wrapper.get('input[aria-label="格式字符串"]')
        await input.trigger("focus")
        await flushPromises()
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get('input[aria-label="Format string"]').element).toBe(input.element)
        expect(suggestions("Format string").some((option) => option.text() === "General")).toBe(true)
        expect(wrapper.get('input[aria-label="Format string"]').attributes("placeholder")).toBe("Choose a preset or enter a custom format")
        expect(fields.formatString).toBe('0.0" custom"')
        expect(fields.displayFolder).toBe("财务\\收入")
    } finally {
        wrapper.unmount()
    }
})

it("批量只修改勾选字段, 锁定或超长的迟到选择不改草稿", async () => {
    const { fields, wrapper } = await setup({ batch: true })
    try {
        const controls = wrapper.findAllComponents(ElAutocomplete)
        const format = controls.find((node) => node.get("input").attributes("aria-label") === "格式字符串")!
        const folder = controls.find((node) => node.get("input").attributes("aria-label") === "显示文件夹")!
        expect(format.get<HTMLInputElement>("input").element.disabled).toBe(true)
        expect(folder.get<HTMLInputElement>("input").element.disabled).toBe(true)
        folder.vm.$emit("update:modelValue", "迟到目录")
        expect(fields.displayFolder).toBe("财务\\收入")
        await wrapper.setProps({ enabled: ["formatString"] })
        expect(format.get<HTMLInputElement>("input").element.disabled).toBe(false)
        await format.get("input").setValue("0.0000")
        expect(fields.formatString).toBe("0.0000")
        format.vm.$emit("update:modelValue", "x".repeat(2001))
        expect(fields.formatString).toBe("0.0000")
        await wrapper.setProps({ disabled: true })
        format.vm.$emit("update:modelValue", "0%")
        folder.vm.$emit("update:modelValue", "迟到目录")
        expect(fields).toMatchObject({ formatString: "0.0000", displayFolder: "财务\\收入" })
    } finally {
        wrapper.unmount()
    }
})
