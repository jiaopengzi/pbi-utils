/**
 * FilePath    : pbi-utils\web\tests\app\ConnectionPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接中心登录、目录选择、错误、取消及退出的类型化请求.
 */
import { afterEach, expect, it, vi } from "vitest"
import { ElMessage } from "element-plus"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { defineComponent, h, markRaw, ref } from "vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import App from "@/app/App.vue"
import { BridgeClient, BridgeError, type Methods, type ProjectSnapshot, type RecentPowerBiServiceModel } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale, type AppLocale } from "@/i18n"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import { expectConsoleWarnings } from "../support/testDiagnostics"

afterEach(() => ElMessage.closeAll())

/** 获取正文之外的真实操作提示, 取消按钮仍执行原桥接请求. */
function operationMessage() {
    return new DOMWrapper(document.querySelector<HTMLElement>(".operation-message .operation-feedback")!)
}
/** 独立定位同一浮动提示的取消入口, 不依赖正文内的动态按钮. */
function operationCancel() {
    return operationMessage().get(".operation-feedback__cancel")
}

/** 控制后台核验终态, 验证页签切换的消息和操作锁不依赖请求快慢. */
function pendingResult<T>() {
    let resolve!: (value: T) => void
    let reject!: (error: Error) => void
    const promise = new Promise<T>((accept, fail) => {
        resolve = accept
        reject = fail
    })
    return { promise, resolve, reject }
}

it("同一应用异步重挂 Service 不重放相同存储警告, 新警告仍提示", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const visible = ref(true)
    const activeProject: ProjectSnapshot = {
        ...structuredClone(previewProject),
        connection: { kind: "powerbi-service", label: "Power BI Service" },
    }
    /** 每轮状态和历史分别等待宿主, 初始未加载不能清除上一轮通知基线. */
    function prepareRead() {
        const status = pendingResult<Methods["powerbi.serviceStatus"]["result"]>()
        const recent = pendingResult<Methods["powerbi.serviceRecentModels"]["result"]>()
        request.mockReturnValueOnce({ id: "service-status", promise: status.promise })
        request.mockReturnValueOnce({ id: "service-recent", promise: recent.promise })
        request.mockReturnValueOnce({ id: "service-models", promise: Promise.resolve([]) })
        return { status, recent }
    }
    /** 发布完整读取终态, 保留真实组件的状态、目录与 busy 排空顺序. */
    async function complete(read: ReturnType<typeof prepareRead>, suffix?: string) {
        read.status.resolve({
            account: { username: "preview@example.com", environmentName: "GlobalCloud", environmentLabel: "全球版" },
            recentLogins: [],
            isSimulation: true,
            storageWarning: suffix ? `登录记录警告-${suffix}` : null,
        })
        await flushPromises()
        read.recent.resolve({ items: [], storageWarning: suffix ? `最近模型警告-${suffix}` : null })
        await flushPromises()
    }
    let read = prepareRead()
    const wrapper = mount(defineComponent({ setup: () => () => (visible.value ? h(ConnectionPanel, { bridge, activeProject }) : null) }))
    try {
        await complete(read, "原值")
        expect(document.body.textContent).toContain("登录记录警告-原值")
        expect(document.body.textContent).toContain("最近模型警告-原值")
        ElMessage.closeAll()
        visible.value = false
        await flushPromises()
        read = prepareRead()
        visible.value = true
        await flushPromises()
        await complete(read, "原值")
        expect(document.querySelectorAll(".el-message")).toHaveLength(0)
        // 主动刷新会先清空最近记录警告, 读取中的临时空值不能视为真实清除.
        const refreshing = pendingResult<Methods["powerbi.serviceRecentModels"]["result"]>()
        request.mockReturnValueOnce({ id: "refresh-recent", promise: refreshing.promise })
        request.mockReturnValueOnce({ id: "refresh-models", promise: Promise.resolve([]) })
        await wrapper.get(".connection-account button").trigger("click")
        await flushPromises()
        expect(wrapper.get(".connection-center").attributes("aria-busy")).toBe("true")
        refreshing.resolve({ items: [], storageWarning: "最近模型警告-原值" })
        await flushPromises()
        expect(document.querySelectorAll(".el-message")).toHaveLength(0)
        // 取消只结束读取, 不代表宿主已经清除存储警告; 下次相同事实仍不重放.
        const cancelled = pendingResult<Methods["powerbi.serviceRecentModels"]["result"]>()
        request.mockReturnValueOnce({ id: "cancelled-recent", promise: cancelled.promise })
        await wrapper.get(".connection-account button").trigger("click")
        await flushPromises()
        request.mockReturnValueOnce({ id: "cancel-recent", promise: Promise.resolve({ accepted: true }) })
        await operationCancel().trigger("click")
        expect(wrapper.get(".connection-center").attributes("aria-busy")).toBe("true")
        cancelled.reject(new BridgeError("CANCELLED", "操作已取消"))
        await flushPromises()
        ElMessage.closeAll()
        request.mockReturnValueOnce({ id: "reread-recent", promise: Promise.resolve({ items: [], storageWarning: "最近模型警告-原值" }) })
        request.mockReturnValueOnce({ id: "reread-models", promise: Promise.resolve([]) })
        await wrapper.get(".connection-account button").trigger("click")
        await flushPromises()
        expect(document.querySelectorAll(".el-message")).toHaveLength(0)
        visible.value = false
        await flushPromises()
        read = prepareRead()
        visible.value = true
        await flushPromises()
        await complete(read, "新值")
        expect(document.body.textContent).toContain("登录记录警告-新值")
        expect(document.body.textContent).toContain("最近模型警告-新值")
        expect(document.body.textContent).not.toContain("浏览器模拟登录与目录")
        ElMessage.closeAll()
        visible.value = false
        await flushPromises()
        read = prepareRead()
        visible.value = true
        await flushPromises()
        await complete(read)
        expect(document.querySelectorAll(".el-message")).toHaveLength(0)
        visible.value = false
        await flushPromises()
        read = prepareRead()
        visible.value = true
        await flushPromises()
        await complete(read, "新值")
        expect(document.body.textContent).toContain("登录记录警告-新值")
        expect(document.body.textContent).toContain("最近模型警告-新值")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

/** 来源切换的后台核验保持静默; 已连接与未连接、已登录与未登录使用同一消息边界. */
it.each([
    { connected: false, signedIn: false },
    { connected: false, signedIn: true },
    { connected: true, signedIn: false },
    { connected: true, signedIn: true },
])("来源切换静默核验, connected=$connected signedIn=$signedIn", async ({ connected, signedIn }) => {
    setLocale("zh-cn")
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge, activeProject: connected ? previewProject : undefined }, attachTo: document.body })
    await flushPromises()
    const card = wrapper.get(".instance-card").element
    const status = pendingResult<Methods["powerbi.serviceStatus"]["result"]>()
    const recents = pendingResult<Methods["powerbi.serviceRecentModels"]["result"]>()
    const models = pendingResult<Methods["powerbi.serviceModels"]["result"]>()
    request.mockReturnValueOnce({ id: "source-status", promise: status.promise })
    if (signedIn) {
        request.mockReturnValueOnce({ id: "source-recents", promise: recents.promise })
        request.mockReturnValueOnce({ id: "source-models", promise: models.promise })
    }
    await wrapper.get("#tab-service").trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("powerbi.serviceStatus", {})
    expect(document.querySelector(".operation-feedback")).toBeNull()
    expect(wrapper.get("#tab-server").classes()).toContain("is-disabled")
    status.resolve({
        account: signedIn ? { username: "preview@example.com", environmentName: "GlobalCloud", environmentLabel: "全球版" } : null,
        recentLogins: [],
        isSimulation: true,
    })
    await flushPromises()
    if (signedIn) {
        expect(request).toHaveBeenLastCalledWith("powerbi.serviceRecentModels", {})
        expect(document.querySelector(".operation-feedback")).toBeNull()
        recents.resolve({ items: [], storageWarning: null })
        await flushPromises()
        expect(request).toHaveBeenLastCalledWith("powerbi.serviceModels", {})
        expect(document.querySelector(".operation-feedback")).toBeNull()
        models.resolve([])
        await flushPromises()
    }
    await wrapper.get("#tab-server").trigger("click")
    await flushPromises()
    expect(document.querySelector(".operation-feedback")).toBeNull()
    const scan = pendingResult<Methods["powerbi.instances"]["result"]>()
    request.mockReturnValueOnce({ id: "source-scan", promise: scan.promise })
    await wrapper.get("#tab-desktop").trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("powerbi.instances", {})
    expect(wrapper.get(".instance-card").element).toBe(card)
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    expect(wrapper.get("#tab-service").classes()).toContain("is-disabled")
    expect(document.querySelector(".operation-feedback")).toBeNull()
    scan.reject(new BridgeError("SCAN_FAILED", "后台核验失败"))
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toContain("后台核验失败")
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    expect(wrapper.get("#tab-service").classes()).not.toContain("is-disabled")
    // 用户主动重试使用同一接口, 仍显示唯一在途消息并保留取消入口.
    const retry = pendingResult<Methods["powerbi.instances"]["result"]>()
    request.mockReturnValueOnce({ id: "explicit-scan", promise: retry.promise })
    await wrapper.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(operationMessage().text()).toContain("正在")
    expect(operationCancel().attributes("disabled")).toBeUndefined()
    retry.resolve({ timestamp: new Date().toISOString(), instances: [] })
    await flushPromises()
    expect(document.querySelector(".operation-feedback")).toBeNull()
    wrapper.unmount()
    bridge.dispose()
})

