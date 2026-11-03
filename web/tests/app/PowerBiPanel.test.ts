/**
 * FilePath    : pbi-utils\web\tests\app\PowerBiPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证实例展示、错误恢复、空态、刷新快照保留和取消请求.
 */

import { afterEach, expect, it, vi } from "vitest"
import { ElMessage } from "element-plus"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { BridgeClient, BridgeError, type PowerBiInstance, type PowerBiSnapshot, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import ModelInstanceCard from "@/app/ModelInstanceCard.vue"
import { setLocale } from "@/i18n"

afterEach(() => ElMessage.closeAll())

/** 空模型不发连接请求或自动通知; 用户查看原因才通过共享反馈显示双语指引. */
it.each(["zh-cn", "en-us"] as const)("%s 无本地模型实例禁用连接并给出 Service 指引", async (locale) => {
    setLocale(locale)
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "empty-local-model",
        promise: Promise.resolve({
            timestamp: new Date().toISOString(),
            instances: [
                {
                    processId: 1,
                    title: "远端报表",
                    port: 1234,
                    status: "unavailable",
                    models: [],
                    errorCode: "DESKTOP_NO_LOCAL_MODEL",
                    message:
                        "此 Desktop 实例未提供可读取的本地模型. 若报表仅实时连接 Power BI Service 语义模型, 请在 Power BI Service 中登录并选择原模型进行只读查看; 若模型仍在加载, 请加载完成后刷新",
                },
            ],
        }),
    })
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    expect(document.querySelector(".el-message")).toBeNull()
    await wrapper.get(".instance-reason-button").trigger("click")
    expect(document.querySelector(".el-message")?.textContent).toContain(locale === "zh-cn" ? "未提供可读取的本地模型" : "no readable local model")
    wrapper.getComponent(ModelInstanceCard).vm.$emit("action")
    await flushPromises()
    expect(request).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    bridge.dispose()
    setLocale("zh-cn")
})

/** 获取正文之外的真实操作提示, 取消按钮仍执行原桥接请求. */
function operationMessage() {
    return new DOMWrapper(document.querySelector<HTMLElement>(".operation-message .operation-feedback")!)
}
/** 独立定位同一浮动提示的取消入口, 不依赖正文内的动态按钮. */
function operationCancel() {
    return operationMessage().get(".operation-feedback__cancel")
}

/** 独立控制业务与取消请求的终态, 不使用计时延迟推测请求顺序. */
function pendingResult<T>() {
    let resolve!: (value: T) => void
    let reject!: (error: Error) => void
    const promise = new Promise<T>((accept, fail) => {
        resolve = accept
        reject = fail
    })
    return { promise, resolve, reject }
}

/** 首次后台扫描不弹在途消息, 仍阻止连接并保留真实错误与重试入口. */
it.each(["success", "failure"] as const)("首次自动扫描 %s 保持静默和业务锁", async (outcome) => {
    const bridge = new BridgeClient(new MockTransport())
    const initial = pendingResult<PowerBiSnapshot>()
    vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial-scan", promise: initial.promise })
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    expect(wrapper.get(".desktop-file-button").attributes("disabled")).toBeDefined()
    expect(document.querySelector(".operation-feedback")).toBeNull()
    if (outcome === "success") initial.resolve({ timestamp: new Date().toISOString(), instances: [] })
    else initial.reject(new Error("首次扫描失败"))
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeUndefined()
    if (outcome === "failure") expect(document.querySelector(".el-message--error")?.textContent).toContain("首次扫描失败")
    expect(document.querySelector(".operation-feedback")).toBeNull()
    wrapper.unmount()
    bridge.dispose()
})

