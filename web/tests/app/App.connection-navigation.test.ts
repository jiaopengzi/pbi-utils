/**
 * FilePath    : pbi-utils\web\tests\app\App.connection-navigation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接成功排空后统一进入模型, 失败和取消保留原页面与工作区.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { ElMessage } from "element-plus"
import App from "@/app/App.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import { BridgeClient, BridgeError, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"

afterEach(() => {
    ElMessage.closeAll()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
})

/** 独立控制宿主连接终态, 不以请求提交或取消确认推定成功. */
function pendingResult<T>() {
    let resolve!: (value: T) => void
    let reject!: (error: Error) => void
    const promise = new Promise<T>((accept, fail) => {
        resolve = accept
        reject = fail
    })
    return { promise, resolve, reject }
}

/** 从真实连接 UI 准备指定来源; 仅使用显式模拟传输, 返回单次用户连接操作. */
async function prepare(source: "file" | "instance" | "service" | "remote") {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    const connection = wrapper.getComponent(ConnectionPanel)
    const bridge = connection.props("bridge") as BridgeClient
    const original: ProjectSnapshot = { ...structuredClone(previewProject), projectPath: "Original.pbip" }
    connection.vm.$emit("connected", original)
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    if (source === "service") {
        await bridge.request("powerbi.serviceSignIn", { email: "preview@example.com", environmentName: "GlobalCloud", remember: true }).promise
        await wrapper.get("#tab-service").trigger("click")
        await flushPromises()
        wrapper.findAllComponents({ name: "ElSelect" })[1]!.vm.$emit("update:modelValue", "preview-service-model")
        await flushPromises()
    } else if (source === "remote") {
        await wrapper.get("#tab-server").trigger("click")
        await flushPromises()
        await wrapper.get('input[aria-label="服务器"]').setValue("ssas.example")
        await wrapper.get('input[aria-label="数据库 / 模型"]').setValue("demo")
    }
    const method =
        source === "file"
            ? "project.open"
            : source === "instance"
              ? "project.connectLocal"
              : source === "service"
                ? "project.connectService"
                : "project.connectRemote"
    const action = () =>
        (source === "file"
            ? wrapper.get(".desktop-file-button")
            : source === "instance"
              ? wrapper.get(".instance-connect-button")
              : source === "service"
                ? wrapper.get(".connection-center .instance-connect-button")
                : wrapper.findAll(".remote-connection button").find((button) => button.text() === "连接")!
        ).trigger("click")
    return { wrapper, bridge, original, method, action }
}

it.each(["file", "instance", "service", "remote"] as const)("%s 连接成功后自动进入模型并释放导航锁", async (source) => {
    const { wrapper, bridge, method, action } = await prepare(source)
    try {
        const result = pendingResult<ProjectSnapshot>()
        const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "connect-pending", promise: result.promise })
        await action()
        await flushPromises()
        expect(request.mock.calls[0]?.[0]).toBe(method)
        expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
        const snapshot: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectId: `connected-${source}`,
            projectPath: `model://${source}`,
            connection: { kind: source === "service" ? "powerbi-service" : source === "remote" ? "ssas-xmla" : "desktop", label: source },
        }
        result.resolve(snapshot)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
        expect(wrapper.getComponent(ModelPanel).props("project")).toEqual(snapshot)
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(wrapper.findAll("nav button").every((button) => button.attributes("disabled") === undefined)).toBe(true)
        expect(request.mock.calls.filter(([name]) => name === method)).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it.each(["CONNECTION_FAILED", "CANCELLED"])("连接 %s 保留原页面和模型, 不触发成功导航", async (code) => {
    const { wrapper, bridge, action } = await prepare("file")
    try {
        vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "connect-failed", promise: Promise.reject(new BridgeError(code, code)) })
        await action()
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".header-actions").text()).toContain("Original.pbip")
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
    } finally {
        wrapper.unmount()
    }
})

it("远端连接成功仍等待一次性上下文排空, 再进入模型且不遗留 busy", async () => {
    const { wrapper, bridge, action } = await prepare("remote")
    try {
        const connection = pendingResult<ProjectSnapshot>()
        const context = pendingResult<{ formId: string; recent: [] }>()
        const request = vi.spyOn(bridge, "request")
        request.mockReturnValueOnce({ id: "connect-remote", promise: connection.promise })
        request.mockReturnValueOnce({ id: "renew-context", promise: context.promise })
        await action()
        connection.resolve({
            ...structuredClone(previewProject),
            projectId: "remote-complete",
            projectPath: "model://remote",
            connection: { kind: "ssas-xmla", label: "SSAS / XMLA" },
        })
        await flushPromises()
        expect(request).toHaveBeenLastCalledWith("remote.form", {})
        expect(wrapper.get('nav button[aria-label="连接"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("true")
        context.resolve({ formId: "next-context", recent: [] })
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="模型"]').attributes("aria-current")).toBe("page")
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
        expect(request.mock.calls.filter(([name]) => name === "request.cancel")).toHaveLength(0)
    } finally {
        wrapper.unmount()
    }
})

it("Desktop 保存成功与日期刷新警告由原操作提示一次, App 不在正文或浮动消息重放", async () => {
    const { wrapper, bridge, action } = await prepare("file")
    try {
        const snapshot: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://desktop",
            connection: { kind: "desktop", label: "Power BI Desktop" },
        }
        vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "connected-desktop", promise: Promise.resolve(snapshot) })
        await action()
        await flushPromises()
        ElMessage.closeAll()
        const saved = "当前连接模型已更新；请在 Power BI Desktop 保存报表。"
        const warning = "日期表数据尚未刷新；请在 Power BI Desktop 显式刷新对应日期表。"
        ElMessage({ message: saved, type: "success" })
        ElMessage({ message: warning, type: "warning" })
        wrapper.getComponent(ModelPanel).vm.$emit("updated", {
            ...snapshot,
            warnings: [warning],
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: saved },
        })
        await flushPromises()
        const messages = Array.from(document.querySelectorAll(".el-message")).map((message) => message.textContent)
        expect(messages.filter((message) => message === saved)).toHaveLength(1)
        expect(messages.filter((message) => message === warning)).toHaveLength(1)
        expect(wrapper.text()).not.toContain(saved)
        expect(wrapper.text()).not.toContain(warning)
        expect(wrapper.find(".notice").exists()).toBe(false)
    } finally {
        wrapper.unmount()
    }
})