/** Desktop 来源往返保留卡片 DOM, 重新激活仍刷新身份, 不反复挂载造成空白. */
it("来源切回 Desktop 时复用原卡片并重新扫描", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge } })
    await flushPromises()
    const card = wrapper.get(".powerbi-panel .instance-card").element
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(1)
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "SSAS / XMLA")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.find(".powerbi-panel").exists()).toBe(false)
    let resolve!: (value: Methods["powerbi.instances"]["result"]) => void
    request.mockReturnValueOnce({
        id: "reactivated-scan",
        promise: new Promise((accept) => {
            resolve = accept
        }),
    })
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Desktop")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.get(".powerbi-panel .instance-card").element).toBe(card)
    expect(wrapper.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(2)
    resolve({ timestamp: new Date().toISOString(), instances: [] })
    await flushPromises()
    expect(wrapper.find(".powerbi-panel .instance-card").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

it.each(["zh-cn", "en-us"] as const)("%s 本地文件与已打开实例同在 Desktop 来源, 文件入口无路径参数且实例入口保留 PID", async (locale: AppLocale) => {
    setLocale(locale)
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge }, attachTo: document.body })
    const popup = wrapper
    await flushPromises()
    expect(popup.findAll('[role="tab"]').map((item) => item.text())).toEqual(["Power BI Desktop", "Power BI Service", "SSAS / XMLA"])
    expect(popup.find(".connection-center > h3").exists()).toBe(false)
    expect(popup.text()).toContain("示例报表 (模拟数据)")
    expect(popup.find(".scan-toolbar").exists()).toBe(false)
    expect(popup.findAll(".desktop-instance-actions")).toHaveLength(1)
    expect(popup.findAll(".desktop-file-button")).toHaveLength(1)
    expect(
        popup
            .findAll(".desktop-instance-actions button")
            .filter((button) => button.text() === (locale === "zh-cn" ? "检查 UDF 能力" : "Check UDF capabilities")),
    ).toHaveLength(0)
    expect(popup.get(".desktop-instance-actions .instance-refresh-button").text()).toBe(locale === "zh-cn" ? "刷新实例" : "Refresh instances")
    expect(popup.find(".el-dialog__footer").exists()).toBe(false)
    expect(popup.find(".el-dialog").exists()).toBe(false)
    await popup
        .findAll("button")
        .find((button) => button.text().includes(locale === "zh-cn" ? "选择 PBIP / PBIX 文件" : "Select PBIP / PBIX file"))!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith("project.open", {}, undefined, expect.any(Function))
    expect(wrapper.emitted("connected")?.[0]?.[0]).toMatchObject({ connection: { kind: "desktop" }, pages: [] })
    await popup
        .findAll("button")
        .find((button) => button.text() === (locale === "zh-cn" ? "连接此实例" : "Connect to this instance"))!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith("project.connectLocal", { processId: "1000", instanceId: "mock-desktop-instance-1000" })
    expect(request.mock.calls.map(([method]) => method)).not.toContain("project.connectRemote")
    wrapper.unmount()
    bridge.dispose()
})

it.each(["success", "failure", "cancelled"] as const)("Desktop 文件连接区分选择和启动阶段, %s 时保留原模型并清理状态", async (outcome) => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge, activeProject: previewProject }, attachTo: document.body })
    const popup = wrapper
    await flushPromises()
    const children = Array.from(popup.get(".connection-center").element.children)
    const scansBefore = request.mock.calls.filter(([method]) => method === "powerbi.instances").length
    let resolve!: (snapshot: typeof previewProject) => void
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "desktop-open-pending",
        promise: new Promise((accept, fail) => {
            resolve = accept
            reject = fail
        }),
    })
    const open = popup.findAll("button").find((button) => button.text().includes("选择 PBIP / PBIX 文件"))!
    await open.trigger("click")
    expect(operationMessage().text()).toContain("原生窗口选择 PBIP 或 PBIX")
    const message = operationMessage().element
    expect(popup.element.contains(message)).toBe(false)
    expect(Array.from(popup.get(".connection-center").element.children)).toEqual(children)
    expect(popup.find(".desktop-file-loading").exists()).toBe(false)
    const onPhase = request.mock.calls.at(-1)?.[3]
    onPhase?.("connecting")
    wrapper.findComponent(PowerBiPanel).vm.$emit("busy", false)
    await flushPromises()
    expect(operationMessage().text()).toContain("正在通过 Power BI Desktop")
    expect(operationMessage().element).toBe(message)
    expect(Array.from(popup.get(".connection-center").element.children)).toEqual(children)
    expect(open.attributes("disabled")).toBeDefined()
    expect(open.classes()).toContain("is-loading")
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(scansBefore)
    expect(wrapper.findAllComponents({ name: "ElTabPane" }).every((tab) => tab.props("disabled"))).toBe(true)
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("powerbi.serviceStatus")
    if (outcome === "success") resolve({ ...previewProject, connection: { kind: "desktop", label: "Power BI Desktop" } })
    else {
        if (outcome === "cancelled") {
            await operationCancel().trigger("click")
            expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "desktop-open-pending" })
            expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(true)
            expect(open.classes()).toContain("is-loading")
        }
        reject(
            new BridgeError(
                outcome === "cancelled" ? "CANCELLED" : "DESKTOP_NOT_INSTALLED",
                outcome === "cancelled" ? "操作已取消" : "未找到可用的 Power BI Desktop, 请安装 Microsoft 官方版本后重试",
            ),
        )
    }
    await flushPromises()
    expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(false)
    expect(open.attributes("disabled")).toBeUndefined()
    expect(request.mock.calls.filter(([method]) => method === "powerbi.instances")).toHaveLength(scansBefore + (outcome === "success" ? 1 : 0))
    if (outcome === "success") expect(wrapper.emitted("connected")).toHaveLength(1)
    else {
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(wrapper.props("activeProject")).toEqual(previewProject)
        expect(
            outcome === "cancelled"
                ? Array.from(document.querySelectorAll(".el-message--info")).at(-1)?.textContent
                : document.querySelector(".el-message--error")?.textContent,
        ).toContain(outcome === "cancelled" ? "操作已取消" : "请安装 Microsoft 官方版本")
    }
    wrapper.unmount()
    bridge.dispose()
})