/** 所有在途通道保留同一操作栏和按钮文案, 只在正文外更新单条可取消消息. */
it.each([
    { action: "scan", selector: ".instance-refresh-button", method: "powerbi.instances" },
    { action: "connect", selector: ".instance-connect-button", method: "project.connectLocal" },
] as const)("$action 使用浮动消息而不插入正文状态或替换工具栏按钮", async ({ selector, method }) => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    const buttons = wrapper.findAll(".desktop-instance-actions button")
    const nodes = buttons.map((button) => button.element)
    const labels = buttons.map((button) => button.text())
    const card = wrapper.get(".instance-card").element
    const pending = pendingResult<never>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "stable-pending", promise: pending.promise })
    await wrapper.get(selector).trigger("click")
    await flushPromises()
    expect(request.mock.calls.at(-1)?.[0]).toBe(method)
    expect(wrapper.findAll(".desktop-instance-actions button").map((button) => button.element)).toEqual(nodes)
    expect(wrapper.findAll(".desktop-instance-actions button").map((button) => button.text())).toEqual(labels)
    expect(wrapper.get(".instance-card").element).toBe(card)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain("正在")
    const message = operationMessage().element
    expect(wrapper.element.contains(message)).toBe(false)
    setLocale("en-us")
    await flushPromises()
    expect(operationMessage().element).toBe(message)
    expect(operationCancel().text()).toBe("Cancel")
    request.mockReturnValueOnce({ id: "cancel-stable", promise: Promise.resolve({ accepted: true }) })
    await operationCancel().trigger("click")
    await flushPromises()
    expect(operationMessage().element).toBe(message)
    expect(operationCancel().attributes("disabled")).toBeDefined()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    pending.reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(document.querySelector(".operation-message .operation-feedback")).toBeNull()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    wrapper.unmount()
    bridge.dispose()
})

/** 共享单行实例保留统计分区, 成功刷新为空时替换旧快照. */
it("单行实例保留模型统计与行首连接入口, 刷新能进入空态", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.text()).toContain("示例报表 (模拟数据)")
    expect(wrapper.get('[data-stat="compatibilityLevel"] dt').text()).toBe("兼容级别")
    expect(wrapper.get('[data-stat="compatibilityLevel"] dd').text()).toBe("1600")
    expect(wrapper.get('[data-stat="tableCount"]').text()).toContain("6")
    expect(wrapper.get('[data-stat="measureCount"]').text()).toContain("12")
    expect(wrapper.get('[data-stat="reportPageCount"] dd').text()).toBe("0")
    expect(wrapper.find(".instance-status").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("可连接 · 读写")
    expect(wrapper.find(".model-summary").exists()).toBe(false)
    expect(wrapper.find(".el-descriptions").exists()).toBe(false)
    expect(wrapper.get(".instance-heading").element.firstElementChild?.classList.contains("instance-connect-button")).toBe(true)
    vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "empty", promise: Promise.resolve({ timestamp: new Date().toISOString(), instances: [] }) })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(wrapper.text()).toContain("尚未发现 Power BI Desktop")
    expect(wrapper.text()).not.toContain("示例报表")
    wrapper.unmount()
    bridge.dispose()
})

/** 重试后仍保留等待状态与禁用边界, 外部实例标题按纯文本处理. */
/** 刷新、取消和失败都保留同一张卡片, 过期结果始终不可操作, 成功后按新结果替换. */
it.each(["success", "failure", "cancelled"] as const)("扫描 %s 期间保留旧卡片, 仅权威结果恢复操作", async (outcome) => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    const card = wrapper.get(".instance-card").element
    const scan = pendingResult<PowerBiSnapshot>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "refresh-pending", promise: scan.promise })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(wrapper.get(".instance-card").element).toBe(card)
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    if (outcome === "success") scan.resolve({ timestamp: new Date().toISOString(), instances: [] })
    else scan.reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "SCAN_FAILED", "本次扫描未完成"))
    await flushPromises()
    if (outcome === "success") expect(wrapper.find(".instance-card").exists()).toBe(false)
    else {
        expect(wrapper.get(".instance-card").element).toBe(card)
        expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
        expect(wrapper.text()).toContain("显示上次扫描结果")
        await wrapper.get(".instance-connect-button").trigger("click")
        expect(request).toHaveBeenCalledTimes(1)
        request.mockRestore()
        await wrapper.get(".instance-refresh-button").trigger("click")
        await flushPromises()
        expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeUndefined()
        expect(wrapper.text()).not.toContain("显示上次扫描结果")
    }
    wrapper.unmount()
    bridge.dispose()
})

