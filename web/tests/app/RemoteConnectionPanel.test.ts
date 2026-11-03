/**
 * FilePath    : pbi-utils\web\tests\app\RemoteConnectionPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证远端平铺表单上下文、秘密清理、完整目标绑定、取消排空和记录操作隔离.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElMessage, ElSelect } from "element-plus"
import { BridgeClient, BridgeError, type RemoteConnectionForm } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"
import RemoteConnectionPanel from "@/app/RemoteConnectionPanel.vue"

const closes: (() => void)[] = []
afterEach(() => {
    for (const close of closes.splice(0)) close()
    ElMessage.closeAll()
    setLocale("zh-cn")
    vi.restoreAllMocks()
})
const saved: RemoteConnectionForm = {
    formId: "form-one",
    recent: [{ id: "saved-one", server: "ssas.example", database: "demo", authenticationMode: "windows", userName: "domain\\user", hasSavedPassword: true }],
}
/** 初始化使用非秘密摘要, 所有网络和用户存储均保持模拟. */
async function harness(initial = saved) {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    request.mockReturnValueOnce({ id: "form-request", promise: Promise.resolve(structuredClone(initial)) })
    const wrapper = mount(RemoteConnectionPanel, { props: { bridge }, attachTo: document.body })
    closes.push(() => {
        wrapper.unmount()
        bridge.dispose()
    })
    await flushPromises()
    const connect = () => wrapper.findAll("button").find((button) => button.text() === "连接")!
    return { bridge, request, wrapper, connect }
}
/** 控制提交终态, 不让取消确认替代原请求完成. */
function pending<T>() {
    let resolve!: (value: T) => void
    let reject!: (error: Error) => void
    const promise = new Promise<T>((accept, fail) => {
        resolve = accept
        reject = fail
    })
    return { promise, resolve, reject }
}

