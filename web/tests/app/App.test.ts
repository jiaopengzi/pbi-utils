/**
 * FilePath    : pbi-utils\web\tests\app\App.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接页连接入口、最近文件与语言偏好边界, 以及设置排序和按需读取.
 */

import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import App from "@/app/App.vue"
import { MockTransport } from "@/bridge/mock"
import { BridgeClient, BridgeError, type UiSettings } from "@/bridge/client"
import { setLocale } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import RecentConnectionsPanel from "@/app/RecentConnectionsPanel.vue"
import { previewProject } from "@/bridge/preview"
import { ElSelect, ElMessage } from "element-plus"

afterEach(() => {
    ElMessage.closeAll()
    vi.unstubAllGlobals()
})

it("连接页仅保留大标题, 移除眉题说明和重复当前模型描述", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    await flushPromises()
    expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
    expect(wrapper.find('nav button[aria-label="AI"]').exists()).toBe(false)
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    expect(wrapper.find(".connections-content").exists()).toBe(true)
    expect(wrapper.find(".connections-content > .eyebrow").exists()).toBe(false)
    expect(wrapper.find(".connections-content > .intro").exists()).toBe(false)
    expect(wrapper.find(".connection-center > h3").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("连接成功后替换当前模型")
    expect(wrapper.find(".connection-source-tabs").exists()).toBe(true)
    wrapper.unmount()
})

it.each(["success", "failure"] as const)("确认删除本地最近记录 %s 时保留当前模型并等待宿主结果", async (outcome) => {
    const transport = new RecentFilesTransport()
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    await wrapper.get(".recent-project-open").trigger("click")
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    const bridge = wrapper.findComponent(ConnectionPanel).props("bridge") as BridgeClient
    const request = vi.spyOn(bridge, "request")
    let resolve!: (value: { deleted: boolean }) => void
    let reject!: (reason: Error) => void
    request.mockReturnValueOnce({
        id: "delete-pending",
        promise: new Promise((done, fail) => {
            resolve = done
            reject = fail
        }),
    })
    const recent = wrapper.findComponent(RecentConnectionsPanel)
    recent.vm.$emit("confirming", true)
    await flushPromises()
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    expect(wrapper.findAllComponents({ name: "ElTabPane" }).every((tab) => tab.props("disabled"))).toBe(true)
    recent.vm.$emit("confirming", false)
    recent.vm.$emit("remove", "recent-pbip")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("project.deleteRecent", { id: "recent-pbip" })
    expect(wrapper.findAll(".recent-project-open")).toHaveLength(2)
    wrapper.findComponent(PowerBiPanel).vm.$emit("busy", false)
    await flushPromises()
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    if (outcome === "success") {
        request.mockReturnValueOnce({ id: "recent-readback", promise: Promise.resolve(transport.records.slice(1)) })
        resolve({ deleted: true })
    } else reject(new BridgeError("STORE_FAILED", "记录删除失败"))
    await flushPromises()
    expect(wrapper.findAll(".recent-project-open")).toHaveLength(outcome === "success" ? 1 : 2)
    expect(wrapper.get(".header-actions").text()).toContain("Sales.pbip")
    if (outcome === "failure") expect(document.querySelector(".el-message--error")?.textContent).toBe("记录删除失败")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("project.disconnect")
    wrapper.unmount()
})

it("未连接业务页通过连接页返回三种连接入口, 不打开连接弹窗", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get('nav button[aria-label="模型"]').trigger("click")
    await flushPromises()
    expect(wrapper.find(".connection-center").exists()).toBe(false)
    await wrapper.get(".empty button").trigger("click")
    await flushPromises()
    expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
    expect(wrapper.findAll(".connection-center")).toHaveLength(1)
    expect(wrapper.findAll('[role="tab"]').map((tab) => tab.text())).toEqual(["Power BI Desktop", "Power BI Service", "SSAS / XMLA"])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    wrapper.unmount()
})

