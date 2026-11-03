/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableSampling.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型侧抽样与全部行导出的明确意图, 不能用样本行数伪造源表总数.
 */
import { expect, it } from "vitest"
import { flushPromises } from "@vue/test-utils"
import { ElInputNumber, ElRadioGroup } from "element-plus"
import { setLocale } from "@/i18n"
import { exportResult, selectTable, setupTables, tableItem } from "./ModelTablesPanel.support"

it("导出默认每表抽样100行, 样本完成不改写源表统计或宣称全部行", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables({
        statistics: [{ table: "销售", rowCount: 900, sizeBytes: 2048, isEstimate: true, isQueryCount: false }],
    })
    try {
        expect(wrapper.findComponent(ElRadioGroup).props("modelValue")).toBe("sample")
        expect(wrapper.findComponent(ElInputNumber).props("modelValue")).toBe(100)
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[0]).toMatchObject({ method: "model.exportTables", params: { tables: ["销售"], rowLimit: 100 } })
        send({ id: sent[0]!.id, result: exportResult("sample.xlsx", ["销售"], 100) })
        await flushPromises()
        expect(tableItem(wrapper, "销售").get(".el-collapse-item__header").text()).toContain("900")
        expect(document.querySelector(".el-message--success")?.textContent).toContain("sample.xlsx")
        expect(wrapper.get(".table-export-toolbar").text()).toContain("抽样导出")
        expect(document.querySelector(".el-message--success")?.textContent).not.toMatch(/总共\s*100|全部\s*100|已截断|truncated/i)
    } finally {
        cleanup()
    }
})

it("自定样本量在语言与模式切换后保留, 明确全部行时传null", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElInputNumber).vm.$emit("update:modelValue", 25)
        await flushPromises()
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.findComponent(ElInputNumber).props("modelValue")).toBe(25)
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[0]!.params.rowLimit).toBe(25)
        send({ id: sent[0]!.id, result: exportResult("sample.xlsx", ["销售"], 7, "xlsx", false, 25) })
        await flushPromises()
        expect(document.querySelector(".el-message--success")?.textContent).toContain("7")
        expect(document.querySelector(".el-message--success")?.textContent).not.toMatch(/截断|truncated/i)
        wrapper.findComponent(ElRadioGroup).vm.$emit("update:modelValue", "all")
        await flushPromises()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[1]!.params.rowLimit).toBeNull()
        send({ id: sent[1]!.id, result: exportResult("all.xlsx", ["销售"], 700, "xlsx", false, null) })
        await flushPromises()
        wrapper.findComponent(ElRadioGroup).vm.$emit("update:modelValue", "sample")
        await flushPromises()
        expect(wrapper.findComponent(ElInputNumber).props("modelValue")).toBe(25)
        expect(wrapper.get(".table-export-button").text()).toBe("Export selected tables")
    } finally {
        cleanup()
    }
})

it.each([1.5, Number.NaN, undefined])("未归一的非法样本行数%s不提交导出", async (value) => {
    const { wrapper, sent, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElInputNumber).vm.$emit("update:modelValue", value)
        await flushPromises()
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it.each([
    [0, 1],
    [-1, 1],
    [2147483648, 2147483647],
])("越界行数%d由数字控件归一为%d, 请求只包含有效行数", async (input, normalized) => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElInputNumber).vm.$emit("update:modelValue", input)
        await flushPromises()
        expect(wrapper.findComponent(ElInputNumber).props("modelValue")).toBe(normalized)
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[0]!.params.rowLimit).toBe(normalized)
        send({ id: sent[0]!.id, result: exportResult("normalized.xlsx", ["销售"], 0, "xlsx", false, normalized) })
        await flushPromises()
    } finally {
        cleanup()
    }
})