it("首次读取失败后可以重试, 恶意标题不能执行 HTML", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "failed", promise: Promise.reject(new Error("发现失败")) })
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toBe("发现失败")
    request.mockReturnValueOnce({
        id: "recovered",
        promise: Promise.resolve({
            timestamp: new Date().toISOString(),
            instances: [
                { processId: 1, title: "<img src=x onerror=alert(1)>", port: null, status: "starting", message: "等待模型", errorCode: null, models: [] },
            ],
        }),
    })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(wrapper.find(".el-alert--error").exists()).toBe(false)
    expect(wrapper.text()).toContain("<img src=x onerror=alert(1)>")
    expect(wrapper.find(".instance-heading img").exists()).toBe(false)
    expect(wrapper.get(".instance-status").text()).toBe("等待模型")
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    wrapper.unmount()
    bridge.dispose()
})

it("读取中拒绝重复刷新并向宿主发送独立取消请求", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    let reject!: (error: Error) => void
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "pending",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeDefined()
    expect(wrapper.get(".desktop-file-button").attributes("disabled")).toBeDefined()
    await operationCancel().trigger("click")
    expect(request).toHaveBeenCalledWith("request.cancel", { requestId: "pending" })
    reject(new Error("操作已取消"))
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toContain("操作已取消")
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})

it("前次扫描取消请求迟到失败不能覆盖下一次扫描的错误或取消状态", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    const firstScan = pendingResult<PowerBiSnapshot>()
    const firstCancel = pendingResult<{ accepted: boolean }>()
    const secondScan = pendingResult<PowerBiSnapshot>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "scan-a", promise: firstScan.promise })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    request.mockReturnValueOnce({ id: "cancel-a", promise: firstCancel.promise })
    await operationCancel().trigger("click")
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "scan-a" })
    firstScan.resolve({ timestamp: "2026-10-14T00:00:00Z", instances: [] })
    await flushPromises()
    request.mockReturnValueOnce({ id: "scan-b", promise: secondScan.promise })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    firstCancel.reject(new Error("前次扫描取消失败"))
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    expect(wrapper.find(".el-alert--error").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("前次扫描取消失败")
    const cancel = operationCancel()
    expect(cancel.attributes("disabled")).toBeUndefined()
    request.mockReturnValueOnce({ id: "cancel-b", promise: Promise.resolve({ accepted: true }) })
    await cancel.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "scan-b" })
    expect(cancel.attributes("disabled")).toBeDefined()
    secondScan.resolve({ timestamp: "2026-10-14T00:00:01Z", instances: [] })
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    expect(wrapper.find(".el-alert--error").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

it.each([
    { action: "scan", label: "刷新实例", method: "powerbi.instances" },
    { action: "connect", label: "连接此实例", method: "project.connectLocal" },
] as const)("$action 取消确认只发送一次并继续等待业务原请求终态", async ({ action, label, method }) => {
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    const operation = pendingResult<never>()
    request.mockReturnValueOnce({ id: `pending-${action}`, promise: operation.promise })
    await wrapper
        .findAll("button")
        .find((button) => button.text() === label)!
        .trigger("click")
    await flushPromises()
    expect(request.mock.calls.at(-1)?.[0]).toBe(method)
    const cancel = operationCancel()
    request.mockReturnValueOnce({ id: `cancel-${action}`, promise: Promise.resolve({ accepted: true }) })
    await cancel.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: `pending-${action}` })
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    expect(wrapper.get(".desktop-file-button").attributes("disabled")).toBeDefined()
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeDefined()
    expect(cancel.attributes("disabled")).toBeDefined()
    await cancel.trigger("click")
    await flushPromises()
    expect(request.mock.calls.filter(([calledMethod]) => calledMethod === "request.cancel")).toHaveLength(1)
    expect(wrapper.emitted("connected")).toBeUndefined()
    operation.reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeUndefined()
    expect(document.querySelector(".el-message--error")?.textContent).toContain("操作已取消")
    wrapper.unmount()
    bridge.dispose()
})

