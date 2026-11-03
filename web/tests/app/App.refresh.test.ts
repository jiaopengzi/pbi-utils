/**
 * FilePath    : pbi-utils\web\tests\app\App.refresh.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证顶部模型刷新保留业务节点和输入, 只更新权威变化并排空取消.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { ElMessage } from "element-plus"
import { BridgeClient, BridgeError, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import App from "@/app/App.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"

afterEach(() => {
    ElMessage.closeAll()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
})

/** 建立已有模型与可控制的刷新回执, 不执行文件选择或模型修改. */
async function setup(page: "connections" | "model") {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    const connection = wrapper.getComponent(ConnectionPanel)
    const bridge = connection.props("bridge") as BridgeClient
    connection.vm.$emit("connected", structuredClone(previewProject))
    await flushPromises()
    if (page === "connections") {
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
    }
    let resolve!: (snapshot: ProjectSnapshot) => void
    let reject!: (reason: Error) => void
    let authoritative = structuredClone(previewProject)
    const pending = new Promise<ProjectSnapshot>((done, fail) => {
        resolve = (snapshot) => {
            authoritative = snapshot
            done(snapshot)
        }
        reject = fail
    })
    const original = bridge.request.bind(bridge)
    const request = vi.spyOn(bridge, "request").mockImplementation((method, ...args) => {
        const forward = () => original(method, ...args)
        if (method === "project.inspect") return { id: "refresh-pending", promise: pending }
        if (method === "configuration.context") {
            // 两个只读入口属于同一宿主, 后续配置读取必须使用刷新后权威模型, 不能回复夹具旧基线.
            const context = original("configuration.context", { target: "current" })
            return { ...context, promise: context.promise.then((value) => ({ ...value, project: authoritative })) }
        }
        return forward()
    })
    const refresh = wrapper.findAll(".header-actions button").find((button) => button.text() === "刷新模型")!
    return { wrapper, request, refresh, resolve, reject }
}

it.each(["connections", "model"] as const)("%s 刷新在途及无变化回读保留原节点, 不刷新无关最近列表", async (page) => {
    const { wrapper, request, refresh, resolve } = await setup(page)
    try {
        const selector = page === "connections" ? ".connection-center" : ".measures-panel"
        const panel = wrapper.get(selector).element
        const search = page === "model" ? wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]') : undefined
        if (search) await search.setValue("销售")
        const editor = page === "model" ? wrapper.get(".cm-editor").element : undefined
        const projectBefore = page === "model" ? wrapper.getComponent(ModelPanel).props("project") : undefined
        await refresh.trigger("click")
        await flushPromises()
        expect(wrapper.get(selector).element).toBe(panel)
        expect(wrapper.find(".content > .connection-progress").exists()).toBe(false)
        expect(wrapper.find(".operation-feedback").exists()).toBe(false)
        expect(document.querySelector(".operation-feedback")?.textContent).toContain("正在刷新当前模型")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
        expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
        resolve(structuredClone(previewProject))
        await flushPromises()
        expect(wrapper.get(selector).element).toBe(panel)
        expect(document.querySelector(".operation-feedback")).toBeNull()
        if (search) {
            expect(wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]').element).toBe(search.element)
            expect((search.element as HTMLInputElement).value).toBe("销售")
            expect(wrapper.get(".cm-editor").element).toBe(editor)
            expect(wrapper.getComponent(ModelPanel).props("project")).toBe(projectBefore)
        }
        expect(request.mock.calls.map(([method]) => method)).toEqual(
            page === "model" ? ["project.inspect", "scaffold.templates", "configuration.context"] : ["project.inspect"],
        )
    } finally {
        wrapper.unmount()
    }
})

it("单个度量变化局部更新, 保留模型组件和未变化行", async () => {
    const { wrapper, refresh, resolve } = await setup("model")
    try {
        const panelId = wrapper.getComponent(ModelPanel).vm.$.uid
        const row = wrapper.get(".measure-row").element
        const next = structuredClone(previewProject)
        next.baselineId = "changed-baseline"
        next.model.measures[0]!.description = "宿主最新说明"
        await refresh.trigger("click")
        resolve(next)
        await flushPromises()
        expect(wrapper.getComponent(ModelPanel).vm.$.uid).toBe(panelId)
        expect(wrapper.get(".measure-row").element).toBe(row)
        expect(wrapper.getComponent(ModelPanel).props("project").baselineId).toBe("changed-baseline")
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "宿主最新说明")
    } finally {
        wrapper.unmount()
    }
})

it.each(["CANCELLED", "READ_FAILED"])("刷新 %s 保留模型和编辑节点, 取消确认不能提前解锁", async (code) => {
    const { wrapper, request, refresh, reject } = await setup("model")
    try {
        const panel = wrapper.get(".measures-panel").element
        const input = wrapper.get('textarea[aria-label="说明"]')
        await input.setValue("未提交草稿")
        await refresh.trigger("click")
        await flushPromises()
        if (code === "CANCELLED") {
            await new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!).trigger("click")
            await flushPromises()
            expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "refresh-pending" })
            expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
            expect(document.querySelector(".operation-feedback__cancel")?.hasAttribute("disabled")).toBe(true)
        }
        reject(new BridgeError(code, "刷新没有完成"))
        await flushPromises()
        expect(wrapper.get(".measures-panel").element).toBe(panel)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toBe(input.element)
        expect(input.element).toHaveProperty("value", "未提交草稿")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(document.querySelector(".operation-feedback")).toBeNull()
        expect(document.querySelector(".el-message--error") !== null).toBe(code !== "CANCELLED")
    } finally {
        wrapper.unmount()
    }
})
