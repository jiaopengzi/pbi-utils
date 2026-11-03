/**
 * FilePath    : pbi-utils\web\tests\app\ModelLanguage.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型工作区双语显示、模板表单和编辑草稿在语言切换后保持原文.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { EditorView } from "@codemirror/view"
import { ElOption } from "element-plus"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"
import ModelPanel from "@/app/ModelPanel.vue"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import MeasuresPanel from "@/app/MeasuresPanel.vue"

it("模型编辑栏切换语言只更新固定文案, 保留名称、DAX 和用户输入且不提交修改", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(MeasuresPanel, { props: { bridge, project: structuredClone(previewProject) } })
    await flushPromises()
    const editor = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!
    const raw = "SUM('销售'[金额]) + 123"
    editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: raw }, userEvent: "input" })
    await wrapper.get('textarea[aria-label="说明"]').setValue("用户自定义说明")
    setLocale("en-us")
    await flushPromises()
    expect(wrapper.get("h2").text()).toBe("Measures")
    expect(wrapper.get('input[aria-label="Search names, folders or DAX"]').attributes("placeholder")).toBe("Search tables, folders, measures or DAX…")
    expect(wrapper.text()).toContain("销售额")
    expect(wrapper.text()).not.toContain("View saved DAX")
    expect(wrapper.get('textarea[aria-label="Description"]').element).toHaveProperty("value", "用户自定义说明")
    expect(editor.state.doc.toString()).toBe(raw)
    expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)).toBe(editor)
    expect(request).not.toHaveBeenCalled()
    setLocale("zh-cn")
    await flushPromises()
    expect(wrapper.get("h2").text()).toBe("度量值")
    expect(editor.state.doc.toString()).toBe(raw)
    wrapper.unmount()
    bridge.dispose()
})

it("英文模型表格与 UDF 显示计数和能力限制, 模型对象与函数表达式保持原文", async () => {
    setLocale("en-us")
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const project = structuredClone(previewProject)
    project.connection = { kind: "ssas-xmla", label: "真实模型" }
    project.model.functions = [{ name: "原始函数", description: "原始说明", expression: "(x) => x + 1" }]
    const wrapper = mount(ModelPanel, { props: { bridge, project, initialTab: "tables" } })
    try {
        await flushPromises()
        expect(wrapper.text()).toContain("1 tables")
        expect(wrapper.findAll(".table-list-head .table-title span").map((cell) => cell.text())).toEqual(["Table", "Rows", "Size", "Columns"])
        const tableHeader = wrapper.get(".el-collapse-item__header")
        expect(tableHeader.findAll(".table-title span").map((cell) => cell.text())).toEqual(["销售", "-", "-", "1"])
        expect(wrapper.text()).toContain("2 relationships · 0 roles")
        await tableHeader.trigger("click")
        expect(tableHeader.attributes("aria-expanded")).toBe("true")
        expect(wrapper.findAll("th").map((cell) => cell.text())).not.toContain("Kind")
        expect(wrapper.findAll("th").map((cell) => cell.text())).toContain("Data type")
        expect(wrapper.get(".table-export-toolbar").attributes("aria-label")).toBe("Export data")
        expect(wrapper.get(".table-export-button").text()).toBe("Export selected tables")
        expect(wrapper.findAll(".table-metadata dt").map((cell) => cell.text())).toEqual(["Columns", "Rows", "Size", "Partition type"])
        setLocale("zh-cn")
        await flushPromises()
        expect(wrapper.findAll(".table-list-head .table-title span").map((cell) => cell.text())).toEqual(["表", "行数", "大小", "列数"])
        expect(wrapper.get(".table-metadata").attributes("aria-label")).toBe("表元信息")
        expect(tableHeader.attributes("aria-expanded")).toBe("true")
        expect(tableHeader.findAll(".table-title span").map((cell) => cell.text())).toEqual(["销售", "-", "-", "1"])
        expect(wrapper.get(".table-export-button").text()).toBe("导出所选表")
        setLocale("en-us")
        await flushPromises()
        expect(tableHeader.attributes("aria-expanded")).toBe("true")
        expect(wrapper.get(".table-metadata").attributes("aria-label")).toBe("Table metadata")
        expect(wrapper.findAll("th").map((cell) => cell.text())).toContain("Data type")
        await wrapper
            .findAll('[role="tab"]')
            .find((tab) => tab.text() === "UDF")!
            .trigger("click")
        expect(wrapper.text()).toContain("1 model functions")
        expect(wrapper.text()).toContain("原始函数")
        await wrapper
            .findAll(".el-collapse-item__header")
            .find((item) => item.text().includes("原始函数"))!
            .trigger("click")
        expect(wrapper.text()).toContain("原始说明")
        expect(wrapper.find("code").text()).toBe("(x) => x + 1")
        expect(wrapper.get(".code-toolbar button").text()).toBe("Copy DAX")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("英文 Service 有限架构持续明确未知项, 不展示未确认的零函数或关系", async () => {
    setLocale("en-us")
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const project = structuredClone(previewProject)
    project.connection = { kind: "powerbi-service", label: "服务模型", isLimited: true }
    project.model.measures[0]!.expression = ""
    const wrapper = mount(ModelPanel, { props: { bridge, project } })
    await flushPromises()
    expect(Array.from(document.querySelectorAll(".el-message"), (message) => message.textContent).join(" ")).toContain(
        "partitions, compatibility level, relationships, roles and UDFs are unavailable",
    )
    expect(Array.from(document.querySelectorAll(".el-message"), (message) => message.textContent).join(" ")).toContain(
        "The service did not provide a DAX definition",
    )
    expect(wrapper.text()).not.toContain("Measure templates")
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "UDF")!
        .trigger("click")
    expect(wrapper.text()).toContain("does not provide a compatibility level or UDF definitions")
    expect(wrapper.text()).not.toContain("0 model functions")
    wrapper.unmount()
    bridge.dispose()
})

it("日期、期间、表说明和 UDF 编辑表单在英文显示且属性选项随语言变化", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    try {
        for (const [template, heading] of [
            ["calendar", "Create a Gregorian date table"],
            ["period", "Generate period measures"],
        ] as const) {
            setLocale("en-us")
            const wrapper = mount(ModelEditPanel, { props: { bridge, project: structuredClone(previewProject), template } })
            try {
                expect(wrapper.get("h2").text()).toBe(heading)
                expect(wrapper.text()).toContain("Changes apply directly to the current model or offline PBIP TMDL")
                expect(wrapper.text()).toContain("without automatic backups or data refresh")
                expect(wrapper.text()).not.toContain("Settings page")
                expect(wrapper.text()).not.toContain("关于")
                if (template === "calendar") {
                    expect(wrapper.text()).toContain("Start year")
                    expect(wrapper.text()).toContain("End year")
                } else {
                    expect(wrapper.text()).toContain("Output measure table")
                    expect(wrapper.text()).toContain("Period templates")
                }
            } finally {
                wrapper.unmount()
            }
        }
        for (const operation of ["table.set", "udf.set"]) {
            setLocale("zh-cn")
            const wrapper = mount(ModelEditPanel, { props: { bridge, project: structuredClone(previewProject), initialOperation: operation } })
            try {
                const expected = operation === "table.set" ? "Description" : "Expression"
                setLocale("en-us")
                // oxlint-disable-next-line no-await-in-loop -- 每个挂载实例逐次验证语言切换并卸载.
                await flushPromises()
                expect(wrapper.text()).toContain("Property")
                expect(wrapper.findAllComponents(ElOption).map((option) => option.props("label"))).toContain(expected)
                expect(wrapper.text()).toContain("Apply")
            } finally {
                wrapper.unmount()
            }
        }
    } finally {
        bridge.dispose()
    }
})