it("外部操作锁定三种来源和 Desktop 写入入口, 已在途请求仍可取消并等待终态", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge }, attachTo: document.body })
    await flushPromises()
    await wrapper.setProps({ disabled: true })
    const before = request.mock.calls.length
    await wrapper.get(".desktop-file-button").trigger("click")
    await wrapper.get(".instance-connect-button").trigger("click")
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    expect(request.mock.calls).toHaveLength(before)
    expect(wrapper.findAllComponents({ name: "ElTabPane" }).every((tab) => tab.props("disabled"))).toBe(true)
    await wrapper.setProps({ disabled: false })
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "externally-blocked-open",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    await wrapper.get(".desktop-file-button").trigger("click")
    await wrapper.setProps({ disabled: true })
    const cancel = operationCancel()
    expect(cancel.attributes("disabled")).toBeUndefined()
    await cancel.trigger("click")
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "externally-blocked-open" })
    expect(wrapper.get(".connection-center").attributes("aria-busy")).toBe("true")
    expect(wrapper.emitted("connected")).toBeUndefined()
    reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(wrapper.get(".connection-center").attributes("aria-busy")).toBe("false")
    expect(wrapper.get(".desktop-file-button").attributes("disabled")).toBeDefined()
    wrapper.unmount()
    bridge.dispose()
})

/** 创建连接页内联连接组件与显式模拟桥接, 直接检查组件输出. */
interface RecentSlot {
    source: string
    serviceSignedIn: boolean
    serviceRecents: RecentPowerBiServiceModel[]
    openServiceRecent: (id: string) => Promise<void>
    deleteServiceRecent: (id: string) => Promise<void>
}
/** 测试内联最近连接插槽, 保持目录数据及重开动作来自同一组件状态. */
async function setup(discover = true, onRecent?: (value: RecentSlot) => void) {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, {
        props: { bridge },
        slots: onRecent
            ? {
                  recent: (value: RecentSlot) => {
                      onRecent(value)
                      return []
                  },
              }
            : {},
        attachTo: document.body,
    })
    const popup = wrapper
    await flushPromises()
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    await flushPromises()
    if (discover) {
        await popup.get('input[aria-label="工作或学校邮箱"]').setValue("preview@example.com")
        await popup
            .findAll("button")
            .find((button) => button.text() === "登录")!
            .trigger("click")
        await flushPromises()
        wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "GlobalCloud")
        await flushPromises()
    }
    return { bridge, request, wrapper, popup }
}
/** 打开 Service 模拟目录并选中模型, 返回真实组件与请求观察器供连接状态验收. */
async function serviceReady(onRecent?: (value: RecentSlot) => void) {
    const context = await setup(true, onRecent)
    await context.popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    context.wrapper.findAllComponents({ name: "ElSelect" })[1]!.vm.$emit("update:modelValue", "preview-service-model")
    await flushPromises()
    return context
}

/** 临时锁保持外观时仍阻止操作, 原本不可用的模型选择与父级业务锁不能被美化为可用. */
it("Service 区分临时锁、缺少工作区和父级业务禁用", async () => {
    const { bridge, request, wrapper } = await serviceReady()
    const selects = wrapper.findAllComponents({ name: "ElSelect" })
    selects[0]!.vm.$emit("update:modelValue", "")
    await flushPromises()
    await wrapper.setProps({ disabled: true, operationLocked: true })
    expect(selects[0]!.props("disabled")).toBe(true)
    expect(selects[0]!.classes()).toContain("operation-lock")
    expect(selects[1]!.props("disabled")).toBe(true)
    expect(selects[1]!.classes()).not.toContain("operation-lock")
    request.mockClear()
    await wrapper.get(".connection-account button").trigger("click")
    expect(request).not.toHaveBeenCalled()
    await wrapper.setProps({ operationLocked: false })
    expect(wrapper.findAll(".operation-lock")).toHaveLength(0)
    expect(selects[0]!.props("disabled")).toBe(true)
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(selects[0]!.props("disabled")).toBe(false)
    expect(selects[1]!.props("disabled")).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})

/** 多云环境未选时登录本来不可用, 刷新期间不能改变该状态的外观或发出登录请求. */
it("环境未选择的登录按钮不继承临时锁外观", async () => {
    const { bridge, request, wrapper } = await setup()
    wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "")
    await flushPromises()
    await wrapper.setProps({ disabled: true, operationLocked: true })
    const login = wrapper.findAll("button").find((button) => button.text() === "登录")!
    expect(login.attributes("disabled")).toBeDefined()
    expect(login.classes()).not.toContain("operation-lock")
    expect(wrapper.get(".el-input").classes()).toContain("operation-lock")
    request.mockClear()
    await login.trigger("click")
    expect(request).not.toHaveBeenCalled()
    wrapper.unmount()
    bridge.dispose()
})

it.each(["success", "failure"] as const)("删除云端最近记录 %s 时仅移除该记录, 保留当前目录和模型", async (outcome) => {
    let recent!: RecentSlot
    const { bridge, request, wrapper } = await serviceReady((value) => {
        recent = value
    })
    await wrapper.findAll(".instance-card button")[0]!.trigger("click")
    await flushPromises()
    const record = recent.serviceRecents[0]!
    expect(record).toBeDefined()
    const current = { ...previewProject, connection: { kind: "powerbi-service" as const, label: "Power BI Service", modelId: record.modelId } }
    await wrapper.setProps({ activeProject: current })
    await flushPromises()
    const modelSelection = wrapper.findAllComponents({ name: "ElSelect" })[1]!.props("modelValue")
    request.mockClear()
    if (outcome === "failure") request.mockReturnValueOnce({ id: "delete-failure", promise: Promise.reject(new Error("云记录删除失败")) })
    await recent.deleteServiceRecent(record.id)
    await flushPromises()
    expect(request).toHaveBeenCalledWith("powerbi.serviceDeleteRecentModel", { id: record.id })
    expect(recent.serviceRecents.some((item) => item.id === record.id)).toBe(outcome === "failure")
    expect(wrapper.props("activeProject")).toEqual(current)
    expect(wrapper.findAllComponents({ name: "ElSelect" })[1]!.props("modelValue")).toBe(modelSelection)
    expect(request.mock.calls.map(([method]) => method)).not.toContain("project.connectService")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("powerbi.serviceSignOut")
    if (outcome === "failure") expect(document.querySelector(".el-message--error")?.textContent).toContain("云记录删除失败")
    wrapper.unmount()
    bridge.dispose()
})

it.each(["zh-cn", "en-us"] as const)("%s 首次登录检测 alert 按宿主认证阶段切换, 取消确认仍等待权威终态", async (locale) => {
    const { bridge, request, wrapper, popup } = await setup(false)
    await popup.get('input[aria-label="工作或学校邮箱"]').setValue("first@example.com")
    setLocale(locale)
    await flushPromises()
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "discovery-pending",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    const loginButton = () => popup.findAll("button").find((button) => button.text() === (locale === "zh-cn" ? "登录" : "Sign in"))!
    await loginButton().trigger("click")
    await flushPromises()
    expect(operationMessage().text()).toContain(locale === "zh-cn" ? "正在检测账号的云环境" : "Detecting the account's cloud environment")
    const message = operationMessage().element
    expect(popup.element.contains(message)).toBe(false)
    expect(popup.find(".service-login-loading").exists()).toBe(false)
    const onPhase = request.mock.calls.at(-1)?.[3]
    onPhase?.("authenticating")
    await flushPromises()
    expect(operationMessage().text()).toContain(locale === "zh-cn" ? "系统浏览器" : "system browser")
    expect(operationMessage().element).toBe(message)
    await operationCancel().trigger("click")
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "discovery-pending" })
    expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(true)
    expect(loginButton().attributes("disabled")).toBeDefined()
    reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(false)
    expect(loginButton().attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})