/** 连接后的单行实例卡片承担模型查看入口, 查看只切换页面并保留当前连接及统计. */
it.each(["zh-cn", "en-us"] as const)("%s 连接页连接后移除重复信息模块, 点击查看模型只导航不重新连接", async (locale) => {
    const transport = new MockTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale, showRecentFiles: true, daxFormatStyle: "long" }).promise
    preferences.dispose()
    const calls = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "连接" : "Connections"}"]`).trigger("click")
    await flushPromises()
    await wrapper.get(".desktop-file-button").trigger("click")
    await flushPromises()
    expect(wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "模型" : "Model"}"]`).attributes("aria-current")).toBe("page")
    await wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "连接" : "Connections"}"]`).trigger("click")
    await flushPromises()
    expect(wrapper.find(".hero").exists()).toBe(false)
    expect(wrapper.find(".stats-grid").exists()).toBe(false)
    expect(wrapper.find(".recent-files-panel").exists()).toBe(true)
    const current = wrapper.get(".instance-card.is-current")
    expect(current.findAll(".instance-stat")).toHaveLength(4)
    expect(current.get(".instance-connect-button").text()).toBe(locale === "zh-cn" ? "查看模型" : "View model")
    const connectionsBefore = calls.mock.calls.filter(([message]) =>
        /^(project\.open|project\.connectLocal|project\.connectRemote|project\.connectService)$/.test((message as { method: string }).method),
    ).length
    await current.get(".instance-connect-button").trigger("click")
    await flushPromises()
    expect(wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "模型" : "Model"}"]`).attributes("aria-current")).toBe("page")
    expect(wrapper.findComponent(ModelPanel).exists()).toBe(true)
    expect(
        calls.mock.calls.filter(([message]) =>
            /^(project\.open|project\.connectLocal|project\.connectRemote|project\.connectService)$/.test((message as { method: string }).method),
        ),
    ).toHaveLength(connectionsBefore)
    await wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "连接" : "Connections"}"]`).trigger("click")
    await flushPromises()
    expect(wrapper.get(".instance-card.is-current .instance-connect-button").text()).toBe(locale === "zh-cn" ? "查看模型" : "View model")
    wrapper.unmount()
})

it("连接页关闭最近文件等待持久化成功才隐藏, 业务扫描结束不能提前解锁偏好保存, 设置可重新开启并跨重挂载保留", async () => {
    const transport = new MockTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "short" }).promise
    preferences.dispose()
    vi.stubGlobal("chrome", { webview: transport })
    let wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    const bridge = wrapper.findComponent(ConnectionPanel).props("bridge") as BridgeClient
    const request = vi.spyOn(bridge, "request")
    let finish!: (value: UiSettings) => void
    request.mockReturnValueOnce({ id: "pending-recent", promise: new Promise((resolve) => (finish = resolve)) })
    wrapper.get(".recent-files-panel").getComponent({ name: "ElSwitch" }).vm.$emit("change", false)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { showRecentFiles: false })
    expect(wrapper.find(".recent-files-panel").exists()).toBe(true)
    expect(useI18n().showRecentFiles.value).toBe(true)
    wrapper.findComponent(PowerBiPanel).vm.$emit("busy", false)
    await flushPromises()
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    // 让模拟宿主真正保存同一偏好后再放行组件请求, 后续重挂载仍读取同一来源.
    const saved = await bridge.request("ui.saveSettings", { showRecentFiles: false }).promise
    finish(saved)
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    expect(useI18n().daxFormatStyle.value).toBe("short")
    wrapper.unmount()
    wrapper = mount(App)
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    await wrapper.get('nav button[aria-label="设置"]').trigger("click")
    await flushPromises()
    const toggle = wrapper.get(".ui-settings-panel").getComponent({ name: "ElSwitch" })
    expect(toggle.props("modelValue")).toBe(false)
    toggle.vm.$emit("update:modelValue", true)
    toggle.vm.$emit("change", true)
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(true)
    wrapper.unmount()
})

