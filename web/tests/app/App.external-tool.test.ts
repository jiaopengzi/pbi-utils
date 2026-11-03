/**
 * FilePath    : pbi-utils\web\tests\app\App.external-tool.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证外部工具启动连接的一次消费、初始化锁、取消排空及失败后的手动连接.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { ElMessage } from "element-plus"
import App from "@/app/App.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import { BridgeClient, type ProjectSnapshot, type Transport } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"

type Request = { id: string; method: string; params: Record<string, unknown>; connectionRevision?: number }

/** 仅在测试中绑定启动目标并控制宿主终态, 其他业务使用已有显式模拟传输. */
class LaunchTransport implements Transport {
    readonly requests: Request[] = []
    private readonly listeners = new Set<(event: MessageEvent) => void>()
    private readonly mock = new MockTransport()
    private pending?: Request
    private settings?: Request
    /** 控制启动标志、语言与偏好返回时机; 目标地址不经网页传输. */
    constructor(
        private readonly hasTarget?: boolean,
        private readonly locale: "zh-cn" | "en-us" = "zh-cn",
        private readonly holdSettings = false,
    ) {}
    /** 订阅同一真实 BridgeClient 的回执与模拟业务回执. */
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.add(listener)
        this.mock.addEventListener(type, listener)
    }
    /** 卸载同时移除两条测试回执来源, 迟到消息不能更新已销毁页面. */
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.delete(listener)
        this.mock.removeEventListener(type, listener)
    }
    /** 捕获启动请求及取消, 普通业务转发原模拟实现, 不连接真实模型. */
    postMessage(message: unknown): void {
        const request = message as Request
        this.requests.push(request)
        if (request.method === "app.info") {
            this.reply({ id: request.id, result: { version: "test", webView2: "test", hasExternalToolTarget: this.hasTarget } })
        } else if (request.method === "ui.settings") {
            this.settings = request
            if (!this.holdSettings) this.completeSettings()
        } else if (request.method === "project.connectExternalTool") {
            this.pending = request
        } else if (request.method === "request.cancel" && request.params.requestId === this.pending?.id) {
            this.reply({ id: request.id, result: { accepted: true } })
        } else {
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            this.mock.postMessage(message)
        }
    }
    /** 发布偏好结果, 允许测试确认语言就绪之前不会开始模型连接. */
    completeSettings(): void {
        if (!this.settings) throw new Error("No settings request")
        this.reply({ id: this.settings.id, result: { locale: this.locale, showRecentFiles: true, daxFormatStyle: "long" } })
        this.settings = undefined
    }
    /** 成功包含权威代际变更; 后续模型请求必须绑定新代际. */
    complete(snapshot: ProjectSnapshot): void {
        if (!this.pending) throw new Error("No launch request")
        const { id } = this.pending
        this.reply({ workspaceChanged: { revision: 1, requestId: id, project: snapshot } })
        this.reply({ id, result: snapshot })
        this.pending = undefined
    }
    /** 只返回原连接终态, 取消确认本身不会调用此方法. */
    fail(code: string, message: string): void {
        if (!this.pending) throw new Error("No launch request")
        this.reply({ id: this.pending.id, error: { code, message } })
        this.pending = undefined
    }
    /** 把有限测试信封广播给仍订阅的客户端. */
    private reply(value: Record<string, unknown>): void {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    }
}

afterEach(() => {
    ElMessage.closeAll()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
})

it.each([undefined, false])("普通启动目标标记为 %s 时不发起外部工具连接", async (hasTarget) => {
    const transport = new LaunchTransport(hasTarget)
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        expect(transport.requests.some(({ method }) => method === "project.connectExternalTool")).toBe(false)
        expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
    } finally {
        wrapper.unmount()
    }
})