it("工作区与模型支持名称搜索, 重名时仍通过目录身份选择", async () => {
    const { bridge, wrapper, popup } = await serviceReady()
    const selects = wrapper.findAllComponents({ name: "ElSelect" })
    expect(selects.map((select) => select.props("filterable"))).toEqual([true, true])
    await selects[0]!.get("input").setValue("无匹配的工作区")
    await flushPromises()
    expect(selects[0]!.props("modelValue")).toBe("示例工作区")
    expect(popup.get(".instance-card").text()).toContain("销售模型 (模拟)")
    wrapper.unmount()
    bridge.dispose()
})

it("登录取消太迟而成功后, 自动目录读取恢复独立取消且使用新请求标识", async () => {
    const { bridge, request, wrapper, popup } = await setup()
    let resolve!: (value: Methods["powerbi.serviceSignIn"]["result"]) => void
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "sign-in-too-late",
        promise: new Promise((accept) => {
            resolve = accept
        }),
    })
    request.mockReturnValueOnce({ id: "first-cancel-ack", promise: Promise.resolve({ accepted: false }) })
    request.mockReturnValueOnce({
        id: "next-directory-pending",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    const cancel = operationCancel
    await cancel().trigger("click")
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "sign-in-too-late" })
    expect(cancel().attributes("disabled")).toBeDefined()
    resolve({
        account: { username: "preview@example.com", environmentName: "GlobalCloud", environmentLabel: "全球版" },
        recentLogins: [],
        environments: [],
        isSimulation: true,
    })
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("powerbi.serviceRecentModels", {})
    expect(cancel().attributes("disabled")).toBeUndefined()
    await cancel().trigger("click")
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "next-directory-pending" })
    expect(popup.get(".connection-center").attributes("aria-busy")).toBe("true")
    reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(popup.get(".connection-center").attributes("aria-busy")).toBe("false")
    expect(wrapper.emitted("connected")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})

it.each(["removed", "failure"] as const)("云端最近连接重开先刷新权限, %s 时清除记录且不提交旧句柄", async (outcome) => {
    let recent!: RecentSlot
    const { bridge, request, wrapper, popup } = await serviceReady((value) => {
        recent = value
    })
    expect(recent.serviceRecents).toEqual([])
    await popup.get(".instance-card .instance-connect-button").trigger("click")
    await flushPromises()
    expect(recent.serviceRecents).toHaveLength(1)
    const id = recent.serviceRecents[0]!.id
    const connectionCount = request.mock.calls.filter(([method]) => method === "project.connectService").length
    request.mockReturnValueOnce({
        id: "revalidate-recent",
        promise:
            outcome === "removed"
                ? Promise.resolve({ items: [], storageWarning: null })
                : Promise.reject(new BridgeError("CATALOG_FAILED", "读取模型目录失败")),
    })
    await recent.openServiceRecent(id)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith(outcome === "removed" ? "powerbi.serviceModels" : "powerbi.serviceRecentModels", {})
    expect(request.mock.calls.filter(([method]) => method === "project.connectService")).toHaveLength(connectionCount)
    expect(recent.serviceRecents).toEqual([])
    expect(document.querySelector(".el-message--error")?.textContent).toContain(outcome === "removed" ? "最近连接已不可用" : "读取模型目录失败")
    wrapper.unmount()
    bridge.dispose()
})

it.each(["failure", "cancelled"] as const)("重新进入 Service 时先清空旧历史, 登录状态读取 %s 后不恢复旧权限记录", async (outcome) => {
    let recent!: RecentSlot
    const { bridge, request, wrapper, popup } = await serviceReady((value) => {
        recent = value
    })
    await popup.get(".instance-card .instance-connect-button").trigger("click")
    await flushPromises()
    expect(recent.serviceRecents).toHaveLength(1)
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Desktop")!
        .trigger("click")
    await flushPromises()
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "status-refresh-pending",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    await flushPromises()
    expect(recent.serviceRecents).toEqual([])
    expect(document.querySelector(".operation-feedback")).toBeNull()
    expect(popup.get(".connection-center").attributes("aria-busy")).toBe("true")
    reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "STATUS_FAILED", outcome === "cancelled" ? "操作已取消" : "读取登录状态失败"))
    await flushPromises()
    expect(recent.serviceSignedIn).toBe(true)
    expect(recent.serviceRecents).toEqual([])
    expect(
        outcome === "cancelled"
            ? Array.from(document.querySelectorAll(".el-message--info")).at(-1)?.textContent
            : document.querySelector(".el-message--error")?.textContent,
    ).toContain(outcome === "cancelled" ? "操作已取消" : "读取登录状态失败")
    wrapper.unmount()
    bridge.dispose()
})

it.each(["failure", "cancelled", "revoked"] as const)("刷新 Service 在完整目录成功前暂存最近记录, 后续 %s 时不发布旧历史", async (outcome) => {
    let recent!: RecentSlot
    const { bridge, request, wrapper, popup } = await serviceReady((value) => {
        recent = value
    })
    await popup.get(".instance-card .instance-connect-button").trigger("click")
    await flushPromises()
    const saved = recent.serviceRecents.map((item) => ({ ...item }))
    expect(saved).toHaveLength(1)
    request.mockReturnValueOnce({ id: "fresh-recents", promise: Promise.resolve({ items: saved, storageWarning: null }) })
    let resolve!: (value: []) => void
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "fresh-models-pending",
        promise: new Promise((accept, fail) => {
            resolve = accept
            reject = fail
        }),
    })
    await popup
        .findAll("button")
        .find((button) => button.text() === "刷新模型列表")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("powerbi.serviceModels", {})
    expect(recent.serviceRecents).toEqual([])
    if (outcome === "revoked") resolve([])
    else {
        if (outcome === "cancelled") {
            await operationCancel().trigger("click")
            expect(recent.serviceRecents).toEqual([])
            expect(popup.get(".connection-center").attributes("aria-busy")).toBe("true")
        }
        reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "CATALOG_FAILED", outcome === "cancelled" ? "操作已取消" : "读取模型目录失败"))
    }
    await flushPromises()
    expect(recent.serviceRecents).toEqual([])
    expect(wrapper.findAllComponents({ name: "ElOption" })).toHaveLength(0)
    expect(wrapper.emitted("connected")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})

it("当前云端最近连接核验后解除忙状态再导航, 不重复连接; 退出及其他账号不展示历史", async () => {
    let recent!: RecentSlot
    const { bridge, request, wrapper, popup } = await serviceReady((value) => {
        recent = value
    })
    await popup.get(".instance-card .instance-connect-button").trigger("click")
    await flushPromises()
    const snapshot = wrapper.emitted("connected")![0]![0] as ProjectSnapshot
    await wrapper.setProps({ activeProject: snapshot })
    const id = recent.serviceRecents[0]!.id
    const before = request.mock.calls.filter(([method]) => method === "project.connectService").length
    await recent.openServiceRecent(id)
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "project.connectService")).toHaveLength(before)
    expect(wrapper.emitted("viewModel")).toEqual([[]])
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    await popup
        .findAll("button")
        .find((button) => button.text() === "退出登录")!
        .trigger("click")
    await flushPromises()
    expect(recent.serviceSignedIn).toBe(false)
    expect(recent.serviceRecents).toEqual([])
    await popup.get('input[aria-label="工作或学校邮箱"]').setValue("other@example.com")
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    wrapper.findAllComponents({ name: "ElSelect" }).at(-1)!.vm.$emit("update:modelValue", "GlobalCloud")
    await flushPromises()
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(recent.serviceSignedIn).toBe(true)
    expect(recent.serviceRecents).toEqual([])
    wrapper.unmount()
    bridge.dispose()
})

