/**
 * FilePath    : pbi-utils\web\tests\app\DiagnosticPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证运行环境自动检测、描述展示、失败反馈和离开页面时取消请求.
 */
import { afterEach, expect, it, vi } from "vitest"
import { markRaw } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import { BridgeClient, type Methods } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import DiagnosticPanel from "@/app/DiagnosticPanel.vue"

afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
})

/** 返回可控的宽度通知和帧队列, 验证真实调度边界而不依赖等待时间. */
function resizeHarness() {
    let notify: ResizeObserverCallback | undefined
    let frameId = 0
    const frames = new Map<number, FrameRequestCallback>()
    const requestFrame = vi.fn((callback: FrameRequestCallback) => {
        frames.set(++frameId, callback)
        return frameId
    })
    const cancelFrame = vi.fn((id: number) => frames.delete(id))
    const disconnect = vi.fn()
    const observe = vi.fn()
    vi.stubGlobal("requestAnimationFrame", requestFrame)
    vi.stubGlobal("cancelAnimationFrame", cancelFrame)
    vi.stubGlobal(
        "ResizeObserver",
        class {
            private environment = false
            /** 保存各观察者的独立回调, 浮动消息的尺寸监听不能覆盖环境卡片. */
            constructor(private callback: ResizeObserverCallback) {}
            /** 仅将环境卡片绑定到测试宽度通知, 保留其他组件的正常观察登记. */
            observe(element: Element) {
                observe(element)
                if (element.classList.contains("environment-content")) {
                    this.environment = true
                    notify = this.callback
                }
            }
            /** 记录环境卡片资源释放, 其他浮动组件自行管理其观察者. */
            disconnect() {
                if (this.environment) disconnect()
            }
        },
    )
    return {
        requestFrame,
        cancelFrame,
        disconnect,
        observe,
        resize: (width: number) => notify?.([{ contentRect: { width } } as ResizeObserverEntry], {} as ResizeObserver),
        runFrame: () => {
            const pending = [...frames.values()]
            frames.clear()
            for (const callback of pending) callback(0)
        },
        frames,
    }
}

