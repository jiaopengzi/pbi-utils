/**
 * FilePath    : pbi-utils\web\tests\app\McpConnectionsPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证多客户端展示、权威排空、旧快照禁用和卸载隔离.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient, type McpConnectionsSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import { showMessage } from "@/ui/message"
import McpConnectionsPanel from "@/app/McpConnectionsPanel.vue"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))

/** 构造两个不同权限、相同自报名称的真实身份快照. */
function snapshot(): McpConnectionsSnapshot {
    return {
        acceptingConnections: true,
        connections: [1, 2].map((id) => ({
            id: String(id),
            processId: 100 + id,
            processStartedAt: "2026-11-02T01:00:00Z",
            connectedAt: "2026-11-02T02:00:00Z",
            clientName: null,
            clientVersion: null,
            allowWrite: id === 1,
            state: "connected",
            activeRequests: id,
        })),
    }
}

it("多客户端稳定显示, 断开受理后保持排空卡片直至权威列表删除", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const value = snapshot()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(value) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await flushPromises()
        const first = wrapper.get('[data-connection-id="1"]').element
        expect(wrapper.findAll("li")).toHaveLength(2)
        expect(wrapper.text()).toContain("- · -")
        expect(wrapper.text()).toContain("只读")
        request.mockReturnValueOnce({ id: "disconnect", promise: Promise.resolve({ accepted: true }) }).mockReturnValueOnce({
            id: "draining",
            promise: Promise.resolve({ ...value, connections: value.connections.map((item) => (item.id === "1" ? { ...item, state: "draining" } : item)) }),
        })
        await wrapper.get('[data-connection-id="1"] button').trigger("click")
        await flushPromises()
        expect(request).toHaveBeenCalledWith("mcp.disconnect", { connectionId: "1" })
        expect(wrapper.get('[data-connection-id="1"]').element).toBe(first)
        expect(wrapper.text()).toContain("正在排空")
        expect(wrapper.get('[data-connection-id="1"] button').attributes("disabled")).toBeDefined()
        request.mockReturnValueOnce({ id: "exited", promise: Promise.resolve({ ...value, connections: [value.connections[1]!] }) })
        await wrapper.get(".connections-toolbar button").trigger("click")
        await flushPromises()
        expect(wrapper.find('[data-connection-id="1"]').exists()).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("刷新失败保留旧卡片并禁用管理, 成功后恢复并可暂停或恢复接入", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const value = snapshot()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(value) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await flushPromises()
        request.mockReturnValueOnce({ id: "failed", promise: Promise.reject(new Error("读取失败")) })
        await wrapper.get(".connections-toolbar button").trigger("click")
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(2)
        expect(wrapper.get('[data-connection-id="1"] button').attributes("disabled")).toBeDefined()
        expect(wrapper.text()).toContain("尚未核验")
        request.mockReturnValueOnce({ id: "recover", promise: Promise.resolve(value) })
        await wrapper.get(".connections-toolbar button").trigger("click")
        await flushPromises()
        request
            .mockReturnValueOnce({ id: "pause", promise: Promise.resolve({ ...value, acceptingConnections: false }) })
            .mockReturnValueOnce({ id: "paused", promise: Promise.resolve({ ...value, acceptingConnections: false }) })
        await wrapper.findAll(".connections-toolbar button")[1]!.trigger("click")
        await flushPromises()
        expect(request).toHaveBeenCalledWith("mcp.setAdmission", { acceptingConnections: false })
        expect(wrapper.text()).toContain("恢复接入")
        request.mockReturnValueOnce({ id: "resume", promise: Promise.resolve(value) }).mockReturnValueOnce({ id: "resumed", promise: Promise.resolve(value) })
        await wrapper.findAll(".connections-toolbar button")[1]!.trigger("click")
        await flushPromises()
        expect(request).toHaveBeenCalledWith("mcp.setAdmission", { acceptingConnections: true })
        expect(wrapper.findAll("li")).toHaveLength(2)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("卸载取消在途刷新并忽略迟到结果", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    let finish: ((value: McpConnectionsSnapshot) => void) | undefined
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending", promise: new Promise((resolve) => (finish = resolve)) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    wrapper.unmount()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "pending" })
    finish?.(snapshot())
    await flushPromises()
    expect(wrapper.emitted("busy")).toBeUndefined()
    bridge.dispose()
})

it("exiting 禁用断开, 切语言保持连接身份并显示对应状态; 过期断开只刷新", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const value = snapshot()
    value.connections[1]!.state = "exiting"
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(value) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await flushPromises()
        const node = wrapper.get('[data-connection-id="2"]').element
        expect(wrapper.get('[data-connection-id="2"] button').attributes("disabled")).toBeDefined()
        expect(wrapper.text()).toContain("等待进程退出")
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get('[data-connection-id="2"]').element).toBe(node)
        expect(wrapper.text()).toContain("Waiting for process exit")
        expect(wrapper.text()).toContain("Allow changes")
        expect(wrapper.text()).toContain("Read-only")
        request
            .mockReturnValueOnce({ id: "gone", promise: Promise.resolve({ accepted: false }) })
            .mockReturnValueOnce({ id: "refresh", promise: Promise.resolve({ ...value, connections: [value.connections[1]!] }) })
        await wrapper.get('[data-connection-id="1"] button').trigger("click")
        await flushPromises()
        expect(wrapper.find('[data-connection-id="1"]').exists()).toBe(false)
        expect(showMessage).toHaveBeenCalledWith("The connection has left or expired. Refreshing the list.", "info")
    } finally {
        wrapper.unmount()
        bridge.dispose()
        setLocale("zh-cn")
    }
})