it("连接页关闭最近文件保存失败时保留模块和语言, 不删除已有文件记录", async () => {
    const transport = new MockTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale: "en-us", showRecentFiles: true, daxFormatStyle: "long" }).promise
    preferences.dispose()
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="Connections"]`).trigger("click")
    await flushPromises()
    const bridge = wrapper.findComponent(ConnectionPanel).props("bridge") as BridgeClient
    const request = vi
        .spyOn(bridge, "request")
        .mockReturnValueOnce({ id: "failed-recent", promise: Promise.reject(new BridgeError("SAVE_FAILED", "fixture save failure")) })
    wrapper.get(".recent-files-panel").getComponent({ name: "ElSwitch" }).vm.$emit("change", false)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { showRecentFiles: false })
    expect(wrapper.get(".recent-files-panel").text()).toContain("Recently opened files")
    expect(useI18n().locale.value).toBe("en-us")
    expect(useI18n().showRecentFiles.value).toBe(true)
    expect(document.querySelector(".el-message--error")?.textContent).toBe("fixture save failure")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("project.recent")
    wrapper.unmount()
})

it("统一设置切换全部公共导航, 重挂载读取宿主语言, 示例无独立语言入口", async () => {
    const transport = new MockTransport()
    const requests = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    let wrapper = mount(App)
    try {
        await flushPromises()
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        await flushPromises()
        await wrapper.get(".ui-settings-panel").getComponent(ElSelect).setValue("en-us")
        wrapper.get(".ui-settings-panel").getComponent(ElSelect).vm.$emit("change", "en-us")
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="Settings"]').text()).toContain("Settings")
        expect(wrapper.text()).toContain("Business logs")
        expect(wrapper.text()).not.toContain("Model saving")
        expect(wrapper.text()).not.toContain("业务日志")
        await wrapper.get('nav button[aria-label="New"]').trigger("click")
        await flushPromises()
        expect(wrapper.find(".scaffold-panel").exists()).toBe(true)
        expect(wrapper.get('nav button[aria-label="Sample"]').attributes("disabled")).toBeUndefined()
        await wrapper.get('nav button[aria-label="Sample"]').trigger("click")
        await flushPromises()
        expect(wrapper.text()).toContain("Generate Sample Data")
        expect(wrapper.find(".sample-language").exists()).toBe(false)
        expect(wrapper.find(".sample-panel .el-select").exists()).toBe(false)
        wrapper.unmount()
        setLocale("zh-cn")
        wrapper = mount(App)
        await flushPromises()
        expect(wrapper.find('nav button[aria-label="Settings"]').exists()).toBe(true)
        await wrapper.get('nav button[aria-label="Settings"]').trigger("click")
        await flushPromises()
        expect(wrapper.text()).toContain("Local MCP")
        expect(wrapper.find(".logging-panel").exists()).toBe(true)
        expect(requests.mock.calls.map(([value]) => (value as { method: string }).method)).toContain("ui.saveSettings")
    } finally {
        wrapper.unmount()
    }
})

it("设置顶部展示 MCP、底部展示本地数据, 保留运行环境且每次进入自动检测", async () => {
    const transport = new MockTransport()
    const requests = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        await flushPromises()
        expect(wrapper.findAll(".settings-section").map((section) => section.attributes("aria-label"))).toEqual([
            "本地 MCP",
            "常规",
            "软件更新",
            "运行环境",
            "本地数据",
        ])
        expect(wrapper.findAll(".environment-descriptions .el-descriptions__label")).toHaveLength(7)
        expect(wrapper.text()).toContain("浏览器模拟")
        expect(wrapper.find("#name").exists()).toBe(false)
        expect(wrapper.find(".environment-panel button").exists()).toBe(false)
        expect(wrapper.text()).not.toContain("Mock Agent")
        expect(wrapper.find(".logging-panel").exists()).toBe(true)
        expect(wrapper.find("#model-save-heading").exists()).toBe(false)
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        expect(wrapper.find(".environment-panel").exists()).toBe(false)
        expect(wrapper.find(".endpoint-settings").exists()).toBe(false)
        expect(wrapper.find(".logging-panel").exists()).toBe(false)
        expect(wrapper.find("#model-save-heading").exists()).toBe(false)
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        await flushPromises()
        expect(wrapper.findAll(".settings-section").map((section) => section.attributes("aria-label"))).toEqual([
            "本地 MCP",
            "常规",
            "软件更新",
            "运行环境",
            "本地数据",
        ])
        expect(requests.mock.calls.filter(([message]) => (message as { method: string }).method === "doctor")).toHaveLength(2)
    } finally {
        wrapper.unmount()
    }
})

it("启动更新检查在途仍可导航, 设置复用全局状态且离开设置不取消自动检查", async () => {
    const transport = new MockTransport()
    let listener!: (event: MessageEvent) => void
    let pendingId = ""
    const addListener = transport.addEventListener.bind(transport)
    const post = transport.postMessage.bind(transport)
    vi.spyOn(transport, "addEventListener").mockImplementation((type, handler) => {
        listener = handler
        addListener(type, handler)
    })
    const requests = vi.spyOn(transport, "postMessage").mockImplementation((value) => {
        const message = value as { id: string; method: string }
        if (message.method === "updates.check") pendingId = message.id
        else post(value)
    })
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        expect(pendingId).not.toBe("")
        const settings = wrapper.get('nav button[aria-label="设置"]')
        expect(settings.attributes("disabled")).toBeUndefined()
        await settings.trigger("click")
        await flushPromises()
        expect(wrapper.get(".software-update-panel").attributes("aria-busy")).toBe("true")
        expect(requests.mock.calls.filter(([value]) => (value as { method: string }).method === "updates.status")).toHaveLength(1)
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        expect(wrapper.find(".software-update-panel").exists()).toBe(false)
        expect(
            requests.mock.calls.some(([value]) => {
                const message = value as { method: string; params: { requestId?: string } }
                return message.method === "request.cancel" && message.params.requestId === pendingId
            }),
        ).toBe(false)
        listener(new MessageEvent("message", { data: { protocolVersion: 1, id: pendingId, result: { checkedNow: false, check: null } } }))
        await flushPromises()
    } finally {
        wrapper.unmount()
    }
})

/** 仅提供宿主文件记录与已连接快照, 文件路径不会成为网页请求参数. */
class RecentFilesTransport extends MockTransport {
    private listener?: (event: MessageEvent) => void
    holdRecent = false
    heldRequest?: { id: string; method: string; params: { id?: string } }
    readonly records = [
        { id: "recent-pbip", path: "C:/Mock/Sales.pbip", name: "Sales.pbip", openedAt: "2026-10-14T00:00:00Z" },
        { id: "recent-pbix", path: "C:/Mock/Finance.pbix", name: "Finance.pbix", openedAt: "2026-10-14T00:00:00Z" },
    ]

    /** 保存与实际客户端绑定的监听器, 其余模拟消息仍由原传输发送. */
    override addEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.listener = listener
        super.addEventListener(type, listener)
    }

    /** 模拟宿主两种文件扩展名的最近记录, 只接受受信任记录的标识. */
    override postMessage(message: unknown) {
        const { id, method, params } = message as { id: string; method: string; params: { id?: string; requestId?: string } }
        if (method === "project.openRecent" && this.holdRecent) {
            this.heldRequest = { id, method, params }
            return
        }
        if (method === "request.cancel" && this.heldRequest) {
            this.listener?.(new MessageEvent("message", { data: { protocolVersion: 1, id, result: { accepted: params.requestId === this.heldRequest.id } } }))
            return
        }
        if (method === "project.recent" || method === "project.openRecent") {
            const record = this.records.find((item) => item.id === params.id)
            this.listener?.(
                new MessageEvent("message", {
                    data: {
                        protocolVersion: 1,
                        id,
                        result:
                            method === "project.recent"
                                ? this.records
                                : {
                                      ...previewProject,
                                      projectPath: "model://recent",
                                      model: { ...previewProject.model, name: record?.name },
                                      connection: { kind: "desktop", label: "Power BI Desktop" },
                                      pages: [],
                                  },
                    },
                }),
            )
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 本地测试桥接无 targetOrigin 参数.
        super.postMessage(message)
    }

    /** 用宿主错误结束正在等待的文件连接, 取消响应本身不替代连接终态. */
    finishRecent(error: BridgeError) {
        this.listener?.(
            new MessageEvent("message", { data: { protocolVersion: 1, id: this.heldRequest?.id, error: { code: error.code, message: error.message } } }),
        )
        this.heldRequest = undefined
    }
}

/** 云端展示夹具只返回已核验的目录句柄, 与本地最近文件共用实际桥接和连接页组件. */
class SourceRecentTransport extends RecentFilesTransport {
    private cloudListener?: (event: MessageEvent) => void

    /** 只保存当前桥接监听器, 授权和其他消息仍由原模拟传输负责. */
    override addEventListener(type: "message", listener: (event: MessageEvent) => void) {
        this.cloudListener = listener
        super.addEventListener(type, listener)
    }

    /** 提供独立云端最近条目, 不携带服务器、令牌或本机路径. */
    override postMessage(message: unknown) {
        const { id, method } = message as { id: string; method: string }
        if (method === "powerbi.serviceModels") {
            this.cloudListener?.(
                new MessageEvent("message", {
                    data: {
                        protocolVersion: 1,
                        id,
                        result: [{ id: "preview-service-model", name: "Cloud sales", workspace: "Cloud workspace", unavailableReason: null }],
                    },
                }),
            )
            return
        }
        if (method === "powerbi.serviceRecentModels") {
            this.cloudListener?.(
                new MessageEvent("message", {
                    data: {
                        protocolVersion: 1,
                        id,
                        result: {
                            items: [
                                {
                                    id: "recent-cloud",
                                    modelId: "preview-service-model",
                                    name: "Cloud sales",
                                    workspace: "Cloud workspace",
                                    lastUsedAt: "2026-10-15T00:00:00Z",
                                },
                            ],
                            storageWarning: null,
                        },
                    },
                }),
            )
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 本地测试桥接无 targetOrigin 参数.
        super.postMessage(message)
    }
}

/** 来源页签决定共享模块内容, 未登录和 SSAS 不展示云记录, 设置开关对两种列表共用. */
it("连接页按来源隔离本地和云端最近记录, 仅登录后读取云记录, SSAS 不展示模块", async () => {
    const transport = new SourceRecentTransport()
    const calls = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    expect(wrapper.get(".recent-files-panel").text()).toContain("Sales.pbip")
    await wrapper.get("#tab-service").trigger("click")
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    expect(calls.mock.calls.map(([message]) => (message as { method: string }).method)).not.toContain("powerbi.serviceRecentModels")
    const bridge = wrapper.findComponent(ConnectionPanel).props("bridge") as BridgeClient
    await bridge.request("powerbi.serviceSignIn", { email: "preview@example.com", environmentName: "GlobalCloud", remember: true }).promise
    await wrapper.get("#tab-server").trigger("click")
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    await wrapper.get("#tab-service").trigger("click")
    await flushPromises()
    expect(wrapper.get(".recent-files-panel").text()).toContain("Cloud sales")
    expect(wrapper.get(".recent-files-panel").text()).toContain("Cloud workspace")
    expect(wrapper.get(".recent-files-panel").text()).not.toContain("Sales.pbip")
    await wrapper.get(".recent-project-open").trigger("click")
    await flushPromises()
    const connections = calls.mock.calls.filter(([message]) => (message as { method: string }).method === "project.connectService").length
    expect(connections).toBe(1)
    expect(wrapper.findComponent(ModelPanel).exists()).toBe(true)
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    await wrapper.get(".recent-project-open").trigger("click")
    await flushPromises()
    expect(wrapper.findComponent(ModelPanel).exists()).toBe(true)
    expect(calls.mock.calls.filter(([message]) => (message as { method: string }).method === "project.connectService")).toHaveLength(connections)
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    await wrapper.get("#tab-desktop").trigger("click")
    await flushPromises()
    expect(wrapper.get(".recent-files-panel").text()).toContain("Sales.pbip")
    expect(wrapper.get(".recent-files-panel").text()).not.toContain("Cloud sales")
    await wrapper.get("#tab-server").trigger("click")
    await flushPromises()
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    wrapper.unmount()
})

it.each(["recent-pbip", "recent-pbix"])("连接页 %s 最近文件只提交宿主记录标识, 按 Desktop 来源连接并保持记录", async (recentId) => {
    const transport = new RecentFilesTransport()
    const request = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    const record = transport.records.find((item) => item.id === recentId)!
    await wrapper
        .findAll(".recent-project-open")
        .find((button) => button.text().includes(record.name))!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith(expect.objectContaining({ method: "project.openRecent", params: { id: recentId } }))
    expect(wrapper.get(".header-actions").text()).toContain("Power BI Desktop")
    expect(wrapper.get(".header-actions").text()).toContain(record.name)
    expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    expect(wrapper.findAll(".recent-project-open")).toHaveLength(2)
    expect(wrapper.find('p[role="status"]').exists()).toBe(false)
    wrapper.unmount()
})

it("最近文件等待中可一次取消, 确认取消后继续等待原请求终态并保留此前模型和完整记录", async () => {
    const transport = new RecentFilesTransport()
    const request = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    await wrapper.findAll(".recent-project-open")[0]!.trigger("click")
    await flushPromises()
    expect(wrapper.get(".header-actions").text()).toContain("Sales.pbip")
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    transport.holdRecent = true
    await wrapper.findAll(".recent-project-open")[1]!.trigger("click")
    await flushPromises()
    expect(wrapper.find(".connection-progress").exists()).toBe(false)
    expect(document.querySelector(".operation-feedback")?.textContent).toContain("正在通过 Power BI Desktop")
    const heldId = transport.heldRequest?.id
    const cancel = new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!)
    await cancel.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith(expect.objectContaining({ method: "request.cancel", params: { requestId: heldId } }))
    expect(cancel.attributes("disabled")).toBeDefined()
    await cancel.trigger("click")
    wrapper.findComponent(PowerBiPanel).vm.$emit("busy", false)
    await flushPromises()
    expect(request.mock.calls.filter(([message]) => (message as { method: string }).method === "request.cancel")).toHaveLength(1)
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    expect(wrapper.get(".header-actions").text()).toContain("Sales.pbip")
    transport.finishRecent(new BridgeError("CANCELLED", "操作已取消"))
    await flushPromises()
    expect(wrapper.find(".connection-progress").exists()).toBe(false)
    expect(wrapper.get(".header-actions").text()).toContain("Sales.pbip")
    expect(wrapper.findAll(".recent-project-open")).toHaveLength(2)
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") === undefined)).toBe(true)
    wrapper.unmount()
})

it("等待最近文件连接时卸载页面主动取消当前请求, 不改写已保存显示偏好", async () => {
    const transport = new RecentFilesTransport()
    const request = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get(`nav button[aria-label="连接"]`).trigger("click")
    await flushPromises()
    transport.holdRecent = true
    await wrapper.findAll(".recent-project-open")[1]!.trigger("click")
    await flushPromises()
    const requestId = transport.heldRequest?.id
    wrapper.unmount()
    await flushPromises()
    expect(request).toHaveBeenCalledWith(expect.objectContaining({ method: "request.cancel", params: { requestId } }))
    expect(useI18n().showRecentFiles.value).toBe(true)
})

/** 只延迟首次语言读取, 其他操作仍经实际 bridge client 和显式 mock transport. */
class HoldingLanguageTransport extends MockTransport {
    methods: string[] = []
    held: unknown
    hold = true

    /** 在初始偏好返回前挂起响应, 验证任何业务界面都不能抢先使用默认语言. */
    override postMessage(message: unknown) {
        const method = (message as { method: string }).method
        this.methods.push(method)
        if (method === "ui.settings" && this.hold) {
            this.held = message
            this.hold = false
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 本地测试桥接无 targetOrigin 参数.
        super.postMessage(message)
    }

    /** 放行实际偏好读取, 不伪造宿主返回的语言设置. */
    release() {
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 本地测试桥接无 targetOrigin 参数.
        super.postMessage(this.held)
    }
}

/** 偏好未读完时锁定导航和示例, 读到英文后再开放页面及其语言绑定. */
it("首次语言读取未完成时禁止进入示例及生成, 放行后按已保存英文开放导航", async () => {
    const transport = new HoldingLanguageTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale: "en-us", showRecentFiles: false, daxFormatStyle: "long" }).promise
    preferences.dispose()
    setLocale("zh-cn")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    await flushPromises()
    expect(transport.methods).toContain("ui.settings")
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    expect(wrapper.find(".sample-panel").exists()).toBe(false)
    await wrapper.get('nav button[aria-label="新建"]').trigger("click")
    await flushPromises()
    expect(wrapper.find(".new-panel").exists()).toBe(false)
    expect(transport.methods).not.toContain("sample.status")
    expect(transport.methods).not.toContain("sample.generate")
    transport.release()
    await flushPromises()
    expect(document.documentElement.lang).toBe("en-us")
    expect(wrapper.find(".recent-files-panel").exists()).toBe(false)
    expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") === undefined)).toBe(true)
    await wrapper.get('nav button[aria-label="New"]').trigger("click")
    await flushPromises()
    expect(wrapper.find(".scaffold-panel").exists()).toBe(true)
    expect(wrapper.get('nav button[aria-label="Sample"]').attributes("disabled")).toBeUndefined()
    await wrapper.get('nav button[aria-label="Sample"]').trigger("click")
    await flushPromises()
    expect(wrapper.text()).toContain("Generate Sample Data")
    expect(transport.methods).toContain("sample.status")
    expect(transport.methods).not.toContain("sample.generate")
    wrapper.unmount()
})
