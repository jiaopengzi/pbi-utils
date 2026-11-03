/**
 * FilePath    : pbi-utils\web\tests\app\LoggingPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证日志设置保存、失败恢复与浏览器目录模拟反馈.
 */
import { beforeEach, expect, it, vi } from "vitest"
import { markRaw } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import { BridgeClient, type Methods } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import LoggingPanel from "@/app/LoggingPanel.vue"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
beforeEach(() => {
    vi.mocked(showMessage).mockClear()
})

class LoggingTransport extends MockTransport {
    requests: { id: string; method: string; params: object }[] = []
    failSave = false
    private observers = new Set<(event: MessageEvent) => void>()
    override addEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.observers.add(listener)
        super.addEventListener(type, listener)
    }
    override removeEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.observers.delete(listener)
        super.removeEventListener(type, listener)
    }
    override postMessage(message: unknown) {
        const request = message as { id: string; method: string; params: object }
        this.requests.push(request)
        if (request.method === "logging.save" && this.failSave) {
            for (const listener of this.observers)
                listener(
                    new MessageEvent("message", {
                        data: { protocolVersion: 1, id: request.id, error: { code: "SAVE_FAILED", message: "保存失败，原级别保留" } },
                    }),
                )
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 测试结构化桌面桥接.
        super.postMessage(message)
    }
}
it("读取默认级别、保存选择及打开目录只传固定方法", async () => {
    const transport = new LoggingTransport()
    const bridge = new BridgeClient(transport)
    const wrapper = mount(LoggingPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    const select = wrapper.findComponent({ name: "ElSelect" })
    expect(select.props("modelValue")).toBe("Information")
    select.vm.$emit("update:modelValue", "Debug")
    select.vm.$emit("change", "Debug")
    await flushPromises()
    expect(transport.requests.at(-1)).toMatchObject({ method: "logging.save", params: { level: "Debug" } })
    expect(showMessage).toHaveBeenLastCalledWith(expect.stringContaining("模拟级别已更新"), "info")
    expect(wrapper.text()).not.toContain("模拟级别已更新")
    await wrapper.get("button").trigger("click")
    await flushPromises()
    expect(transport.requests.at(-1)).toMatchObject({ method: "logging.openDirectory", params: {} })
    expect(showMessage).toHaveBeenLastCalledWith(expect.stringContaining("未打开本机目录"), "info")
    wrapper.unmount()
    bridge.dispose()
})
it("保存失败恢复已生效级别并显示错误", async () => {
    const transport = new LoggingTransport()
    transport.failSave = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(LoggingPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    const select = wrapper.findComponent({ name: "ElSelect" })
    select.vm.$emit("update:modelValue", "Error")
    select.vm.$emit("change", "Error")
    await flushPromises()
    expect(select.props("modelValue")).toBe("Information")
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("原级别保留"), "error")
    wrapper.unmount()
    bridge.dispose()
})

it("读取和保存期间保留相同表单节点, 临时提示不插入正文", async () => {
    const bridge = markRaw(new BridgeClient(new LoggingTransport()))
    const request = vi.spyOn(bridge, "request")
    let finish!: (value: Methods["logging.settings"]["result"]) => void
    request.mockReturnValueOnce({ id: "held-load", promise: new Promise((resolve) => (finish = resolve)) })
    const wrapper = mount(LoggingPanel, { props: { bridge } })
    const formNode = wrapper.get("form").element
    const select = wrapper.findComponent({ name: "ElSelect" })
    const selectNode = select.element
    const directoryNode = wrapper.get(".log-directory").element
    expect(select.props("disabled")).toBe(true)
    expect(wrapper.text()).not.toContain("正在读取")
    finish({ level: "Information", directory: "fixture", warning: null, isSimulation: false })
    await flushPromises()
    expect(wrapper.get("form").element).toBe(formNode)
    expect(select.element).toBe(selectNode)
    request.mockReturnValueOnce({ id: "held-save", promise: new Promise((resolve) => (finish = resolve)) })
    select.vm.$emit("update:modelValue", "Debug")
    select.vm.$emit("change", "Debug")
    await flushPromises()
    expect(select.props("disabled")).toBe(true)
    expect(wrapper.get("form").element).toBe(formNode)
    expect(wrapper.get(".log-directory").element).toBe(directoryNode)
    expect(wrapper.text()).not.toContain("正在保存")
    finish({ level: "Debug", directory: "fixture", warning: null, isSimulation: false })
    await flushPromises()
    expect(select.props("disabled")).toBe(false)
    expect(select.props("modelValue")).toBe("Debug")
    expect(wrapper.get("form").element).toBe(formNode)
    wrapper.unmount()
    bridge.dispose()
})
