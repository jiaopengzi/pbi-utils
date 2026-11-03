/**
 * FilePath    : pbi-utils\web\tests\app\HostDialog.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证宿主弹窗的一次性回复、迟到结果隔离及业务确认.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient, type HostDialogRequest } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import HostDialog from "@/app/HostDialog.vue"

const dialogStub = {
    props: ["modelValue", "title", "busy"],
    template: '<section v-if="modelValue"><h2>{{title}}</h2><slot /><footer><slot name="footer" /></footer></section>',
}
const active: (() => void)[] = []
afterEach(() => {
    for (const close of active.splice(0)) close()
    setLocale("zh-cn")
})

/** 为弹窗提供真实 BridgeClient 和可控宿主事件, 不访问用户存储或服务. */
function harness(stubDialog = true) {
    let receive: (event: MessageEvent) => void = vi.fn()
    const post = vi.fn()
    const bridge = markRaw(
        new BridgeClient({
            postMessage: post,
            addEventListener: (_type, callback) => {
                receive = callback
            },
            removeEventListener: vi.fn(),
        }),
    )
    const wrapper = mount(HostDialog, { props: { bridge }, global: { stubs: stubDialog ? { UiDialog: dialogStub } : {} }, attachTo: document.body })
    active.push(() => {
        wrapper.unmount()
        bridge.dispose()
    })
    const send = (message: unknown) => receive(new MessageEvent("message", { data: { protocolVersion: 1, ...(message as object) } }))
    const open = async (dialog: Partial<HostDialogRequest> = {}) => {
        send({
            uiDialog: {
                id: "dialog-one",
                kind: "confirm",
                titleKey: "hostDialog.sampleClearTitle",
                messageKey: "hostDialog.sampleClearMessage",
                messageArgs: { directory: "C:/preview" },
                actions: ["confirm"],
                ...dialog,
            },
        })
        await flushPromises()
    }
    return { bridge, wrapper, post, send, open }
}

it("独立弹窗无需在途业务请求, 取消向宿主回复且等待同 ID 权威关闭", async () => {
    const { wrapper, post, send, open } = harness()
    await open()
    expect(wrapper.emitted("active")?.at(-1)).toEqual([true])
    const formSubmit = new Event("submit", { cancelable: true, bubbles: true })
    wrapper.element.dispatchEvent(formSubmit)
    expect(post).not.toHaveBeenCalled()
    await wrapper.findAll("footer button")[0]!.trigger("click")
    const response = post.mock.calls[0]![0]
    expect(response).toMatchObject({ method: "ui.dialogRespond", params: { dialogId: "dialog-one", action: "cancel", values: {} } })
    send({ id: response.id, result: { accepted: true } })
    await flushPromises()
    expect(wrapper.find("h2").exists()).toBe(true)
    expect(wrapper.findAll("footer button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
    send({ uiDialogClosed: { id: "dialog-one" } })
    await flushPromises()
    expect(wrapper.find("h2").exists()).toBe(false)
    expect(wrapper.emitted("active")?.at(-1)).toEqual([false])
})

it("新弹窗不会被旧回复或迟到关闭清除, 重新订阅可收到当前弹窗", async () => {
    const { wrapper, post, send, open, bridge } = harness()
    await open()
    await wrapper.findAll("footer button")[1]!.trigger("click")
    const response = post.mock.calls[0]![0]
    await open({ id: "dialog-two", titleKey: "hostDialog.sampleOverwriteTitle" })
    send({ id: response.id, result: { accepted: true } })
    send({ uiDialogClosed: { id: "dialog-one" } })
    await flushPromises()
    expect(wrapper.get("h2").text()).toBe("覆盖示例数据")
    const listener = vi.fn()
    const stop = bridge.onDialog(listener)
    expect(listener).toHaveBeenCalledWith(expect.objectContaining({ id: "dialog-two" }))
    stop()
})

it("显式交互预览直接完成手工模型编辑和保存, 不产生普通保存确认或确认回复", async () => {
    const transport = new MockTransport(true)
    const bridge = new BridgeClient(transport)
    const post = vi.spyOn(transport, "postMessage")
    const dialog = vi.fn()
    const stop = bridge.onDialog(dialog)
    try {
        const project = await bridge.request("project.inspect", {}).promise
        const measure = project.model.measures[0]!
        const edited = await bridge.request("model.edit", {
            operation: "measure.set",
            objectName: measure.name,
            property: "description",
            value: "直接编辑预览",
            table: measure.table,
            pageId: "",
        }).promise
        expect(edited.model.measures[0]?.description).toBe("直接编辑预览")
        const saved = await bridge.request("model.save", {}).promise
        expect(saved.hasPendingChanges).toBe(false)
        expect(saved.saveResult?.message).toContain("浏览器模拟保存结果")
        expect(dialog).not.toHaveBeenCalled()
        expect(post.mock.calls.map(([request]) => (request as { method: string }).method)).toEqual(["project.inspect", "model.edit", "model.save"])
    } finally {
        stop()
        bridge.dispose()
    }
})

it.each(["model.saveSettings", "model.updateSaveSettings"])("显式模拟拒绝已移除的模型保存设置方法 %s", async (method) => {
    const bridge = new BridgeClient(new MockTransport(true))
    try {
        await expect(bridge.request(method as never, {} as never).promise).rejects.toMatchObject({ code: "UNKNOWN_METHOD" })
    } finally {
        bridge.dispose()
    }
})

it("浏览器预览确认经过同一弹窗回复桥接, 取消不产生模拟业务修改", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport(true)))
    const wrapper = mount(HostDialog, { props: { bridge }, global: { stubs: { UiDialog: dialogStub } } })
    active.push(() => {
        wrapper.unmount()
        bridge.dispose()
    })
    const clear = bridge.request("sample.clear", {})
    const result = clear.promise.catch((error: unknown) => error)
    await flushPromises()
    await wrapper.findAll("footer button")[0]!.trigger("click")
    expect(await result).toMatchObject({ code: "CANCELLED" })
})