it.each(["success", "failure"] as const)("最近云端入口按最新目录身份同步跨工作区目标, %s 后保留准确当前卡片", async (outcome) => {
    let recent!: RecentSlot
    const { bridge, request, wrapper, popup } = await serviceReady((value) => {
        recent = value
    })
    const current: ProjectSnapshot = {
        ...previewProject,
        model: { ...previewProject.model, name: "同名模型" },
        connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "preview-service-model" },
    }
    await wrapper.setProps({ activeProject: current })
    const target: ProjectSnapshot = {
        ...current,
        projectId: "service-target-b",
        connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "service-model-b" },
    }
    request.mockReturnValueOnce({
        id: "recent-targets",
        promise: Promise.resolve({
            items: [{ id: "recent-b", modelId: "service-model-b", name: "同名模型", workspace: "另一个工作区", lastUsedAt: "2026-10-15T01:00:00Z" }],
            storageWarning: null,
        }),
    })
    request.mockReturnValueOnce({
        id: "current-catalog",
        promise: Promise.resolve([
            { id: "preview-service-model", name: "同名模型", workspace: "原工作区", workspaceId: "workspace-a", unavailableReason: null },
            { id: "service-model-b", name: "同名模型", workspace: "另一个工作区", workspaceId: "workspace-b", unavailableReason: null },
        ]),
    })
    let resolve!: (value: ProjectSnapshot) => void
    let reject!: (cause: Error) => void
    request.mockReturnValueOnce({
        id: "connect-recent-b",
        promise: new Promise((accept, fail) => {
            resolve = accept
            reject = fail
        }),
    })
    const opening = recent.openServiceRecent("recent-b")
    try {
        await flushPromises()
        const selects = wrapper.findAllComponents({ name: "ElSelect" })
        expect(selects.map((select) => select.props("modelValue"))).toEqual(["workspace-b", "service-model-b"])
        expect(request).toHaveBeenLastCalledWith("project.connectService", { modelId: "service-model-b" })
        expect(popup.findAll(".instance-card")).toHaveLength(2)
        if (outcome === "success") resolve(target)
        else reject(new BridgeError("CONNECTION_FAILED", "读取模型失败"))
        await opening
        await flushPromises()
        if (outcome === "success") {
            expect(wrapper.emitted("connected")?.[0]?.[0]).toEqual(target)
            await wrapper.setProps({ activeProject: target })
            expect(popup.findAll(".instance-card")).toHaveLength(1)
            expect(popup.get(".instance-card").classes()).toContain("is-current")
        } else {
            expect(wrapper.emitted("connected")).toBeUndefined()
            expect(wrapper.props("activeProject")).toEqual(current)
            expect(document.querySelector(".el-message--error")?.textContent).toContain("读取模型失败")
            expect(popup.findAll(".instance-card")).toHaveLength(2)
            expect(
                popup
                    .findAll(".instance-card")
                    .find((card) => !card.classes().includes("is-current"))!
                    .text(),
            ).toContain("另一个工作区")
        }
    } finally {
        resolve(target)
        await opening
        wrapper.unmount()
        bridge.dispose()
    }
})

it("最近目标句柄未出现在最新目录时不以同名替代连接, 保留当前模型", async () => {
    let recent!: RecentSlot
    const { bridge, request, wrapper } = await serviceReady((value) => {
        recent = value
    })
    const current: ProjectSnapshot = { ...previewProject, connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "preview-service-model" } }
    await wrapper.setProps({ activeProject: current })
    request.mockReturnValueOnce({
        id: "stale-recent-handle",
        promise: Promise.resolve({
            items: [{ id: "recent-b", modelId: "old-handle-b", name: "同名模型", workspace: "同名工作区", lastUsedAt: "2026-10-15T01:00:00Z" }],
            storageWarning: null,
        }),
    })
    request.mockReturnValueOnce({
        id: "fresh-catalog-handle",
        promise: Promise.resolve([{ id: "new-handle-b", name: "同名模型", workspace: "同名工作区", workspaceId: "workspace-b", unavailableReason: null }]),
    })
    await recent.openServiceRecent("recent-b")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "project.connectService")).toHaveLength(0)
    expect(wrapper.emitted("connected")).toBeUndefined()
    expect(wrapper.props("activeProject")).toEqual(current)
    expect(recent.serviceRecents).toEqual([])
    expect(document.querySelector(".el-message--error")?.textContent).toContain("最近连接已不可用")
    wrapper.unmount()
    bridge.dispose()
})
it("待保存状态阻止 Service 切换、退出账号和刷新目录, 不发送连接或身份变更请求", async () => {
    const { bridge, request, wrapper, popup } = await serviceReady()
    await wrapper.setProps({ disabled: true, activeProject: { ...previewProject, hasPendingChanges: true } })
    const before = request.mock.calls.length
    const buttons = [
        popup.get(".instance-card .instance-connect-button"),
        ...["退出登录", "刷新模型列表"].map((label) => popup.findAll("button").find((item) => item.text() === label)!),
    ]
    expect(buttons.every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    await Promise.all(buttons.map((button) => button.trigger("click")))
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "SSAS / XMLA")!
        .trigger("click")
    expect(request.mock.calls).toHaveLength(before)
    expect(wrapper.props("activeProject")?.hasPendingChanges).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})