it("管理失败及后续刷新失败保留旧快照, 成功刷新前不再受理管理", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(snapshot()) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await flushPromises()
        request
            .mockReturnValueOnce({ id: "failed-management", promise: Promise.reject(new Error("管理失败")) })
            .mockReturnValueOnce({ id: "failed-refresh", promise: Promise.reject(new Error("读取失败")) })
        await wrapper.get('[data-connection-id="1"] button').trigger("click")
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(2)
        expect(wrapper.get('[data-connection-id="1"] button').attributes("disabled")).toBeDefined()
        expect(wrapper.findAll(".connections-toolbar button")[1]!.attributes("disabled")).toBeDefined()
        expect(showMessage).toHaveBeenCalledWith("管理失败", "error")
        const count = request.mock.calls.length
        await wrapper.get('[data-connection-id="1"] button').trigger("click")
        expect(request).toHaveBeenCalledTimes(count)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("慢刷新不重叠, 管理期间不轮询, 隐藏暂停且卸载停止调度", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] })
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    let finish: ((value: McpConnectionsSnapshot) => void) | undefined
    let disconnect: ((value: { accepted: boolean }) => void) | undefined
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "slow", promise: new Promise((resolve) => (finish = resolve)) })
    const visibility = vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible")
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await vi.advanceTimersByTimeAsync(6000)
        expect(request).toHaveBeenCalledTimes(1)
        finish?.(snapshot())
        await flushPromises()
        request.mockReturnValueOnce({ id: "disconnect", promise: new Promise((resolve) => (disconnect = resolve)) })
        await wrapper.get('[data-connection-id="1"] button').trigger("click")
        await vi.advanceTimersByTimeAsync(6000)
        expect(request).toHaveBeenCalledTimes(2)
        request.mockReturnValueOnce({ id: "after-management", promise: Promise.resolve(snapshot()) })
        disconnect?.({ accepted: true })
        await flushPromises()
        expect(request).toHaveBeenCalledTimes(3)
        visibility.mockReturnValue("hidden")
        await vi.advanceTimersByTimeAsync(6000)
        expect(request).toHaveBeenCalledTimes(3)
        wrapper.unmount()
        await vi.advanceTimersByTimeAsync(6000)
        expect(request).toHaveBeenCalledTimes(3)
    } finally {
        if (wrapper.exists()) wrapper.unmount()
        bridge.dispose()
        visibility.mockRestore()
        vi.useRealTimers()
    }
})

it("管理请求卸载取消, 迟到受理不回读也不通知", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    let finish: ((value: { accepted: boolean }) => void) | undefined
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(snapshot()) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    await flushPromises()
    request.mockReturnValueOnce({ id: "pending-disconnect", promise: new Promise((resolve) => (finish = resolve)) })
    await wrapper.get('[data-connection-id="1"] button').trigger("click")
    wrapper.unmount()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "pending-disconnect" })
    const calls = request.mock.calls.length
    vi.mocked(showMessage).mockClear()
    finish?.({ accepted: true })
    await flushPromises()
    expect(request).toHaveBeenCalledTimes(calls)
    expect(showMessage).not.toHaveBeenCalled()
    bridge.dispose()
})

it("时间按当前语言显示本地日期时间并保留机器值, 无效时间显示未知值", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const value = snapshot()
    value.connections[0]!.connectedAt = "invalid-date"
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(value) })
    const wrapper = mount(McpConnectionsPanel, { props: { bridge } })
    try {
        await flushPromises()
        const times = wrapper.findAll('[data-connection-id="1"] time')
        expect(times[0]!.attributes("datetime")).toBe(value.connections[0]!.processStartedAt)
        expect(times[0]!.text()).toBe(
            new Intl.DateTimeFormat("zh-cn", { dateStyle: "medium", timeStyle: "medium" }).format(new Date(value.connections[0]!.processStartedAt)),
        )
        expect(times[1]!.text()).toBe("-")
        expect(times[1]!.attributes("datetime")).toBeUndefined()
        setLocale("en-us")
        await flushPromises()
        expect(times[0]!.text()).toBe(
            new Intl.DateTimeFormat("en-us", { dateStyle: "medium", timeStyle: "medium" }).format(new Date(value.connections[0]!.processStartedAt)),
        )
        expect(times[1]!.text()).toBe("-")
        expect(request).toHaveBeenCalledTimes(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        setLocale("zh-cn")
    }
})