/** 记录完整桥接请求, 用可控在途请求验证生命周期清理. */
class DiagnosticTransport extends MockTransport {
    methods: string[] = []
    fail = false
    failureMessage = "运行环境暂时无法读取"
    hold = false
    pending = ""
    diagnosticOverride?: Methods["doctor"]["result"]
    private observers = new Set<(event: MessageEvent) => void>()
    /** 记录监听器并保留模拟传输行为. */
    override addEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.observers.add(listener)
        super.addEventListener(type, listener)
    }
    /** 移除监听器. */
    override removeEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.observers.delete(listener)
        super.removeEventListener(type, listener)
    }
    /** 返回固定错误或取消确认, 不使用等待时间模拟竞争. */
    override postMessage(message: unknown) {
        const request = message as { id: string; method: string }
        this.methods.push(request.method)
        const reply = (data: object) => {
            for (const listener of this.observers) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...data } }))
        }
        if (request.method === "doctor" && this.diagnosticOverride) {
            reply({ id: request.id, result: this.diagnosticOverride })
            return
        }
        if (request.method === "doctor" && this.fail) {
            reply({ id: request.id, error: { code: "ENVIRONMENT_FAILED", message: this.failureMessage } })
            return
        }
        if (request.method === "doctor" && this.hold) {
            this.pending = request.id
            return
        }
        if (request.method === "request.cancel" && this.pending) {
            reply({ id: this.pending, error: { code: "CANCELLED", message: "已取消" } })
            reply({ id: request.id, result: { accepted: true } })
            this.pending = ""
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 测试结构化桌面桥接.
        super.postMessage(message)
    }
}
it("打开运行环境自动检测一次, 使用描述列表且不展示 Hello、Mock Agent 或 JSON", async () => {
    const transport = new DiagnosticTransport()
    const bridge = new BridgeClient(transport)
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    expect(wrapper.text()).not.toContain("正在读取运行环境")
    expect(wrapper.findComponent({ name: "ElSkeleton" }).exists()).toBe(true)
    await flushPromises()
    expect(transport.methods).toEqual(["doctor"])
    expect(wrapper.findAll(".el-descriptions__label")).toHaveLength(7)
    expect(wrapper.text()).toContain("WebView2")
    expect(wrapper.text()).toContain("浏览器模拟")
    expect(wrapper.text()).not.toContain("Hello")
    expect(wrapper.text()).not.toContain("Mock Agent")
    expect(wrapper.find("pre").exists()).toBe(false)
    expect(wrapper.find("button").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("业务日志")
    expect(wrapper.text()).not.toContain("模型保存")
    expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(3)
    wrapper.unmount()
    bridge.dispose()
})
it("运行环境按卡片实际宽度切换三、二、一列并在卸载后释放观察者", async () => {
    const resize = resizeHarness()
    const bridge = new BridgeClient(new DiagnosticTransport())
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(resize.observe).toHaveBeenCalledWith(wrapper.find(".environment-content").element)
    for (const [width, expected] of [
        [840, 3],
        [839, 2],
        [560, 2],
        [559, 1],
        [1200, 3],
    ]) {
        const previous = wrapper.findComponent({ name: "ElDescriptions" }).props("column")
        resize.resize(width!)
        // oxlint-disable-next-line no-await-in-loop -- 观察器通知交付阶段不得同步改变列数.
        await flushPromises()
        expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(previous)
        resize.runFrame()
        // oxlint-disable-next-line no-await-in-loop -- 列数变化必须按宽度顺序逐次验证.
        await flushPromises()
        expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(expected)
    }
    wrapper.unmount()
    expect(resize.disconnect).toHaveBeenCalledOnce()
    bridge.dispose()
})
it("同帧宽度通知合并为最新值, 卸载取消待执行帧并忽略迟到通知", async () => {
    const resize = resizeHarness()
    const bridge = new BridgeClient(new DiagnosticTransport())
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    resize.resize(650)
    resize.resize(410)
    resize.resize(640)
    expect(resize.requestFrame).toHaveBeenCalledOnce()
    await flushPromises()
    expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(3)
    resize.runFrame()
    await flushPromises()
    expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(2)
    resize.resize(1200)
    expect(resize.frames.size).toBe(1)
    const lateFrame = resize.requestFrame.mock.calls[1]![0]
    wrapper.unmount()
    expect(resize.cancelFrame).toHaveBeenCalledWith(2)
    expect(resize.frames.size).toBe(0)
    resize.resize(410)
    expect(resize.requestFrame).toHaveBeenCalledTimes(2)
    lateFrame(0)
    expect(resize.frames.size).toBe(0)
    bridge.dispose()
})
it("首次挂载按实际宽度同步测量, 不等待观察器帧才确定列数", async () => {
    const resize = resizeHarness()
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 650, 500))
    const bridge = new BridgeClient(new DiagnosticTransport())
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(wrapper.findComponent({ name: "ElDescriptions" }).props("column")).toBe(2)
    expect(resize.requestFrame).not.toHaveBeenCalled()
    wrapper.unmount()
    bridge.dispose()
})
it("英文诊断翻译标签与状态并保留宿主原始系统信息, 切换语言不重复检测", async () => {
    setLocale("en-us")
    const transport = new DiagnosticTransport()
    transport.diagnosticOverride = {
        diagnostic: {
            version: "1.2.3",
            environment: {
                os: "Microsoft Windows 11 10.0.26100",
                architecture: "X64",
                runtime: ".NET 10.0.1",
                desktop: "available-platform",
                powerBi: "read-only-discovery",
            },
        },
        webView2: "140.0.3485.81",
    }
    const bridge = new BridgeClient(transport)
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(wrapper.text()).toContain("Runtime environment")
    expect(wrapper.text()).toContain("Detection complete")
    expect(wrapper.text()).toContain("Read-only local instance discovery available")
    expect(wrapper.text()).toContain("Microsoft Windows 11 10.0.26100")
    expect(wrapper.text()).toContain(".NET 10.0.1")
    expect(wrapper.text()).toContain("140.0.3485.81")
    expect(wrapper.text()).not.toContain("运行环境")
    setLocale("zh-cn")
    await flushPromises()
    expect(wrapper.text()).toContain("运行环境")
    expect(transport.methods).toEqual(["doctor"])
    wrapper.unmount()
    bridge.dispose()
})
it("英文浏览器预览只展示模拟状态, 不把预览占位值当成本机环境", async () => {
    setLocale("en-us")
    const bridge = new BridgeClient(new DiagnosticTransport())
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(wrapper.text()).toContain("Browser simulation")
    expect(wrapper.text()).toContain("Not detected")
    expect(document.querySelector(".el-message")?.textContent).toContain("the local runtime environment has not been checked")
    expect(wrapper.text()).not.toContain("浏览器模拟")
    expect(wrapper.text()).not.toContain("Detection complete")
    wrapper.unmount()
    bridge.dispose()
})
it("环境检测失败明确展示错误, 不显示完成状态", async () => {
    const transport = new DiagnosticTransport()
    transport.fail = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toBe("运行环境暂时无法读取")
    expect(wrapper.find("dl").exists()).toBe(false)
    expect(wrapper.find(".environment-badge").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})
it("英文诊断通用桥接失败使用共享宿主提示翻译", async () => {
    setLocale("en-us")
    const transport = new DiagnosticTransport()
    transport.fail = true
    transport.failureMessage = "请求执行失败"
    const bridge = new BridgeClient(transport)
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toBe("The request failed.")
    expect(wrapper.find(".environment-badge").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})
it("离开关于时取消在途检测并接收终态", async () => {
    const transport = new DiagnosticTransport()
    transport.hold = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(DiagnosticPanel, { props: { bridge: markRaw(bridge) } })
    expect(transport.pending).not.toBe("")
    wrapper.unmount()
    await flushPromises()
    expect(transport.methods).toContain("request.cancel")
    expect(transport.pending).toBe("")
    bridge.dispose()
})
