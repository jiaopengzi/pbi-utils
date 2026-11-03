/**
 * FilePath    : pbi-utils\web\tests\app\LearningPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证学习浏览器的区域裁剪、弹窗避让、固定打开、显式重试与卸载迟到保护.
 */
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { markRaw } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import LearningPanel from "@/app/LearningPanel.vue"
import { BridgeClient, type Transport } from "@/bridge/client"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
let frames: Map<number, FrameRequestCallback>
beforeEach(() => {
    frames = new Map()
    let next = 0
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
        frames.set(++next, callback)
        return next
    })
    vi.stubGlobal("cancelAnimationFrame", (id: number) => frames.delete(id))
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
        if (this.classList.contains("learning-viewport")) return new DOMRect(280, 50, 700, 720)
        if (this.classList.contains("content")) return new DOMRect(260, 40, 760, 750)
        if (this.classList.contains("workspace-header")) return new DOMRect(260, 0, 760, 120)
        if (this.classList.contains("el-overlay")) return new DOMRect(0, 0, 1024, 768)
        return new DOMRect()
    })
    vi.mocked(showMessage).mockClear()
})
afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
})

/** 只实现当前学习协议的真实客户端传输替身, 参数和响应仍经过 BridgeClient. */
class LearningTransport implements Transport {
    requests: { id: string; method: string; params: Record<string, unknown> }[] = []
    listeners = new Set<(event: MessageEvent) => void>()
    failLayout = false
    holdOpen = false
    addEventListener(_type: "message", listener: (event: MessageEvent) => void) {
        this.listeners.add(listener)
    }
    removeEventListener(_type: "message", listener: (event: MessageEvent) => void) {
        this.listeners.delete(listener)
    }
    postMessage(value: unknown) {
        const request = value as (typeof this.requests)[number]
        this.requests.push(request)
        if (request.method === "learning.open" && this.holdOpen) return
        this.reply(request.id, request.method === "learning.update" && this.failLayout)
    }
    reply(id: string, fail = false) {
        const data = fail
            ? { protocolVersion: 1, id, error: { code: "OPEN_FAILED", message: "failed" } }
            : { protocolVersion: 1, id, result: { embedded: true, opened: true, closed: true, isSimulation: false } }
        for (const listener of this.listeners) listener(new MessageEvent("message", { data }))
    }
}

/** 显式推进布局合并帧, 不使用真实定时等待或把 jsdom 几何当作实际窗口证据. */
async function layoutFrame(): Promise<void> {
    await flushPromises()
    const pending = [...frames.values()]
    frames.clear()
    pending.forEach((callback) => callback(0))
    await flushPromises()
}

it("原生区域避让顶栏并裁剪到内容与视口, 模态框显示时隐藏, 关闭后恢复, 卸载时释放", async () => {
    document.body.innerHTML = '<header class="workspace-header"></header><section class="content"></section>'
    const transport = new LearningTransport()
    const bridge = markRaw(new BridgeClient(transport))
    const wrapper = mount(LearningPanel, { props: { bridge }, attachTo: document.querySelector(".content")! })
    await layoutFrame()
    expect(transport.requests.at(-1)).toMatchObject({
        method: "learning.update",
        params: { visible: true, left: 280, top: 120, width: 700, height: 648 },
    })
    const overlay = document.createElement("div")
    overlay.className = "el-overlay"
    document.body.append(overlay)
    await layoutFrame()
    expect(transport.requests.at(-1)).toMatchObject({ method: "learning.update", params: { visible: false } })
    overlay.remove()
    await layoutFrame()
    expect(transport.requests.at(-1)).toMatchObject({ method: "learning.update", params: { visible: true } })
    wrapper.unmount()
    expect(transport.requests.at(-1)).toMatchObject({ method: "learning.close", params: {} })
    bridge.dispose()
})

it("浏览器打开仅传空参数, 操作锁阻止重复打开, 卸载后的失败不显示通知", async () => {
    const transport = new LearningTransport()
    transport.holdOpen = true
    const bridge = markRaw(new BridgeClient(transport))
    const wrapper = mount(LearningPanel, { props: { bridge } })
    await wrapper.get("button").trigger("click")
    const request = transport.requests.find((item) => item.method === "learning.open")!
    expect(request.params).toEqual({})
    expect(wrapper.emitted("busy")).toEqual([[true]])
    await wrapper.get("button").trigger("click")
    expect(transport.requests.filter((item) => item.method === "learning.open")).toHaveLength(1)
    wrapper.unmount()
    transport.reply(request.id, true)
    await flushPromises()
    expect(showMessage).not.toHaveBeenCalled()
    bridge.dispose()
})

it("初始化失败不会因DOM变化循环重试, 用户重试才重新申请原生视图", async () => {
    const transport = new LearningTransport()
    transport.failLayout = true
    const bridge = markRaw(new BridgeClient(transport))
    const wrapper = mount(LearningPanel, { props: { bridge }, attachTo: document.body })
    await layoutFrame()
    expect(showMessage).toHaveBeenCalledTimes(1)
    await layoutFrame()
    expect(transport.requests.filter((item) => item.method === "learning.update")).toHaveLength(1)
    transport.failLayout = false
    await wrapper.get(".learning-placeholder button").trigger("click")
    await layoutFrame()
    expect(transport.requests.filter((item) => item.method === "learning.update")).toHaveLength(2)
    expect(wrapper.find(".learning-placeholder button").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

it("学习页只展示无图标的浏览器打开按钮和学习区域", () => {
    const bridge = markRaw(new BridgeClient(new LearningTransport()))
    const wrapper = mount(LearningPanel, { props: { bridge } })
    expect(wrapper.findAll("button")).toHaveLength(1)
    expect(wrapper.get("button").text()).toBe("使用默认浏览器打开")
    expect(wrapper.find("button svg").exists()).toBe(false)
    expect(wrapper.find(".panel").exists()).toBe(false)
    expect(wrapper.find("h2").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("jiaopengzi.com")
    expect(wrapper.find(".learning-viewport").exists()).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})