it("等待语言偏好后只消费一次启动目标, 成功采用新代际并进入模型", async () => {
    const transport = new LaunchTransport(true, "en-us", true)
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        expect(transport.requests.some(({ method }) => method === "project.connectExternalTool")).toBe(false)
        transport.completeSettings()
        await flushPromises()
        const launches = transport.requests.filter(({ method }) => method === "project.connectExternalTool")
        expect(launches).toHaveLength(1)
        expect(launches[0]).toMatchObject({ params: {}, connectionRevision: 0 })
        expect(document.querySelector(".operation-feedback")?.textContent).toContain("Connecting to the report model")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
        expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
        const snapshot: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://launch-test",
            connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "launch-instance" },
        }
        transport.complete(snapshot)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="Model"]').attributes("aria-current")).toBe("page")
        expect(wrapper.getComponent(ModelPanel).props("project")).toEqual(snapshot)
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(document.querySelector(".operation-feedback")).toBeNull()
        const bridge = wrapper.getComponent(ModelPanel).props("bridge") as BridgeClient
        await bridge.request("project.inspect", {}).promise
        expect(transport.requests.findLast(({ method }) => method === "project.inspect")?.connectionRevision).toBe(1)
        expect(transport.requests.filter(({ method }) => method === "project.connectExternalTool")).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it.each([
    ["EXTERNAL_TOOL_TARGET_UNAVAILABLE", "外部工具启动目标已失效, 请从 Power BI Desktop 重新打开", "The external tool launch target is no longer available."],
    [
        "EXTERNAL_TOOL_DATABASE_MISMATCH",
        "外部工具指定的数据库与当前 Desktop 模型不匹配, 请从 Power BI Desktop 重新打开",
        "The external tool database does not match the current Desktop model.",
    ],
])("启动 %s 失败保留连接页与可用桥接, 用户可手动连接", async (code, message, translated) => {
    const transport = new LaunchTransport(true, "en-us")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        transport.fail(code!, message!)
        await flushPromises()
        expect(document.querySelector(".el-message--error")?.textContent).toContain(translated)
        expect(wrapper.get('nav button[aria-label="Connections"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(wrapper.getComponent(ConnectionPanel).props("bridge")).toBeDefined()
        await wrapper.get(".instance-connect-button").trigger("click")
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="Model"]').attributes("aria-current")).toBe("page")
        expect(transport.requests.filter(({ method }) => method === "project.connectExternalTool")).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it("启动连接取消确认后仍等待原终态, 然后解除初始化锁且不自动重试", async () => {
    const transport = new LaunchTransport(true)
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        const button = new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-feedback button")!)
        await button.trigger("click")
        await flushPromises()
        expect(transport.requests.filter(({ method }) => method === "request.cancel")).toHaveLength(1)
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
        expect(button.attributes("disabled")).toBeDefined()
        transport.fail("CANCELLED", "操作已取消")
        await flushPromises()
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
        expect(document.querySelector(".operation-feedback")).toBeNull()
        expect(transport.requests.filter(({ method }) => method === "project.connectExternalTool")).toHaveLength(1)
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
    } finally {
        wrapper.unmount()
    }
})

it("显式浏览器模拟没有启动目标时不能伪造自动连接成功", async () => {
    const bridge = new BridgeClient(new MockTransport())
    try {
        await expect(bridge.request("app.info", {}).promise).resolves.toMatchObject({ hasExternalToolTarget: false })
        await expect(bridge.request("project.connectExternalTool", {}).promise).rejects.toMatchObject({ code: "EXTERNAL_TOOL_TARGET_UNAVAILABLE" })
    } finally {
        bridge.dispose()
    }
})

it("启动连接尚未完成时卸载只取消原请求, 迟到成功不重新显示模型或提示", async () => {
    const transport = new LaunchTransport(true)
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    wrapper.unmount()
    await flushPromises()
    expect(transport.requests.filter(({ method }) => method === "request.cancel")).toHaveLength(1)
    transport.complete(structuredClone(previewProject))
    await flushPromises()
    expect(document.querySelector(".operation-feedback")).toBeNull()
    expect(document.querySelector(".el-message")).toBeNull()
    expect(transport.requests.filter(({ method }) => method === "project.connectExternalTool")).toHaveLength(1)
})