it("当前请求取消失败保留实际错误和等待状态, 可对同一请求再次取消", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    const operation = pendingResult<PowerBiSnapshot>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "current-scan", promise: operation.promise })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    const cancel = operationCancel()
    request.mockReturnValueOnce({ id: "cancel-failed", promise: Promise.reject(new Error("当前扫描取消失败")) })
    await cancel.trigger("click")
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toBe("当前扫描取消失败")
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    expect(cancel.attributes("disabled")).toBeUndefined()
    request.mockReturnValueOnce({ id: "cancel-retry", promise: Promise.resolve({ accepted: true }) })
    await cancel.trigger("click")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "request.cancel")).toEqual([
        ["request.cancel", { requestId: "current-scan" }],
        ["request.cancel", { requestId: "current-scan" }],
    ])
    expect(cancel.attributes("disabled")).toBeDefined()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    operation.reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    expect(Array.from(document.querySelectorAll(".el-message--error")).at(-1)?.textContent).toContain("操作已取消")
    wrapper.unmount()
    bridge.dispose()
})

/** 两个同名进程仍由宿主实例身份区分, 名称、PID 和连接来源不能代替该身份. */
it.each(["zh-cn", "en-us"] as const)("%s 同名实例只标记精确当前身份, 查看模型不重连且切换后状态同步", async (locale) => {
    setLocale(locale)
    const bridge = new BridgeClient(new MockTransport())
    const instances: PowerBiInstance[] = [1000, 2000].map((processId) => ({
        processId,
        instanceId: `desktop-${processId}`,
        title: "同名报表",
        port: 50000 + processId,
        status: "connected",
        message: null,
        errorCode: null,
        models: [{ name: "模型", compatibilityLevel: 1600, tableCount: 1, measureCount: 1 }],
    }))
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "instances",
        promise: Promise.resolve({ timestamp: new Date().toISOString(), instances }),
    })
    const activeProject: ProjectSnapshot = { ...previewProject, connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "desktop-1000" } }
    const wrapper = mount(PowerBiPanel, { props: { bridge, activeProject } })
    await flushPromises()
    const cards = wrapper.findAll(".instance-card")
    const current = locale === "zh-cn" ? "查看模型" : "View model"
    const switchLabel = locale === "zh-cn" ? "切换到此实例" : "Switch to this instance"
    expect(cards[0]!.text()).toContain(locale === "zh-cn" ? "已连接" : "Connected")
    expect(cards[0]!.get("button").text()).toBe(current)
    expect(cards[0]!.get("button").attributes("disabled")).toBeUndefined()
    await cards[0]!.get("button").trigger("click")
    expect(request.mock.calls.filter(([method]) => method === "project.connectLocal")).toHaveLength(0)
    expect(wrapper.emitted("viewModel")).toEqual([[]])
    expect(cards[1]!.text()).toContain(locale === "zh-cn" ? "未连接" : "Not connected")
    expect(cards[1]!.get("button").text()).toBe(switchLabel)
    let resolve!: (project: ProjectSnapshot) => void
    request.mockReturnValueOnce({
        id: "switch",
        promise: new Promise((accept) => {
            resolve = accept
        }),
    })
    await cards[1]!.get("button").trigger("click")
    await cards[1]!.get("button").trigger("click")
    expect(request.mock.calls.filter(([method]) => method === "project.connectLocal")).toEqual([
        ["project.connectLocal", { processId: "2000", instanceId: "desktop-2000" }],
    ])
    expect(wrapper.get(".desktop-file-button").attributes("disabled")).toBeDefined()
    expect(wrapper.get(".instance-refresh-button").attributes("disabled")).toBeDefined()
    await cards[0]!.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toHaveLength(1)
    const switched: ProjectSnapshot = { ...activeProject, connection: { ...activeProject.connection!, instanceId: "desktop-2000" } }
    resolve(switched)
    await flushPromises()
    expect(wrapper.emitted("connected")?.[0]).toEqual([switched])
    await wrapper.setProps({ activeProject: switched })
    expect(cards[0]!.get("button").text()).toBe(switchLabel)
    expect(cards[1]!.get("button").text()).toBe(current)
    await wrapper.setProps({ activeProject: undefined })
    expect(cards.every((card) => card.text().includes(locale === "zh-cn" ? "未连接" : "Not connected"))).toBe(true)
    expect(cards.every((card) => card.get("button").attributes("disabled") === undefined)).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})