it("首次读取后直接平铺, 空闲无桥接请求或导航锁; Enter 不隐式连接", async () => {
    const { wrapper, request, connect } = await harness()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(request).toHaveBeenCalledExactlyOnceWith("remote.form", {})
    expect(wrapper.emitted("busy")).toEqual([[true], [false]])
    expect(connect().attributes("disabled")).toBeUndefined()
    expect((wrapper.get('input[type="password"]').element as HTMLInputElement).value).toBe("")
    const submit = new Event("submit", { cancelable: true, bubbles: true })
    wrapper.get("form").element.dispatchEvent(submit)
    expect(submit.defaultPrevented).toBe(true)
    expect(request).toHaveBeenCalledTimes(1)
})
it.each(["服务器", "数据库 / 模型", "Windows 账号"])("修改%s解除保存密码的完整目标绑定并清临时输入", async (label) => {
    const { wrapper, connect } = await harness()
    const password = wrapper.get('input[type="password"]')
    expect(password.attributes("placeholder")).toContain("已保存密码")
    await password.setValue("temporary-only-password")
    await wrapper.get(`input[aria-label="${label}"]`).setValue("changed")
    expect((password.element as HTMLInputElement).value).toBe("")
    expect(password.attributes("placeholder")).not.toContain("已保存密码")
    expect(connect().attributes("disabled")).toBeDefined()
    await password.setValue("temporary-only-password")
    expect(connect().attributes("disabled")).toBeUndefined()
})
it("XMLA 强制当前身份并清账号密码, 关闭记住连接禁用保存密码", async () => {
    const { wrapper } = await harness()
    await wrapper.get('input[type="password"]').setValue("temporary-only-password")
    await wrapper.get('input[aria-label="服务器"]').setValue("powerbi://api.powerbi.com/v1.0/myorg/workspace")
    expect(wrapper.findAllComponents(ElSelect)[1]!.props("modelValue")).toBe("current")
    expect((wrapper.get('input[aria-label="Windows 账号"]').element as HTMLInputElement).value).toBe("")
    expect((wrapper.get('input[type="password"]').element as HTMLInputElement).value).toBe("")
    expect(wrapper.get('input[type="password"]').attributes("disabled")).toBeDefined()
    await wrapper.findAll('input[type="checkbox"]')[0]!.setValue(false)
    expect((wrapper.findAll('input[type="checkbox"]')[1]!.element as HTMLInputElement).checked).toBe(false)
})
it.each(["failure", "cancelled"])("%s 等待原连接终态并刷新上下文, 不重放连接或保存秘密", async (outcome) => {
    const { wrapper, request, connect } = await harness()
    const storage = vi.spyOn(Storage.prototype, "setItem")
    const held = pending<typeof previewProject>()
    request.mockReturnValueOnce({ id: "connect-one", promise: held.promise })
    await wrapper.get('input[type="password"]').setValue("temporary-only-password")
    await connect().trigger("click")
    expect(request).toHaveBeenLastCalledWith("project.connectRemote", {
        formId: "form-one",
        values: expect.objectContaining({ password: "temporary-only-password", recentId: "saved-one", remember: true, rememberPassword: true }),
    })
    expect((wrapper.get('input[type="password"]').element as HTMLInputElement).value).toBe("")
    const row = wrapper.get(".remote-connection__recent").element
    if (outcome === "cancelled") {
        const cancel = new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!)
        await cancel.trigger("click")
        expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "connect-one" })
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(connect().attributes("disabled")).toBeDefined()
        await cancel.trigger("click")
        expect(request.mock.calls.filter(([method]) => method === "request.cancel")).toHaveLength(1)
    }
    request.mockReturnValueOnce({ id: "form-refresh", promise: Promise.resolve({ ...saved, formId: "form-two" }) })
    held.reject(new BridgeError(outcome === "cancelled" ? "CANCELLED" : "CONNECTION_FAILED", "模拟终态"))
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("remote.form", {})
    expect(wrapper.get(".remote-connection__recent").element).toBe(row)
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    expect(request.mock.calls.filter(([method]) => method === "project.connectRemote")).toHaveLength(1)
    expect(wrapper.emitted("connected")).toBeUndefined()
    expect((wrapper.get('input[aria-label="服务器"]').element as HTMLInputElement).value).toBe("ssas.example")
    expect(storage).not.toHaveBeenCalled()
})
it.each(["delete", "clearPassword"] as const)("%s 只发送当前上下文与展示记录 ID, 清秘密并使用新上下文", async (action) => {
    const { wrapper, request, connect } = await harness()
    await wrapper.get('input[type="password"]').setValue("temporary-only-password")
    const result = { ...saved, formId: "form-two", recent: action === "delete" ? [] : saved.recent.map((item) => ({ ...item, hasSavedPassword: false })) }
    request.mockReturnValueOnce({ id: "mutate-one", promise: Promise.resolve(result) })
    await wrapper
        .findAll("button")
        .find((button) => button.text() === (action === "delete" ? "删除记录" : "清除密码"))!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("remote.mutate", { formId: "form-one", action, recordId: "saved-one" })
    expect((wrapper.get('input[type="password"]').element as HTMLInputElement).value).toBe("")
    expect((wrapper.get('input[aria-label="服务器"]').element as HTMLInputElement).value).toBe("ssas.example")
    expect(connect().attributes("disabled")).toBeDefined()
})
it("离开平铺页立即清除状态与输入节点中的秘密", async () => {
    const { wrapper } = await harness()
    const password = wrapper.get('input[type="password"]')
    await password.setValue("temporary-only-password")
    wrapper.unmount()
    expect((password.element as HTMLInputElement).value).toBe("")
})

it.each(["mutate", "connect"] as const)("%s 消费上下文后的失败允许显式重读, 不重放业务请求", async (kind) => {
    const { wrapper, request, connect } = await harness()
    if (kind === "mutate") {
        request.mockReturnValueOnce({ id: "mutate-failed", promise: Promise.reject(new BridgeError("STORAGE_CHANGED", "记录已改变")) })
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "清除密码")!
            .trigger("click")
    } else {
        request.mockReturnValueOnce({ id: "connect-failed", promise: Promise.reject(new BridgeError("CONNECTION_FAILED", "连接失败")) })
        request.mockReturnValueOnce({ id: "reload-failed", promise: Promise.reject(new BridgeError("READ_FAILED", "读取失败")) })
        await connect().trigger("click")
    }
    await flushPromises()
    expect(connect().attributes("disabled")).toBeDefined()
    expect(
        wrapper
            .findAll("button")
            .filter((button) => ["删除记录", "清除密码"].includes(button.text()))
            .every((button) => button.attributes("disabled") !== undefined),
    ).toBe(true)
    const calls = request.mock.calls.filter(([method]) => method === "project.connectRemote" || method === "remote.mutate").length
    request.mockReturnValueOnce({ id: "reload-explicit", promise: Promise.resolve({ ...saved, formId: "form-recovered" }) })
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "重新读取连接配置")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("remote.form", {})
    expect(request.mock.calls.filter(([method]) => method === "project.connectRemote" || method === "remote.mutate")).toHaveLength(calls)
    expect((wrapper.get('input[aria-label="服务器"]').element as HTMLInputElement).value).toBe("ssas.example")
    expect(connect().attributes("disabled")).toBeUndefined()
})
