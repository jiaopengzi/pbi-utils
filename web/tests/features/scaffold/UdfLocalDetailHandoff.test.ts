/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfLocalDetailHandoff.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以真实新建/模型配置父级验证本地详情关闭到编辑的锁交接和卸载保护.
 */
import { afterEach, expect, it } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElSelect } from "element-plus"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"

type Request = { id: string; method: string; params: Record<string, unknown> }
const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))

/** 定位实际本地详情弹窗, 不通过组件私有状态跳过模态或父级更新. */
function localDialog(): DOMWrapper<HTMLElement> {
    return new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="Local.Handoff"]')!)
}

/** 记录真实桥接信封, 其余行为运行既有显式内存宿主, 不 mock 父级/子级锁. */
class RecordingTransport extends MockTransport {
    requests: Request[] = []
    holdLocalGet = false
    held?: Request
    override postMessage(value: unknown): void {
        this.requests.push(structuredClone(value) as Request)
        const request = value as Request
        if (this.holdLocalGet && request.method === "udf.localGet") {
            this.held = request
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        super.postMessage(value)
    }
    /** 释放已发送读取的迟到响应, 仅用于观察卸载后是否仍更新弹窗. */
    release(): void {
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        super.postMessage(this.held!)
    }
}

/** 两个真实父级按业务界面进入 UDF, 保留 busy→父级locked→子级disabled 完整反向链路. */
async function fixture(parent: "scaffold" | "configuration", source: "daxlib" | "local" | "custom") {
    const transport = new RecordingTransport(),
        bridge = markRaw(new BridgeClient(transport))
    await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const local = await bridge.request("udf.localSave", {
        definition: {
            schemaVersion: 1,
            name: "Local.Handoff",
            version: "1.2.3",
            expression: "() => 7",
            description: "**Original local description**",
            dependencies: [],
        },
    }).promise
    const custom = await bridge.request("udf.sourceSave", { name: "Handoff source", baseUrl: "https://github.com/example/udfs" }).promise
    await bridge.request("udf.catalogRefresh", { source: custom.id, sourceBaseline: custom.baseline }).promise
    // 本用例聚焦父级与详情弹窗之间的锁交接, CodeEditor 复用既有父级套件的展示替身.
    // 真实 CodeMirror 行为由编辑器套件覆盖, 本用例仍保留全部诊断门禁.
    const global = {
        stubs: {
            CodeEditor: {
                props: { modelValue: String, language: { type: String, default: "dax" } },
                template: "<pre>{{ modelValue }}</pre>",
            },
        },
    }
    const wrapper =
        parent === "scaffold"
            ? mount(ScaffoldPanel, { attachTo: document.body, props: { bridge }, global })
            : mount(ConfigurationPanel, {
                  attachTo: document.body,
                  props: { bridge, project: structuredClone(previewProject), section: "udfs", active: true },
                  global,
              })
    let closed = false
    const close = () => {
        if (closed) return
        closed = true
        wrapper.unmount()
        bridge.dispose()
    }
    cleanup.push(close)
    await flushPromises()
    if (parent === "scaffold") {
        await wrapper.get('[data-step="scaffold-udfs"]').trigger("click")
        await flushPromises()
        await wrapper.get("#section-scaffold-udfs [data-optional] input").setValue(true)
        await flushPromises()
    }
    const udf = wrapper.getComponent(UdfStep)
    if (source !== "daxlib") {
        const picker = udf.getComponent(ElSelect),
            id = source === "local" ? "local" : custom.id
        await picker.setValue(id)
        picker.vm.$emit("change", id)
        await flushPromises()
    }
    await udf.get('[data-function="Local.Handoff"] button').trigger("click")
    await flushPromises()
    expect(localDialog().exists()).toBe(true)
    expect(udf.props("disabled")).toBe(true)
    transport.requests = []
    return { wrapper, udf, transport, bridge, local, dialog: localDialog, close }
}

it.each((["scaffold", "configuration"] as const).flatMap((parent) => (["daxlib", "local", "custom"] as const).map((source) => ({ parent, source }))))(
    "$parent 的 $source 入口关闭本地详情后重新读取当前基线并打开编辑器",
    async ({ parent, source }) => {
        const { udf, transport, local, dialog } = await fixture(parent, source)
        await dialog()
            .findAll("button")
            .find((button) => button.text() === "编辑")!
            .trigger("click")
        await flushPromises()
        expect(transport.requests.filter((request) => request.method === "udf.localGet").map((request) => request.params)).toEqual([{ id: local.id }])
        const editor = new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="编辑"]')!)
        expect(editor.exists()).toBe(true)
        expect(editor.find<HTMLInputElement>("input").element.value).toBe("Local.Handoff")
        expect(udf.props("disabled")).toBe(true)
    },
)