it("只显示一个登录按钮, 多云等待选择, 改邮箱会撤销原选择", async () => {
    const { bridge, request, wrapper, popup } = await setup(false)
    const loginButton = () => popup.findAll("button").find((button) => button.text() === "登录")!
    expect(popup.findAll("button").filter((button) => button.text() === "登录")).toHaveLength(1)
    expect(popup.text()).not.toContain("获取登录环境")
    const email = popup.get('input[aria-label="工作或学校邮箱"]')
    await expectConsoleWarnings([["async-validator:", ["email is not a valid email"]]], async () => {
        await email.setValue("invalid")
        await loginButton().trigger("click")
        await flushPromises()
    })
    expect(request.mock.calls.some(([method]) => method === "powerbi.serviceSignIn")).toBe(false)
    await email.setValue("preview@example.com")
    await loginButton().trigger("click")
    await flushPromises()
    expect(popup.text()).toContain("此邮箱有多个登录环境")
    expect(popup.text()).not.toContain("preview@example.com (模拟)")
    expect(loginButton().attributes("disabled")).toBeDefined()
    await loginButton().trigger("click")
    expect(request.mock.calls.filter(([method]) => method === "powerbi.serviceSignIn")).toHaveLength(1)
    wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "ChinaCloud")
    await flushPromises()
    await email.setValue("other@example.com")
    await flushPromises()
    expect(popup.text()).not.toContain("此邮箱有多个登录环境")
    expect(wrapper.findComponent({ name: "ElSelect" }).exists()).toBe(false)
    await loginButton().trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith(
        "powerbi.serviceSignIn",
        { email: "other@example.com", environmentName: "", remember: true },
        undefined,
        expect.any(Function),
    )
    wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "ChinaCloud")
    await flushPromises()
    await loginButton().trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith(
        "powerbi.serviceSignIn",
        { email: "other@example.com", environmentName: "ChinaCloud", remember: true },
        undefined,
        expect.any(Function),
    )
    expect(popup.text()).toContain("世纪互联 (中国)")
    wrapper.unmount()
    bridge.dispose()
})
it.each(["GlobalCloud", "ChinaCloud"])("单个 %s 一次登录, 不展示云选择", async (environmentName) => {
    const { bridge, request, wrapper, popup } = await setup(false)
    await popup.get('input[aria-label="工作或学校邮箱"]').setValue("test@example.com")
    request.mockReturnValueOnce({
        id: "login",
        promise: Promise.resolve({
            account: { username: "test@example.com", environmentName, environmentLabel: environmentName },
            recentLogins: [],
            environments: [],
            isSimulation: true,
        }),
    })
    request.mockReturnValueOnce({ id: "recents", promise: Promise.resolve({ items: [], storageWarning: null }) })
    request.mockReturnValueOnce({ id: "models", promise: Promise.resolve([]) })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith(
        "powerbi.serviceSignIn",
        { email: "test@example.com", environmentName: "", remember: true },
        undefined,
        expect.any(Function),
    )
    expect(popup.text()).toContain("test@example.com")
    expect(popup.findAll('input[aria-label="登录环境"]')).toHaveLength(0)
    wrapper.unmount()
    bridge.dispose()
})
it("最近邮箱跨组件重开回填, 已选中国云直接登录, 可删除记录", async () => {
    const context = await setup()
    context.wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "ChinaCloud")
    await flushPromises()
    await context.popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    await context.popup
        .findAll("button")
        .find((button) => button.text() === "退出登录")!
        .trigger("click")
    await flushPromises()
    context.wrapper.unmount()
    const wrapper = mount(ConnectionPanel, { props: { bridge: context.bridge }, attachTo: document.body })
    await flushPromises()
    const popup = wrapper
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    await flushPromises()
    expect((popup.get('input[aria-label="工作或学校邮箱"]').element as HTMLInputElement).value).toBe("preview@example.com")
    expect(popup.text()).toContain("最近登录")
    expect(popup.findAll('input[aria-label="登录环境"]')).toHaveLength(0)
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(popup.findAll('input[aria-label="登录环境"]')).toHaveLength(0)
    expect(popup.get(".account-name").text()).toContain("世纪互联")
    expect(context.request).toHaveBeenCalledWith(
        "powerbi.serviceSignIn",
        { email: "preview@example.com", environmentName: "ChinaCloud", remember: true },
        undefined,
        expect.any(Function),
    )
    await popup
        .findAll("button")
        .find((button) => button.text() === "退出登录")!
        .trigger("click")
    await flushPromises()
    wrapper.findAllComponents({ name: "ElSelect" })[0]!.vm.$emit("update:modelValue", "preview@example.com/ChinaCloud")
    await flushPromises()
    await popup
        .findAll("button")
        .find((button) => button.text() === "删除记录")!
        .trigger("click")
    await flushPromises()
    expect(context.request).toHaveBeenCalledWith("powerbi.serviceDeleteLogin", { id: "preview@example.com/ChinaCloud" })
    expect(popup.text()).not.toContain("最近登录")
    expect((popup.get('input[aria-label="工作或学校邮箱"]').element as HTMLInputElement).value).toBe("preview@example.com")
    wrapper.unmount()
    context.bridge.dispose()
})
it.each(["success", "failure", "cancelled"] as const)("Service 单行卡片连接锁定并显示阶段提示, %s 后恢复操作", async (outcome) => {
    const { bridge, request, wrapper, popup } = await serviceReady()
    await wrapper.setProps({ activeProject: previewProject })
    let resolve!: (snapshot: typeof previewProject) => void
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "service-pending",
        promise: new Promise<typeof previewProject>((accept, fail) => {
            resolve = accept
            reject = fail
        }),
    })
    const connect = popup.get(".instance-card .instance-connect-button")
    await connect.trigger("click")
    expect(connect.attributes("disabled")).toBeDefined()
    expect(operationMessage().text()).toContain("正在连接 Power BI Service 模型")
    expect(operationMessage().text()).not.toContain("系统浏览器")
    expect(popup.get(".connection-center").attributes("aria-busy")).toBe("true")
    expect(wrapper.findAllComponents({ name: "ElSelect" }).every((select) => select.props("disabled"))).toBe(true)
    await connect.trigger("click")
    expect(request.mock.calls.filter(([method]) => method === "project.connectService")).toHaveLength(1)
    if (outcome === "success") {
        resolve({ ...previewProject, connection: { kind: "powerbi-service", label: "Power BI Service" } })
    } else {
        if (outcome === "cancelled") {
            await operationCancel().trigger("click")
            expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "service-pending" })
            expect(connect.attributes("disabled")).toBeDefined()
        }
        reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "CONNECTION_FAILED", outcome === "cancelled" ? "操作已取消" : "连接模型失败"))
    }
    await flushPromises()
    expect(connect.attributes("disabled")).toBeUndefined()
    expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(false)
    expect(popup.get(".connection-center").attributes("aria-busy")).toBe("false")
    if (outcome === "success") {
        expect(wrapper.emitted("connected")).toHaveLength(1)
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    } else {
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
        expect(wrapper.props("activeProject")).toEqual(previewProject)
        expect(connect.attributes("disabled")).toBeUndefined()
    }
    wrapper.unmount()
    bridge.dispose()
})
it.each(["success", "failure", "cancelled"] as const)("SSAS/XMLA 平铺编辑不锁导航, %s 后排空并重新读取上下文", async (outcome) => {
    const { bridge, request, wrapper, popup } = await setup()
    await popup
        .findAll('[role="tab"]')
        .find((button) => button.text() === "SSAS / XMLA")!
        .trigger("click")
    await flushPromises()
    let resolve!: (snapshot: typeof previewProject) => void
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "remote-pending",
        promise: new Promise<typeof previewProject>((accept, fail) => {
            resolve = accept
            reject = fail
        }),
    })
    await popup.get('input[aria-label="服务器"]').setValue("ssas.example")
    await popup.get('input[aria-label="数据库 / 模型"]').setValue("demo")
    const connect = popup.findAll("button").find((button) => button.text() === "连接")!
    await connect.trigger("click")
    expect(connect.classes()).toContain("is-loading")
    expect(operationMessage().text()).toContain("正在连接 SSAS / XMLA 模型")
    expect(connect.find(".el-icon.is-loading").exists()).toBe(true)
    expect(popup.find(".el-dialog__headerbtn").exists()).toBe(false)
    expect(popup.findAll("button").some((button) => button.text() === "关闭")).toBe(false)
    if (outcome === "success") {
        resolve({ ...previewProject, connection: { kind: "ssas-xmla", label: "SSAS / XMLA" } })
    } else {
        if (outcome === "cancelled") {
            await operationCancel().trigger("click")
            expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "remote-pending" })
            expect(connect.classes()).toContain("is-loading")
        }
        reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "CONNECTION_FAILED", outcome === "cancelled" ? "操作已取消" : "连接模型失败"))
    }
    await flushPromises()
    expect(connect.classes()).not.toContain("is-loading")
    expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(false)
    if (outcome === "success") expect(wrapper.emitted("connected")).toHaveLength(1)
    else {
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(connect.attributes("disabled")).toBeUndefined()
        request.mockReturnValueOnce({ id: "remote-retry", promise: Promise.reject(new BridgeError("CANCELLED", "取消填写")) })
        await connect.trigger("click")
        await flushPromises()
        expect(document.querySelector(".operation-feedback")).toBeNull()
    }
    wrapper.unmount()
    bridge.dispose()
})
it("登录后选择账号目录模型, 连接只发送后端 modelId 且模拟提示保留", async () => {
    const { bridge, request, wrapper, popup } = await setup()
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(Array.from(document.querySelectorAll(".el-message--info")).at(-1)?.textContent).toContain("浏览器模拟登录与目录")
    expect(popup.text()).toContain("preview@example.com (模拟)")
    expect(request).toHaveBeenCalledWith(
        "powerbi.serviceSignIn",
        { email: "preview@example.com", environmentName: "GlobalCloud", remember: true },
        undefined,
        expect.any(Function),
    )
    expect(request).toHaveBeenCalledWith("powerbi.serviceModels", {})
    const selects = wrapper.findAllComponents({ name: "ElSelect" })
    selects[1]!.vm.$emit("update:modelValue", "preview-service-model")
    await flushPromises()
    await popup
        .findAll("button")
        .find((button) => button.text() === "连接所选模型")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith("project.connectService", { modelId: "preview-service-model" })
    expect(wrapper.emitted("connected")?.[0]?.[0]).toMatchObject({ connection: { kind: "powerbi-service" }, pages: [] })
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})
it("登录失败保留连接页连接面板和重试入口, 不发出已连接事件", async () => {
    const { bridge, request, wrapper, popup } = await setup()
    request.mockReturnValueOnce({ id: "failed-login", promise: Promise.reject(new BridgeError("POWER_BI_SIGN_IN_FAILED", "组织账号登录失败")) })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toContain("组织账号登录失败")
    expect(wrapper.emitted("connected")).toBeUndefined()
    expect(
        popup
            .findAll("button")
            .find((button) => button.text() === "登录")!
            .attributes("disabled"),
    ).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})