it.each([
    { kind: "ssas-xmla", instanceId: "mock-desktop-instance-1000" },
    { kind: "powerbi-service", instanceId: "mock-desktop-instance-1000" },
    { kind: "desktop", instanceId: null },
    { kind: "desktop", instanceId: "" },
] as const)("$kind / $instanceId 不凭名称或不完整身份误标当前实例", async (connection) => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, {
        props: { bridge, activeProject: { ...previewProject, connection: { ...connection, label: "示例报表 (模拟数据)" } } },
    })
    await flushPromises()
    const card = wrapper.get(".instance-card")
    expect(card.text()).toContain("未连接")
    expect(card.get("button").text()).toBe("切换到此实例")
    expect(card.get("button").attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})

it("缺少发现身份时保守不标已连接, 文件读取期间不允许新连接", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "legacy-snapshot",
        promise: Promise.resolve({
            timestamp: new Date().toISOString(),
            instances: [{ processId: 1000, title: "同名报表", port: 50000, status: "connected", message: null, errorCode: null, models: [] }],
        }),
    })
    const wrapper = mount(PowerBiPanel, {
        props: { bridge, activeProject: { ...previewProject, connection: { kind: "desktop", label: "同名报表", instanceId: "desktop-1000" } } },
    })
    await flushPromises()
    expect(wrapper.get(".instance-card").text()).toContain("未连接")
    await wrapper.setProps({ disabled: true, fileOpening: true })
    await wrapper.get(".desktop-file-button").trigger("click")
    await wrapper.get(".instance-refresh-button").trigger("click")
    await wrapper.get(".instance-card button").trigger("click")
    expect(wrapper.emitted("openFile")).toBeUndefined()
    expect(request).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    bridge.dispose()
})

/** 连接页唯一的 Desktop 操作栏保留文件与刷新入口. */
it("公共文件和刷新入口不产生说明卡或重复文件按钮", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.find(".scan-toolbar").exists()).toBe(false)
    expect(wrapper.findAll(".desktop-instance-actions")).toHaveLength(1)
    expect(wrapper.findAll(".desktop-file-button")).toHaveLength(1)
    expect(wrapper.get(".desktop-file-button img").attributes("src")).toBe("/icons/power-bi.svg")
    expect(wrapper.get(".desktop-instance-actions .instance-refresh-button").text()).toBe("刷新实例")
    await wrapper.get(".desktop-file-button").trigger("click")
    expect(wrapper.emitted("openFile")).toEqual([[]])
    expect(wrapper.findAll("button").some((button) => button.text() === "检查 UDF 能力")).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