it("父级真实外部锁在详情交接时保持, 不读取或打开编辑器", async () => {
    const { wrapper, transport, dialog } = await fixture("configuration", "daxlib")
    await wrapper.setProps({ externalLocked: true })
    await dialog()
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .trigger("click")
    await flushPromises()
    expect(transport.requests.filter((request) => request.method === "udf.localGet")).toEqual([])
    expect(document.querySelector('[role="dialog"][aria-label="编辑"]')).toBeNull()
})

it("关闭详情到下一渲染之间卸载不再发读取, 不产生迟到编辑弹窗", async () => {
    const { transport, dialog, close } = await fixture("scaffold", "local")
    dialog()
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .element.dispatchEvent(new MouseEvent("click", { bubbles: true }))
    close()
    await flushPromises()
    expect(transport.requests.filter((request) => request.method === "udf.localGet")).toEqual([])
    expect(document.querySelector('[role="dialog"][aria-label="编辑"]')).toBeNull()
})

it("交接已发读取后卸载, 取消排空与迟到响应不能重新打开编辑器", async () => {
    const { transport, dialog, close } = await fixture("configuration", "custom")
    transport.holdLocalGet = true
    await dialog()
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .trigger("click")
    await flushPromises()
    expect(transport.held?.method).toBe("udf.localGet")
    close()
    transport.release()
    await flushPromises()
    expect(transport.requests.some((request) => request.method === "request.cancel")).toBe(true)
    expect(document.querySelector('[role="dialog"][aria-label="编辑"]')).toBeNull()
})

it("真实新建父级管理本地函数后立即更新可用行, 新建、编辑和删除均不依赖重开来源", async () => {
    const { udf, dialog } = await fixture("scaffold", "daxlib")
    await dialog()
        .findAll("button")
        .find((button) => button.text() === "关闭")!
        .trigger("click")
    await flushPromises()
    await udf
        .findAll("button")
        .find((button) => button.text() === "管理本地函数")!
        .trigger("click")
    await flushPromises()
    const manager = new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="管理本地函数"]')!)
    await manager
        .findAll("button")
        .find((button) => button.text() === "新建本地函数")!
        .trigger("click")
    await flushPromises()
    const create = new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="新建本地函数"]')!)
    await create.findAll("input")[0]!.setValue("Local.Instant")
    udf.findAllComponents(CodeEditor)
        .find((editor) => editor.props("language") === "dax")!
        .vm.$emit("update:modelValue", "() => 8")
    await create
        .findAll("button")
        .find((button) => button.text() === "保存")!
        .trigger("click")
    await flushPromises()
    expect(udf.find('[data-function="Local.Instant"]').exists()).toBe(true)
    const row = manager.findAll(".udf-local-package-row").find((item) => item.text().includes("Local.Instant"))!
    await row
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .trigger("click")
    await flushPromises()
    const edit = new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="编辑"]')!)
    await edit.findAll("input")[0]!.setValue("Local.InstantRenamed")
    await edit.findAll("input")[1]!.setValue("1.1.0")
    await edit
        .findAll("button")
        .find((button) => button.text() === "保存")!
        .trigger("click")
    await flushPromises()
    expect(udf.find('[data-function="Local.Instant"]').exists()).toBe(false)
    expect(udf.get('[data-function="Local.InstantRenamed"]').text()).toContain("1.1.0")
    const updated = manager.findAll(".udf-local-package-row").find((item) => item.text().includes("Local.InstantRenamed"))!
    await updated
        .findAll("button")
        .find((button) => button.text() === "删除")!
        .trigger("click")
    await flushPromises()
    const confirmation = new DOMWrapper(document.querySelector<HTMLElement>('[role="dialog"][aria-label="删除本地函数"]')!)
    await confirmation
        .findAll("button")
        .find((button) => button.text() === "确认删除")!
        .trigger("click")
    await flushPromises()
    expect(udf.find('[data-function="Local.InstantRenamed"]').exists()).toBe(false)
    expect(udf.find('[data-function="Local.Handoff"]').exists()).toBe(true)
    expect(udf.getComponent(ElSelect).props("modelValue")).toBe("daxlib")
})