it("完整显示实际修改前后和保存统计, 不执行用户内容", async () => {
    const { wrapper, open } = harness()
    await open({
        id: "tool-model-2",
        kind: "tool",
        titleKey: "hostDialog.toolTitle",
        messageKey: "hostDialog.toolMessage",
        data: {
            operation: "retry",
            detail: { created: 2, updated: 3, deleted: 1 },
            changes: [
                { objectName: "Sales.Amount", property: "expression", before: "SUM ( Sales[Amount] )", after: "SUM ( Sales[Amount] ) * 2" },
                { objectName: "Sales.Legacy", property: "description", before: "<script>untrusted()</script>", after: null },
            ],
        },
    })
    expect(wrapper.text()).toContain("继续保存")
    expect(wrapper.text()).toContain("删除度量值1")
    expect(wrapper.text()).toContain("SUM ( Sales[Amount] ) * 2")
    expect(wrapper.text()).toContain("<script>untrusted()</script>")
    expect(wrapper.find("script").exists()).toBe(false)
    expect(wrapper.text()).toContain("未设置")
    expect(wrapper.text()).toContain("请在 Power BI Desktop 中手动保存报表文件")
    expect(wrapper.text()).toContain("确认仅授权本次提案")
    expect(wrapper.text()).toContain("不自动备份或刷新数据")
    expect(wrapper.text()).not.toContain("保存确认偏好")
})

/** 确认对话框逐字展示宿主最终映射, 语言切换不猜测或重编号旧列. */
it.each(["zh-cn", "en-us"] as const)("工具日期精确授权展示真实保留和新增列（%s）", async (locale) => {
    setLocale(locale)
    const { wrapper, open } = harness()
    await open({
        kind: "tool",
        titleKey: "hostDialog.toolTitle",
        messageKey: "hostDialog.toolMessage",
        data: {
            detail: {
                calendarFields: "Dates: 日期, C01_Dates, Year, C13_Month",
                calendarRetainedFields: "Dates: 日期, C01_Dates, Year",
                calendarAddedFields: "Dates: C13_Month",
            },
        },
    })
    expect(wrapper.findAll("dd").map((node) => node.text())).toEqual([
        "Dates: 日期, C01_Dates, Year, C13_Month",
        "Dates: 日期, C01_Dates, Year",
        "Dates: C13_Month",
    ])
    expect(wrapper.text()).toContain(locale === "zh-cn" ? "已有字段的最终名称" : "Final names of existing fields")
    expect(wrapper.text()).toContain(locale === "zh-cn" ? "请在 Power BI Desktop 中手动保存报表文件" : "Save the report file manually in Power BI Desktop")
    expect(wrapper.text()).not.toContain("C28_Year")
    expect(wrapper.text()).not.toContain("hostDialog.calendar")
})