/** 当前卡片采用已连接快照的权威统计, 其他扫描实例保持独立统计, 未知和真实零值分开. */
it.each(["zh-cn", "en-us"] as const)("%s 当前模型统计覆盖旧扫描, 未知不伪装成零且保存未完锁定查看", async (locale) => {
    setLocale(locale)
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "summary-scan",
        promise: Promise.resolve({
            timestamp: "2026-10-14T00:00:00Z",
            instances: [
                {
                    processId: 1000,
                    instanceId: "desktop-current",
                    title: "当前模型原文",
                    port: 50000,
                    status: "connected",
                    errorCode: null,
                    message: null,
                    models: [{ name: "过期扫描", compatibilityLevel: 1600, tableCount: 1, measureCount: 2 }],
                },
                {
                    processId: 2000,
                    instanceId: "desktop-other",
                    title: "其他模型原文",
                    port: 50001,
                    status: "connected",
                    errorCode: null,
                    message: null,
                    models: [
                        {
                            name: "其他模型",
                            compatibilityLevel: 1702,
                            tableCount: 0,
                            measureCount: 0,
                            summary: { compatibilityLevel: 1702, tableCount: 0, measureCount: null, reportPageCount: null },
                        },
                    ],
                },
            ],
        }),
    })
    const activeProject: ProjectSnapshot = {
        ...previewProject,
        connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "desktop-current" },
        summary: { compatibilityLevel: 1702, tableCount: 17, measureCount: 23, reportPageCount: 9 },
    }
    const wrapper = mount(PowerBiPanel, { props: { bridge, activeProject } })
    await flushPromises()
    const [current, other] = wrapper.findAll(".instance-card")
    expect(current!.get('[data-stat="compatibilityLevel"]').text()).toContain("1702")
    expect(current!.get('[data-stat="tableCount"]').text()).toContain("17")
    expect(current!.get('[data-stat="measureCount"]').text()).toContain("23")
    expect(current!.get('[data-stat="reportPageCount"]').text()).toContain("9")
    expect(other!.get('[data-stat="tableCount"] dd').text()).toBe("0")
    expect(other!.get('[data-stat="measureCount"] dd').text()).toBe("-")
    expect(other!.get('[data-stat="reportPageCount"] dd').text()).toBe("0")
    await wrapper.setProps({ disabled: true })
    await current!.get("button").trigger("click")
    await other!.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toBeUndefined()
    expect(wrapper.emitted("connected")).toBeUndefined()
    expect(request).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll(".instance-connect-button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})

/** 外部连接仍锁定时延后首次扫描, 只在第一次解锁补扫, 失败不会变成自动重试循环. */
it.each(["success", "failure"] as const)("禁用状态首次挂载延后扫描, %s 后再次解锁不重复扫描", async (outcome) => {
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(PowerBiPanel, { props: { bridge, disabled: true } })
    await flushPromises()
    expect(request).not.toHaveBeenCalled()
    if (outcome === "failure") request.mockReturnValueOnce({ id: "deferred-failure", promise: Promise.reject(new Error("延后扫描失败")) })
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    if (outcome === "failure") expect(document.querySelector(".el-message--error")?.textContent).toBe("延后扫描失败")
    else expect(wrapper.find(".instance-card").exists()).toBe(true)
    await wrapper.setProps({ disabled: true })
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})

/** 文件加载的固定阶段和取消确认均不代表完成; 成功标记随锁排空后才读取真实实例并合并同轮快照更新. */
it.each(["zh-cn", "en-us"] as const)("%s 文件连接成功后的刷新标记等待解锁, 同轮更新模型仅扫描一次", async (locale) => {
    setLocale(locale)
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(PowerBiPanel, { props: { bridge, refreshKey: "0:0" } })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    await wrapper.setProps({ disabled: true, fileOpening: true })
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("true")
    expect(wrapper.get(".desktop-file-button").classes()).toContain("is-loading")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    const project: ProjectSnapshot = {
        ...previewProject,
        connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "desktop-opened" },
    }
    await wrapper.setProps({ activeProject: project, refreshKey: "0:1", fileOpening: false })
    await wrapper.setProps({ activeProject: { ...project } })
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    request.mockReturnValueOnce({
        id: "post-file-scan",
        promise: Promise.resolve({
            timestamp: "2026-10-15T00:00:00Z",
            instances: [
                {
                    processId: 3000,
                    instanceId: "desktop-opened",
                    title: "刚打开的模型",
                    port: 50300,
                    status: "connected",
                    errorCode: null,
                    message: null,
                    models: [],
                },
            ],
        }),
    })
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(2)
    expect(wrapper.get(".instance-card").text()).toContain("刚打开的模型")
    expect(wrapper.get(".instance-card button").text()).toBe(locale === "zh-cn" ? "查看模型" : "View model")
    expect(wrapper.get(".desktop-file-button").classes()).not.toContain("is-loading")
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    await wrapper.setProps({ disabled: true })
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(2)
    wrapper.unmount()
    bridge.dispose()
})