it("同名工作区和模型按服务身份区分, 切换工作区清除原选择", async () => {
    const { bridge, request, wrapper, popup } = await setup()
    request.mockReturnValueOnce({
        id: "login",
        promise: Promise.resolve({
            account: { username: "test@example.com", environmentName: "GlobalCloud", environmentLabel: "全球版" },
            recentLogins: [],
            environments: [],
            isSimulation: true,
        }),
    })
    request.mockReturnValueOnce({
        id: "recents",
        promise: Promise.resolve({ items: [], storageWarning: null }),
    })
    request.mockReturnValueOnce({
        id: "models",
        promise: Promise.resolve([
            { id: "first", modelId: "db1", name: "Sales", workspaceId: "one", workspace: "团队", unavailableReason: null },
            { id: "second", modelId: "db2", name: "Sales", workspaceId: "one", workspace: "团队", unavailableReason: null },
            { id: "third", modelId: "db3", name: "Sales", workspaceId: "two", workspace: "团队", unavailableReason: null },
        ]),
    })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    const labels = wrapper.findAllComponents({ name: "ElOption" }).map((option) => option.props("label"))
    expect(labels).toEqual(["团队 · one", "团队 · two", "Sales · db1", "Sales · db2"])
    const selects = wrapper.findAllComponents({ name: "ElSelect" })
    selects[1]!.vm.$emit("update:modelValue", "second")
    await flushPromises()
    selects[0]!.vm.$emit("update:modelValue", "two")
    await flushPromises()
    expect(popup.find(".instance-card .instance-connect-button").exists()).toBe(false)
    expect(wrapper.findAllComponents({ name: "ElOption" }).map((option) => option.props("label"))).toEqual(["团队 · one", "团队 · two", "Sales"])
    wrapper.unmount()
    bridge.dispose()
})
it("登录期间锁定来源操作, 取消发送独立请求并等待原请求终态", async () => {
    const { bridge, request, wrapper, popup } = await setup()
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "login-pending",
        promise: new Promise((_, fail) => {
            reject = fail
        }),
    })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    expect(popup.find(".el-dialog__headerbtn").exists()).toBe(false)
    expect(popup.findAll("button").some((button) => button.text() === "关闭")).toBe(false)
    await operationCancel().trigger("click")
    expect(request).toHaveBeenCalledWith("request.cancel", { requestId: "login-pending" })
    reject(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(Array.from(document.querySelectorAll(".el-message--info")).at(-1)?.textContent).toContain("操作已取消")
    expect(wrapper.emitted("connected")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})
it("退出服务账号断开当前 Service, 不断开 PBIP", async () => {
    const { bridge, wrapper, popup } = await setup()
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    await wrapper.setProps({ activeProject: { ...previewProject, connection: { kind: "powerbi-service", label: "Power BI Service" } } })
    await popup
        .findAll("button")
        .find((button) => button.text() === "退出登录")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.emitted("disconnected")).toHaveLength(1)
    expect(popup.find(".account-name").exists()).toBe(false)
    expect(popup.text()).toContain("最近登录")
    await wrapper.setProps({ activeProject: previewProject })
    await popup
        .findAll("button")
        .find((button) => button.text() === "登录")!
        .trigger("click")
    await flushPromises()
    await popup
        .findAll("button")
        .find((button) => button.text() === "退出登录")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.emitted("disconnected")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})
it("连接页直接展示三种连接来源且没有弹窗与顶栏连接按钮, Desktop 文件连接后保留面板并刷新最近文件", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App, { attachTo: document.body })
    const popup = wrapper
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    expect(wrapper.get(".header-actions").text()).toContain("未连接")
    expect(wrapper.get(".header-actions").text()).not.toContain("本地文件")
    expect(wrapper.findAll(".connection-center")).toHaveLength(1)
    expect(wrapper.findAll('[role="tab"]').map((tab) => tab.text())).toEqual(["Power BI Desktop", "Power BI Service", "SSAS / XMLA"])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.find(".hero").exists()).toBe(false)
    expect(wrapper.findAll(".header-actions button").some((button) => button.text() === "连接模型" || button.text() === "切换模型")).toBe(false)
    await popup
        .findAll(".connection-center button")
        .find((button) => button.text() === "选择 PBIP / PBIX 文件")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.get(".header-actions").text()).toContain("Power BI Desktop")
    expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
    expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    expect(wrapper.get(".recent-projects").text()).toContain("示例工程 (模拟)")
    expect(wrapper.findAll(".header-actions button").some((button) => button.text() === "连接模型" || button.text() === "切换模型")).toBe(false)
    expect(wrapper.get(".header-actions").text()).not.toContain("连接远端模型")
    expect(wrapper.findAll(".connection-center")).toHaveLength(1)
    expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
    expect(wrapper.find(".hero").exists()).toBe(false)
    expect(wrapper.find(".stats-grid").exists()).toBe(false)
    expect(wrapper.get(".instance-card.is-current .instance-connect-button").text()).toBe("查看模型")
    wrapper.unmount()
    vi.unstubAllGlobals()
})

/** 连接页使用公共文件入口时保留原生选择和连接阶段, 取消不提前恢复操作; 模型刷新不冒充文件打开. */
it("连接页文件选择复用分阶段状态与独立取消, 刷新模型不点亮文件选择动画", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    const bridge = wrapper.findComponent(PowerBiPanel).props("bridge") as BridgeClient
    const request = vi.spyOn(bridge, "request")
    try {
        let reject!: (error: Error) => void
        request.mockReturnValueOnce({
            id: "connections-file-pending",
            promise: new Promise((_, fail) => {
                reject = fail
            }),
        })
        let file = wrapper.get(".content .desktop-file-button")
        await file.trigger("click")
        expect(request).toHaveBeenLastCalledWith("project.open", {}, undefined, expect.any(Function))
        expect(operationMessage().text()).toContain("原生窗口选择 PBIP 或 PBIX")
        expect(file.classes()).toContain("is-loading")
        request.mock.calls.at(-1)?.[3]?.("connecting")
        await flushPromises()
        expect(operationMessage().text()).toContain("正在通过 Power BI Desktop")
        const cancel = operationCancel()
        await cancel.trigger("click")
        expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "connections-file-pending" })
        expect(file.attributes("disabled")).toBeDefined()
        expect(cancel.attributes("disabled")).toBeDefined()
        reject(new BridgeError("CANCELLED", "操作已取消"))
        await flushPromises()
        expect(document.querySelector(".operation-message .operation-feedback") !== null).toBe(false)
        expect(file.classes()).not.toContain("is-loading")
        expect(wrapper.get(".header-actions").text()).toContain("未连接")
        await file.trigger("click")
        await flushPromises()
        // 成功连接会导航并卸载连接页; 返回后读取当前控件, 不检查已经卸载的旧按钮.
        expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        file = wrapper.get(".content .desktop-file-button")
        expect(file.element.isConnected).toBe(true)
        let resolve!: (snapshot: typeof previewProject) => void
        request.mockReturnValueOnce({
            id: "connections-inspect-pending",
            promise: new Promise((accept) => {
                resolve = accept
            }),
        })
        await wrapper
            .findAll(".header-actions button")
            .find((button) => button.text() === "刷新模型")!
            .trigger("click")
        expect(request).toHaveBeenLastCalledWith("project.inspect", {})
        expect(file.classes()).not.toContain("is-loading")
        expect(file.attributes("disabled")).toBeDefined()
        resolve({ ...previewProject, connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "mock-desktop-instance-1000" } })
        await flushPromises()
        expect(wrapper.get(".header-actions").text()).toContain("Power BI Desktop")
    } finally {
        wrapper.unmount()
        request.mockRestore()
        vi.unstubAllGlobals()
    }
})

