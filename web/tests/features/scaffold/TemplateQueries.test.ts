/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\TemplateQueries.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模板页固定查询选择、跨步骤保留、真实草稿提交和只读代码复制.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
import TemplateQueries from "@/features/scaffold/TemplateQueries.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import templateQueries from "@/features/scaffold/templateQueries.json"
import * as messageUi from "@/ui/message"
import { validateScaffoldBase } from "@/features/scaffold/validation"

const originalWriteText = navigator.clipboard.writeText
afterEach(() => {
    setLocale("zh-cn")
    vi.restoreAllMocks()
    expect(navigator.clipboard.writeText).toBe(originalWriteText)
})

it("辅助表的默认及编辑分组跨步骤、语言和重新挂载保留, 创建仅提交选中项的显式分组", async () => {
    const transport = new MockTransport()
    const send = vi.spyOn(transport, "postMessage")
    const bridge = markRaw(new BridgeClient(transport))
    let wrapper = mount(ScaffoldPanel, { props: { bridge } })
    try {
        await flushPromises()
        expect(wrapper.findAll("[data-template-query] input")).toHaveLength(4)
        expect(wrapper.findAll("[data-template-group] input").map((item) => (item.element as HTMLInputElement).value)).toEqual([
            "model",
            "model",
            "model",
            "config",
        ])
        await wrapper.get('[data-template-group="Hour24"] input').setValue("业务辅助")
        await wrapper.get('[data-template-group="MQHY"] input').setValue("保留但本次不提交")
        await wrapper.get('[data-template-group="RefreshTime"] input').setValue("刷新分组")
        await wrapper.get('[data-template-query="Hour24"] input').setValue(true)
        await wrapper.get('[data-template-query="TypeDetails"] input').setValue(true)
        await wrapper.get('[data-template-query="RefreshTime"] input').setValue(true)
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get('[data-step="scaffold-template"]').trigger("click")
        expect((wrapper.get('[data-template-query="Hour24"] input').element as HTMLInputElement).checked).toBe(true)
        expect((wrapper.get('[data-template-group="Hour24"] input').element as HTMLInputElement).value).toBe("业务辅助")
        setLocale("en-us")
        await flushPromises()
        expect((wrapper.get('[data-template-group="MQHY"] input').element as HTMLInputElement).value).toBe("保留但本次不提交")
        expect(wrapper.findComponent(TemplateQueries).props("queries")).toEqual(templateQueries)
        expect((wrapper.get('[data-template-query="RefreshTime"] input').element as HTMLInputElement).checked).toBe(true)
        expect((wrapper.get('[data-template-group="RefreshTime"] input').element as HTMLInputElement).value).toBe("刷新分组")
        wrapper.unmount()
        wrapper = mount(ScaffoldPanel, { props: { bridge } })
        await flushPromises()
        expect((wrapper.get('[data-template-query="Hour24"] input').element as HTMLInputElement).checked).toBe(true)
        expect((wrapper.get('[data-template-group="Hour24"] input').element as HTMLInputElement).value).toBe("业务辅助")
        expect((wrapper.get('[data-template-group="MQHY"] input').element as HTMLInputElement).value).toBe("保留但本次不提交")
        expect((wrapper.get('[data-template-query="RefreshTime"] input').element as HTMLInputElement).checked).toBe(true)
        expect((wrapper.get('[data-template-group="RefreshTime"] input').element as HTMLInputElement).value).toBe("刷新分组")
        setLocale("zh-cn")
        await flushPromises()
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "选择目录")!
            .trigger("click")
        await flushPromises()
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "创建项目")!
            .trigger("click")
        await flushPromises()
        const updates = send.mock.calls.map(([value]) => value as { method: string; params: { section?: string; value?: unknown } })
        expect(updates.find((item) => item.method === "scaffold.updateDraft" && item.params.section === "templateQueries")?.params.value).toEqual([
            "Hour24",
            "RefreshTime",
            "TypeDetails",
        ])
        expect(updates.find((item) => item.method === "scaffold.updateDraft" && item.params.section === "templateQueryGroups")?.params.value).toEqual({
            Hour24: "业务辅助",
            TypeDetails: "config",
            RefreshTime: "刷新分组",
        })
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("未选择也能预览并复制宿主原文, 切语言保持选择和代码", async () => {
    const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    const transport = new MockTransport()
    const send = vi.spyOn(transport, "postMessage")
    const bridge = markRaw(new BridgeClient(transport))
    const wrapper = mount(ScaffoldPanel, { props: { bridge } })
    try {
        await flushPromises()
        const requestCount = send.mock.calls.length
        await Promise.all(templateQueries.map((query) => wrapper.get(`[data-template-copy="${query.id}"]`).trigger("click")))
        expect(writeText.mock.calls.map(([value]) => value)).toEqual(templateQueries.map((query) => query.expression))
        expect((wrapper.get('[data-template-query="Hour24"] input').element as HTMLInputElement).checked).toBe(false)
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.text()).toContain("Helper tables")
        expect(wrapper.text()).toContain("Hour24")
        await wrapper.get("button[aria-expanded]").trigger("click")
        expect(wrapper.findComponent(CodeEditor).props("modelValue")).toBe(templateQueries[0]!.expression)
        expect(send.mock.calls).toHaveLength(requestCount)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("临时操作锁阻止选择和复制, 解锁后原文与选择均保留", async () => {
    const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    const wrapper = mount(TemplateQueries, { props: { queries: templateQueries, modelValue: ["MQHY"], groups: { Hour24: "独立分组" }, disabled: true } })
    try {
        expect(wrapper.findAll("input").every((item) => (item.element as HTMLInputElement).disabled)).toBe(true)
        expect((wrapper.get('[data-template-query="MQHY"] input').element as HTMLInputElement).checked).toBe(true)
        const groupInput = wrapper.get('[data-template-group="Hour24"] input')
        expect((groupInput.element as HTMLInputElement).value).toBe("独立分组")
        await groupInput.setValue("不得修改")
        expect(wrapper.emitted("update:groups")).toBeUndefined()
        await wrapper.get('[data-template-copy="Hour24"]').trigger("click")
        expect(writeText).not.toHaveBeenCalled()
        expect(wrapper.findAllComponents(CodeEditor).every((editor) => editor.props("readonly") === true)).toBe(true)
        await wrapper.setProps({ disabled: false })
        await wrapper.get('[data-template-copy="Hour24"]').trigger("click")
        expect(writeText).toHaveBeenCalledExactlyOnceWith(templateQueries[0]!.expression)
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("复制失败使用统一错误反馈, 不清空可手动选取的只读原文", async () => {
    vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(new Error("clipboard unavailable"))
    const feedback = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const wrapper = mount(TemplateQueries, { props: { queries: templateQueries, modelValue: [], groups: {}, disabled: false } })
    try {
        await wrapper.get("button[aria-expanded]").trigger("click")
        const before = wrapper.findComponent(CodeEditor).props("modelValue")
        await wrapper.get('[data-template-copy="Hour24"]').trigger("click")
        await flushPromises()
        expect(feedback).toHaveBeenCalledWith(expect.any(String), "error")
        expect(wrapper.findComponent(CodeEditor).props("modelValue")).toBe(before)
        expect(wrapper.findComponent(CodeEditor).props("readonly")).toBe(true)
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("清空分组保持辅助表选择、宿主目录和完整 M 原文, 创建显式提交空组", async () => {
    const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    const transport = new MockTransport()
    const send = vi.spyOn(transport, "postMessage")
    const bridge = markRaw(new BridgeClient(transport))
    const wrapper = mount(ScaffoldPanel, { props: { bridge } })
    try {
        await flushPromises()
        const groupInput = wrapper.get('[data-template-group="Hour24"] input')
        const groupId = groupInput.attributes("id")
        expect(groupInput.attributes("maxlength")).toBe("200")
        expect(wrapper.get(`label[for="${groupId}"]`).text()).toBe("分组")
        await groupInput.setValue("")
        expect((wrapper.get('[data-template-query="Hour24"] input').element as HTMLInputElement).checked).toBe(false)
        expect(wrapper.findComponent(TemplateQueries).props("queries")).toEqual(templateQueries)
        await wrapper.get("button[aria-expanded]").trigger("click")
        expect(wrapper.findComponent(CodeEditor).props("modelValue")).toBe(templateQueries[0]!.expression)
        await wrapper.get('[data-template-copy="Hour24"]').trigger("click")
        expect(writeText).toHaveBeenCalledExactlyOnceWith(templateQueries[0]!.expression)
        await wrapper.get('[data-template-query="Hour24"] input').setValue(true)
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "选择目录")!
            .trigger("click")
        await flushPromises()
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "创建项目")!
            .trigger("click")
        await flushPromises()
        const updates = send.mock.calls.map(([value]) => value as { method: string; params: { section?: string; value?: unknown } })
        expect(updates.find((item) => item.method === "scaffold.updateDraft" && item.params.section === "templateQueryGroups")?.params.value).toEqual({
            Hour24: "",
        })
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it.each(["   ", "中".repeat(201), "组\u0000名", null])("仅已选辅助表的非法分组阻止创建: %j", (group) => {
    const input = {
        template: { id: "builtin:blank", name: "blank", source: "builtin", baseline: "a".repeat(64), description: "", warnings: [] },
        projectName: "MyProject",
        output: "C:/host-selected",
        templateQueries: ["Hour24"],
        templateQueryGroups: { Hour24: group as string },
        templateQueryDefinitions: templateQueries,
    }
    expect(validateScaffoldBase(input)?.field).toBe("templateQueryGroups")
    expect(validateScaffoldBase({ ...input, templateQueries: [] })).toBeUndefined()
    expect(validateScaffoldBase({ ...input, templateQueryGroups: { Hour24: "" } })).toBeUndefined()
    expect(validateScaffoldBase({ ...input, templateQueryGroups: { Hour24: "分组 中间 空格" } })).toBeUndefined()
})

it("选中项的分组错误关联真实输入并可通过创建提示定位, 修正后恢复创建", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(ScaffoldPanel, { props: { bridge }, attachTo: document.body })
    try {
        await flushPromises()
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "选择目录")!
            .trigger("click")
        await flushPromises()
        const group = wrapper.get('[data-template-group="Hour24"] input')
        await group.setValue("   ")
        const create = wrapper.findAll("button").find((item) => item.text() === "创建项目")!
        expect((create.element as HTMLButtonElement).disabled).toBe(false)
        await wrapper.get('[data-template-query="Hour24"] input').setValue(true)
        expect((create.element as HTMLButtonElement).disabled).toBe(true)
        expect(group.attributes("aria-invalid")).toBe("true")
        const description = group.attributes("aria-describedby")
        expect(wrapper.get(`[id="${description}"]`).text()).toContain("200")
        await wrapper.get(".scaffold-create-hint").trigger("click")
        await flushPromises()
        expect(document.activeElement).toBe(group.element)
        await group.setValue("中文 分组")
        expect(group.attributes("aria-invalid")).toBeUndefined()
        expect((create.element as HTMLButtonElement).disabled).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
