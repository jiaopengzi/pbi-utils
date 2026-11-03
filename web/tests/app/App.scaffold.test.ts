/**
 * FilePath    : pbi-utils\web\tests\app\App.scaffold.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证新建工程 Desktop 连接更新唯一工作区及原模型保护.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import App from "@/app/App.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import NewPanel from "@/app/NewPanel.vue"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
import { BridgeClient, BridgeError } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { ElMessage } from "element-plus"

afterEach(() => {
    ElMessage.closeAll()
    vi.unstubAllGlobals()
})

/** 通过 wrapper 的应用新建入口创建模拟工程, 不返回值; 请求错误由真实组件状态展示. */
async function createProject(wrapper: ReturnType<typeof mount>): Promise<void> {
    await wrapper.get('nav button[aria-label="新建"]').trigger("click")
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
}

it("新建工程 Desktop 连接沿面板更新顶栏和模型页, 模拟当前模型与回执一致", async () => {
    const transport = new MockTransport()
    const requests = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        await createProject(wrapper)
        const scaffold = wrapper.findComponent(ScaffoldPanel)
        const newPanel = wrapper.getComponent(NewPanel)
        const bridge = scaffold.props("bridge") as BridgeClient
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "Power BI Desktop 打开")!
            .trigger("click")
        await flushPromises()
        const snapshot = scaffold.emitted("connected")?.[0]?.[0]
        expect(snapshot).toMatchObject({ connection: { kind: "desktop" } })
        expect(newPanel.emitted("connected")).toEqual([[snapshot]])
        expect(wrapper.get(".header-actions").text()).toContain("Power BI Desktop")
        expect(await bridge.request("project.inspect", {}).promise).toEqual(snapshot)
        expect(await bridge.request("project.recent", {}).promise).toHaveLength(1)
        expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(wrapper.findComponent(ModelPanel).props("project")).toEqual(snapshot)
        expect(requests.mock.calls.filter(([message]) => (message as { method: string }).method === "scaffold.openDesktop")).toHaveLength(1)
        expect(requests.mock.calls.some(([message]) => (message as { method: string }).method === "project.open")).toBe(false)
    } finally {
        wrapper.unmount()
    }
})

it.each(["OPEN_FAILED", "CANCELLED"])("新建 Desktop 连接 %s 保留原工作区", async (code) => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    try {
        await flushPromises()
        const original = { ...structuredClone(previewProject), projectPath: "Original.pbip" }
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(ConnectionPanel).vm.$emit("connected", original)
        await flushPromises()
        await createProject(wrapper)
        const scaffold = wrapper.findComponent(ScaffoldPanel)
        const bridge = scaffold.props("bridge") as BridgeClient
        vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "open-created", promise: Promise.reject(new BridgeError(code, code)) })
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "Power BI Desktop 打开")!
            .trigger("click")
        await flushPromises()
        expect(scaffold.emitted("connected")).toBeUndefined()
        expect(wrapper.get('nav button[aria-label="新建"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".header-actions").text()).toContain("Original.pbip")
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(ModelPanel).props("project")).toEqual(original)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed"])("当前模型 %s 时新建入口不允许替换连接", async (state) => {
    const transport = new MockTransport()
    const requests = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(ConnectionPanel).vm.$emit("connected", {
            ...structuredClone(previewProject),
            hasPendingChanges: state === "pending",
            saveResult: state === "saveFailed" ? { errorCode: "SAVE_FAILED" } : undefined,
        })
        await flushPromises()
        await createProject(wrapper)
        const desktop = wrapper.findAll("button").find((item) => item.text() === "Power BI Desktop 打开")!
        expect(desktop.attributes("disabled")).toBeDefined()
        await desktop.trigger("click")
        expect(requests.mock.calls.some(([message]) => (message as { method: string }).method === "scaffold.openDesktop")).toBe(false)
    } finally {
        wrapper.unmount()
    }
})