/** Service 当前卡片仅使用宿主目录句柄匹配, 同名模型和其他来源不能伪装当前连接. */
it.each(["zh-cn", "en-us"] as const)("%s Service 单行卡片按目录身份查看当前模型, 未知计数不显示零且待保存禁用操作", async (locale) => {
    const { bridge, request, wrapper, popup } = await serviceReady()
    setLocale(locale)
    const active: ProjectSnapshot = {
        ...previewProject,
        model: { ...previewProject.model, name: "销售模型 (模拟)" },
        connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "preview-service-model" },
        summary: { compatibilityLevel: null, tableCount: 8, measureCount: 19, reportPageCount: null },
    }
    await wrapper.setProps({ activeProject: active })
    const card = popup.get(".instance-card.is-current")
    expect(card.get(".instance-connect-button").text()).toBe(locale === "zh-cn" ? "查看模型" : "View model")
    expect(card.get('[data-stat="tableCount"]').text()).toContain("8")
    expect(card.get('[data-stat="measureCount"]').text()).toContain("19")
    expect(card.get('[data-stat="reportPageCount"] dd').text()).toBe("0")
    const before = request.mock.calls.length
    await card.get("button").trigger("click")
    expect(request.mock.calls).toHaveLength(before)
    expect(wrapper.emitted("viewModel")).toEqual([[]])
    await wrapper.setProps({ disabled: true, activeProject: { ...active, hasPendingChanges: true } })
    expect(card.get("button").attributes("disabled")).toBeDefined()
    await card.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toHaveLength(1)
    expect(request.mock.calls).toHaveLength(before)
    await wrapper.setProps({ disabled: false, activeProject: { ...active, connection: { ...active.connection!, modelId: "same-name-other-id" } } })
    expect(popup.findAll(".instance-card")).toHaveLength(2)
    expect(popup.get(".current-model-card").classes()).toContain("is-current")
    const selected = popup.findAll(".instance-card").find((item) => !item.classes().includes("is-current"))!
    expect(selected.get("button").text()).toBe(locale === "zh-cn" ? "切换到此模型" : "Switch to this model")
    expect(selected.get('[data-stat="tableCount"] dd').text()).toBe("-")
    await wrapper.setProps({ activeProject: { ...active, connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "preview-service-model" } } })
    expect(popup.find(".instance-card.is-current").exists()).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

/** SSAS 不尝试发现或重连当前服务器, 当前模型与原生服务器入口共享来源锁. */
it.each(["zh-cn", "en-us"] as const)("%s SSAS 当前模型卡片展示权威统计并只导航, 保存未完和在途请求禁止查看", async (locale) => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, {
        props: {
            bridge,
            activeProject: {
                ...previewProject,
                connection: { kind: "ssas-xmla", label: "SSAS / XMLA" },
                summary: { compatibilityLevel: 1600, tableCount: 0, measureCount: 3, reportPageCount: null },
            },
        },
    })
    await flushPromises()
    setLocale(locale)
    await flushPromises()
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "SSAS / XMLA")!
        .trigger("click")
    await flushPromises()
    const current = wrapper.get(".current-model-card")
    expect(current.get('[data-stat="tableCount"] dd').text()).toBe("0")
    expect(current.get('[data-stat="reportPageCount"] dd').text()).toBe("0")
    const before = request.mock.calls.length
    await current.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toEqual([[]])
    expect(request.mock.calls).toHaveLength(before)
    await wrapper.setProps({ disabled: true })
    await current.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toHaveLength(1)
    await wrapper.setProps({ disabled: false })
    let finish!: (snapshot: ProjectSnapshot) => void
    request.mockReturnValueOnce({ id: "remote-held", promise: new Promise((resolve) => (finish = resolve)) })
    await wrapper.get(`input[aria-label="${locale === "zh-cn" ? "服务器" : "Server"}"]`).setValue("ssas.example")
    await wrapper.get(`input[aria-label="${locale === "zh-cn" ? "数据库 / 模型" : "Database / model"}"]`).setValue("demo")
    await wrapper
        .findAll("button")
        .find((button) => button.text() === (locale === "zh-cn" ? "连接" : "Connect"))!
        .trigger("click")
    expect(current.get("button").attributes("disabled")).toBeDefined()
    await current.get("button").trigger("click")
    expect(wrapper.emitted("viewModel")).toHaveLength(1)
    finish(wrapper.props("activeProject")!)
    await flushPromises()
    expect(current.get("button").attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})

/** 外部最近文件连接按真实来源切回 Desktop; 同来源刷新不会清空 Service 模型选择. */
it("外部连接来源改变同步页签, 同一来源更新保留已选 Service 模型", async () => {
    const { bridge, wrapper, popup } = await serviceReady()
    const serviceProject: ProjectSnapshot = {
        ...previewProject,
        connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "different-current-service" },
    }
    await wrapper.setProps({ activeProject: serviceProject })
    await flushPromises()
    const modelSelect = wrapper.findAllComponents({ name: "ElSelect" })[1]!
    expect(modelSelect.props("modelValue")).toBe("preview-service-model")
    await wrapper.setProps({ activeProject: { ...serviceProject, baselineId: "updated-service" } })
    await flushPromises()
    expect(modelSelect.props("modelValue")).toBe("preview-service-model")
    const desktopProject: ProjectSnapshot = {
        ...previewProject,
        connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "mock-desktop-instance-1000" },
    }
    await wrapper.setProps({ disabled: true, activeProject: desktopProject })
    await flushPromises()
    expect(
        popup
            .findAll('[role="tab"]')
            .find((tab) => tab.text() === "Power BI Desktop")!
            .attributes("aria-selected"),
    ).toBe("true")
    expect(popup.findComponent(PowerBiPanel).exists()).toBe(true)
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(popup.get(".instance-card.is-current .instance-connect-button").text()).toBe("查看模型")
    // 浏览另一来源后连接同为 Desktop 的新工作区仍回到实际来源, 同工作区刷新则保留浏览选择.
    const serverTab = popup.findAll('[role="tab"]').find((tab) => tab.text() === "SSAS / XMLA")!
    await serverTab.trigger("click")
    await wrapper.setProps({ activeProject: { ...desktopProject, baselineId: "desktop-refresh" } })
    await flushPromises()
    expect(serverTab.attributes("aria-selected")).toBe("true")
    await wrapper.setProps({ disabled: true, activeProject: { ...desktopProject, projectId: "another-desktop-workspace" } })
    await flushPromises()
    expect(popup.findComponent(PowerBiPanel).exists()).toBe(true)
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(popup.get(".instance-card.is-current .instance-connect-button").text()).toBe("查看模型")
    wrapper.unmount()
    bridge.dispose()
})