/** 扫描在途期间收到多个已完成连接标记只补一轮; 补扫失败独立显示且不改写已连接模型或循环重试. */
it("在途实例扫描合并刷新标记, 补扫失败不撤销文件连接成功事实", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const initial = pendingResult<PowerBiSnapshot>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending-instances", promise: initial.promise })
    const activeProject: ProjectSnapshot = {
        ...previewProject,
        connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "desktop-confirmed" },
    }
    const wrapper = mount(PowerBiPanel, { props: { bridge, activeProject, refreshKey: 0 } })
    await wrapper.setProps({ refreshKey: 1 })
    await wrapper.setProps({ refreshKey: 2 })
    expect(request).toHaveBeenCalledTimes(1)
    request.mockReturnValueOnce({ id: "failed-refresh", promise: Promise.reject(new Error("实例扫描暂不可用")) })
    initial.resolve({ timestamp: "2026-10-15T00:00:00Z", instances: [] })
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(2)
    expect(document.querySelector(".el-message--error")?.textContent).toBe("实例扫描暂不可用")
    expect(wrapper.props("activeProject")).toEqual(activeProject)
    expect(wrapper.emitted("connected")).toBeUndefined()
    expect(wrapper.get(".powerbi-panel").attributes("aria-busy")).toBe("false")
    await wrapper.setProps({ disabled: true })
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(request).toHaveBeenCalledTimes(2)
    wrapper.unmount()
    bridge.dispose()
})

/** 多模型实例仍只显示一张实例卡, 未连接时拒绝歧义连接, 已绑定实例只查看宿主权威模型. */
it.each([
    { locale: "zh-cn", current: false },
    { locale: "en-us", current: false },
    { locale: "zh-cn", current: true },
    { locale: "en-us", current: true },
] as const)("$locale 多模型实例 current=$current 不复制卡片或混合统计, 操作遵循已绑定身份", async ({ locale, current }) => {
    setLocale(locale)
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "multiple-models",
        promise: Promise.resolve({
            timestamp: "2026-10-14T00:00:00Z",
            instances: [
                {
                    processId: 1000,
                    instanceId: "desktop-multiple",
                    title: "多模型实例原始标题",
                    port: 50000,
                    status: "connected",
                    errorCode: null,
                    message: null,
                    models: [
                        { name: "不可擅自绑定的模型甲", compatibilityLevel: 1600, tableCount: 101, measureCount: 102 },
                        { name: "不可擅自绑定的模型乙", compatibilityLevel: 1702, tableCount: 201, measureCount: 202 },
                    ],
                },
            ],
        }),
    })
    const activeProject: ProjectSnapshot | undefined = current
        ? {
              ...previewProject,
              connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "desktop-multiple" },
              summary: { compatibilityLevel: 1702, tableCount: 17, measureCount: 23, reportPageCount: 9 },
          }
        : undefined
    const wrapper = mount(PowerBiPanel, { props: { bridge, activeProject } })
    await flushPromises()
    expect(wrapper.findAll(".instance-card")).toHaveLength(1)
    const card = wrapper.get(".instance-card")
    const values = card.findAll(".instance-stat dd").map((value) => value.text())
    expect(values).toEqual(current ? ["1702", "17", "23", "9"] : ["-", "-", "-", "0"])
    expect(card.text()).not.toContain("不可擅自绑定的模型甲")
    expect(card.text()).not.toContain("不可擅自绑定的模型乙")
    expect(card.get("button").attributes("disabled") !== undefined).toBe(!current)
    // 直接发出组件动作, 验证处理器的歧义保护, 不仅依赖按钮 disabled 的浏览器行为.
    wrapper.getComponent(ModelInstanceCard).vm.$emit("action")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "project.connectLocal")).toHaveLength(0)
    expect(wrapper.emitted("viewModel")).toEqual(current ? [[]] : undefined)
    await wrapper.setProps({ disabled: true })
    wrapper.getComponent(ModelInstanceCard).vm.$emit("action")
    await flushPromises()
    expect(wrapper.emitted("viewModel")).toEqual(current ? [[]] : undefined)
    expect(request.mock.calls.filter(([method]) => method === "project.connectLocal")).toHaveLength(0)
    wrapper.unmount()
    bridge.dispose()
})
