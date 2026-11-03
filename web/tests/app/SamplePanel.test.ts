/**
 * FilePath    : pbi-utils\web\tests\app\SamplePanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用真实 Element Plus 组件验证示例参数、增量、取消、提交阶段与结果权威回读.
 */
import { beforeEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElDatePicker, ElInputNumber } from "element-plus"
import { setLocale, type AppLocale } from "@/i18n"
import { BridgeClient, type SampleDataState, type Transport } from "@/bridge/client"
import type { ScaffoldMethods } from "@/features/scaffold/contracts"
import NewPanel from "@/app/NewPanel.vue"
import SamplePanel from "@/app/SamplePanel.vue"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
beforeEach(() => {
    vi.mocked(showMessage).mockClear()
})

type Request = { id: string; method: string; params: Record<string, unknown> }
/** 仅返回内存模拟状态, 生成请求由测试手动推进, 不创建文件. */
class SampleTransport implements Transport {
    requests: Request[] = []
    state: SampleDataState = { outputPath: "C:/fixture/示例", hasData: false, lastFactDate: null, tables: [], isSimulation: true }
    listeners = new Set<(event: MessageEvent) => void>()
    corruptStatistics = false
    deferStatistics = false
    /** 订阅受控响应, listener 为桥接回调. */
    addEventListener(_type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.add(listener)
    }
    /** 释放受控响应回调. */
    removeEventListener(_type: "message", listener: (event: MessageEvent) => void): void {
        this.listeners.delete(listener)
    }
    /** 记录请求并返回非生成操作结果, message 为结构化桥接包. */
    postMessage(message: unknown): void {
        const request = message as Request
        this.requests.push(request)
        if (request.method === "sample.status" && this.deferStatistics) return
        if (request.method === "scaffold.templates") {
            this.reply(request.id, {
                templates: [],
                defaultMeasures: [],
                periods: [],
                templateQueries: [],
                templateDirectory: "C:/fixture/templates",
            } satisfies ScaffoldMethods["scaffold.templates"]["result"])
            return
        }
        if (request.method === "sample.status" && this.corruptStatistics)
            this.send({ id: request.id, error: { code: "SAMPLE_DATA_INVALID", message: "示例 CSV 损坏，请重新生成或清空" } })
        else if (request.method === "sample.status" || request.method === "sample.chooseDirectory") this.reply(request.id, structuredClone(this.state))
        else if (request.method === "sample.clear") {
            this.state = { ...this.state, hasData: false, lastFactDate: null, tables: [] }
            this.reply(request.id, structuredClone(this.state))
        } else if (request.method === "request.cancel") this.reply(request.id, { accepted: true })
    }
    /** 注入宿主包, data 为协议正文. */
    send(data: unknown): void {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...(data as object) } }))
    }
    /** 注入终态, id 为目标请求, result 为实际结果. */
    reply(id: string, result: unknown): void {
        this.send({ id, result })
    }
    /** 完成最后生成请求, 统计保持模拟标识. */
    finish(): void {
        const request = this.requests.findLast((item) => item.method === "sample.generate")!
        this.state = { ...this.state, hasData: true, lastFactDate: request.params.endDate as string, tables: [{ name: "T00_产品表", rows: 7, size: 1024 }] }
        this.reply(request.id, { state: structuredClone(this.state), result: { products: 7, stores: 2, customers: 3, inventory: 4, orders: 5, orderItem: 6 } })
    }
}
/** 找到当前文案的动作按钮, label 为完整文本, 缺失时测试失败. */
function button(wrapper: VueWrapper, label: string) {
    return wrapper.findAll("button").find((item) => item.text() === label)!
}
/** 从正文外的真实消息浮层读取取消入口, 不将浮层误算为页面内容. */
function operationCancel() {
    const element = document.querySelector<HTMLButtonElement>(".operation-message .operation-feedback__cancel")
    return element ? new DOMWrapper(element) : undefined
}
/** 返回当前浮层的阶段和百分比, 便于验证进度不进入正文. */
function operationText(): string {
    return document.querySelector(".operation-message .operation-feedback")?.textContent ?? ""
}

it.each([
    ["zh-cn", "正在加载已有示例数据", "Loading existing sample data"],
    ["en-us", "Loading existing sample data", "正在加载已有示例数据"],
] as const)("%s 首次统计等待显示持续加载, 切语言不重读, 完成关闭且不弹成功", async (locale, loading, translated) => {
    setLocale(locale)
    const transport = new SampleTransport()
    transport.deferStatistics = true
    transport.state = { ...transport.state, hasData: true, tables: [{ name: "T00_产品表", rows: 7, size: 1024 }] }
    const bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        expect(operationText()).toContain(loading)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.attributes("aria-busy")).toBe("true")
        expect(wrapper.find(".sample-actions button").attributes("disabled")).toBeDefined()
        expect(document.querySelector(".operation-message [role=progressbar]")).toBeNull()
        setLocale(locale === "zh-cn" ? "en-us" : "zh-cn")
        await flushPromises()
        expect(operationText()).toContain(translated)
        expect(transport.requests).toHaveLength(1)
        transport.reply(transport.requests[0]!.id, structuredClone(transport.state))
        await flushPromises()
        expect(document.querySelector(".operation-message")).toBeNull()
        expect(wrapper.text()).toContain("T00_产品表")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.attributes("aria-busy")).toBe("false")
        expect(showMessage).not.toHaveBeenCalled()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("首次读取可取消, 等待真实终态才关闭加载并解除锁", async () => {
    setLocale("zh-cn")
    const transport = new SampleTransport()
    transport.deferStatistics = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        const request = transport.requests[0]!
        expect(operationCancel()).toBeDefined()
        await operationCancel()!.trigger("click")
        await flushPromises()
        expect(transport.requests.at(-1)).toMatchObject({ method: "request.cancel", params: { requestId: request.id } })
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(operationCancel()!.attributes("disabled")).toBeDefined()
        transport.send({ id: request.id, error: { code: "CANCELLED", message: "已取消" } })
        await flushPromises()
        expect(document.querySelector(".operation-message")).toBeNull()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(showMessage).not.toHaveBeenCalled()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("统计失败结束加载并允许重新选择, 卸载后迟到结果不恢复提示", async () => {
    setLocale("zh-cn")
    const transport = new SampleTransport()
    transport.deferStatistics = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    let mounted = true
    try {
        await flushPromises()
        transport.send({ id: transport.requests[0]!.id, error: { code: "SAMPLE_DATA_INVALID", message: "示例 CSV 损坏，请重新生成或清空" } })
        await flushPromises()
        expect(document.querySelector(".operation-message")).toBeNull()
        expect(showMessage).toHaveBeenCalledExactlyOnceWith("示例 CSV 损坏，请重新生成或清空", "error")
        await button(wrapper, "指定存放目录").trigger("click")
        await flushPromises()
        expect(operationText()).toContain("正在加载已有示例数据")
        const pending = transport.requests.at(-1)!
        expect(pending.method).toBe("sample.status")
        wrapper.unmount()
        mounted = false
        expect(document.querySelector(".operation-message")).toBeNull()
        transport.reply(pending.id, structuredClone(transport.state))
        await flushPromises()
        expect(document.querySelector(".operation-message")).toBeNull()
        expect(showMessage).toHaveBeenCalledTimes(1)
    } finally {
        if (mounted) wrapper.unmount()
        bridge.dispose()
    }
})

it("新建独立流程不读取示例目录, 示例面板首次挂载才读取", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(NewPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(wrapper.find(".scaffold-panel").exists()).toBe(true)
    expect(wrapper.find("#tab-sample").exists()).toBe(false)
    expect(transport.requests.map((item) => item.method)).toEqual(["scaffold.templates"])
    wrapper.unmount()
    const sample = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(transport.requests.map((item) => item.method)).toEqual(["scaffold.templates", "sample.status"])
    sample.unmount()
    bridge.dispose()
})
it("示例读取期间切语言保留用户日期, 不重新读取或生成", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.deferStatistics = true
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    const dates = wrapper.findAllComponents(ElDatePicker)
    await dates[0]!.setValue("2024-01-01")
    setLocale("en-us")
    await flushPromises()
    expect(dates[0]!.props("modelValue")).toBe("2024-01-01")
    transport.reply(transport.requests.find((request) => request.method === "sample.status")!.id, structuredClone(transport.state))
    await flushPromises()
    expect(transport.requests.filter((request) => request.method === "sample.status")).toHaveLength(1)
    expect(transport.requests.some((request) => request.method === "sample.generate")).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})
it("示例工作区卸载会取消目录回读, 迟到结果不重新发出忙状态或展示消息", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.deferStatistics = true
    const activity = vi.fn()
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge), onBusy: activity } })
    await flushPromises()
    const status = transport.requests.find((request) => request.method === "sample.status")!
    const busyEvents = activity.mock.calls.length
    wrapper.unmount()
    expect(transport.requests.at(-1)).toMatchObject({ method: "request.cancel", params: { requestId: status.id } })
    transport.reply(status.id, structuredClone(transport.state))
    await flushPromises()
    expect(activity).toHaveBeenCalledTimes(busyEvents)
    expect(showMessage).not.toHaveBeenCalled()
    bridge.dispose()
})
it("首次显示和重挂载从结束日减 1600 天, 延迟目录回读与后续日期编辑不覆盖用户起点", async () => {
    vi.useFakeTimers({ toFake: ["Date"] })
    vi.setSystemTime(new Date(2026, 9, 14, 0, 15))
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.deferStatistics = true
    let wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    try {
        let dates = wrapper.findAllComponents(ElDatePicker)
        expect(dates[0]!.props("modelValue")).toBe("2022-05-28")
        expect(dates[1]!.props("modelValue")).toBe("2026-10-14")
        await dates[0]!.setValue("2024-01-01")
        transport.reply(transport.requests.at(-1)!.id, structuredClone(transport.state))
        await flushPromises()
        expect(dates[0]!.props("modelValue")).toBe("2024-01-01")
        await dates[1]!.setValue("2026-12-31")
        expect(dates[0]!.props("modelValue")).toBe("2024-01-01")
        wrapper.unmount()
        transport.deferStatistics = false
        vi.setSystemTime(new Date(2026, 9, 15, 0, 15))
        wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
        await flushPromises()
        dates = wrapper.findAllComponents(ElDatePicker)
        expect(dates[0]!.props("modelValue")).toBe("2022-05-29")
        expect(dates[1]!.props("modelValue")).toBe("2026-10-15")
    } finally {
        wrapper.unmount()
        bridge.dispose()
        vi.useRealTimers()
    }
})
it.each(["zh-cn", "en-us"])("移除说明和刷新入口, %s 的全部动作按钮均使用本地图标", async (locale) => {
    setLocale(locale as AppLocale)
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.state = { ...transport.state, hasData: true, lastFactDate: "2026-01-31" }
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    expect(wrapper.find(".eyebrow").exists()).toBe(false)
    expect(wrapper.find(".sample-field-hint").exists()).toBe(false)
    expect(wrapper.text()).not.toContain("刷新结果")
    expect(wrapper.text()).not.toContain("11 张关联数据表")
    expect(wrapper.findAll(".sample-directory-row > .el-button")).toHaveLength(2)
    expect(wrapper.find(".sample-language").exists()).toBe(false)
    expect(wrapper.find(".el-select").exists()).toBe(false)
    for (const action of wrapper.findAll(".sample-directory-row button, .sample-actions button, .sample-cutoff button")) {
        expect(action.find("img.ui-icon").attributes("src")).toMatch(/^\/icons\/[a-z0-9-]+\.svg$/)
    }
    expect(wrapper.text()).not.toContain("Refresh Results")
    expect(wrapper.text()).not.toContain("11 related tables")
    wrapper.unmount()
    bridge.dispose()
})
it("无效日期阻止生成, 双语切换保留参数且生成不含路径", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await wrapper.findAllComponents(ElDatePicker)[0]!.setValue("2026-01-01")
    await wrapper.findAllComponents(ElDatePicker)[1]!.setValue("2026-01-02")
    await wrapper.get("form").trigger("submit")
    expect(transport.requests).toHaveLength(1)
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("至少间隔 60 天"), "warning")
    await wrapper.findAllComponents(ElDatePicker)[1]!.setValue("2026-03-02")
    await wrapper.findAllComponents(ElInputNumber)[0]!.setValue(7)
    setLocale("en-us")
    await flushPromises()
    expect(wrapper.text()).toContain("Generate Sample Data")
    await wrapper.get("form").trigger("submit")
    expect(transport.requests.at(-1)?.params).toEqual({
        mode: "full",
        locale: "en-us",
        productCount: 7,
        storeCount: 55,
        inventoryCycle: 22,
        startDate: "2026-01-01",
        endDate: "2026-03-02",
    })
    transport.finish()
    await flushPromises()
    expect(showMessage).toHaveBeenLastCalledWith(expect.stringContaining("Browser simulation"), "info")
    expect(wrapper.text()).not.toContain("Browser simulation")
    wrapper.unmount()
    bridge.dispose()
})
it("在途只有一条正文外进度, 提交前可取消且提交后禁用, 权威完成后只保留最终消息", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    const form = wrapper.get("form").element
    const footer = wrapper.get(".sample-footer").element
    await wrapper.get("form").trigger("submit")
    const request = transport.requests.at(-1)!
    expect(operationCancel()).toBeDefined()
    expect(operationCancel()!.attributes("disabled")).toBeUndefined()
    expect(wrapper.findAll(".el-progress, .sample-progress, [role=status]")).toHaveLength(0)
    expect(document.querySelectorAll(".operation-message .el-progress")).toHaveLength(1)
    expect(wrapper.find(".sample-actions .is-loading").exists()).toBe(true)
    transport.send({ event: { requestId: request.id, sequence: 1, type: "sampleProgress", payload: { percent: 100, stage: "stageSummary" } } })
    await flushPromises()
    expect(operationText()).toContain("99%")
    expect(wrapper.text()).not.toContain("99%")
    expect(wrapper.get("form").element).toBe(form)
    expect(wrapper.get(".sample-footer").element).toBe(footer)
    expect(wrapper.find(".sample-results .el-table").exists()).toBe(false)
    expect(wrapper.find(".sample-results").exists()).toBe(false)
    expect(wrapper.find(".sample-language").exists()).toBe(false)
    transport.send({ event: { requestId: request.id, sequence: 2, type: "sampleProgress", payload: { percent: 100, stage: "stageDone" } } })
    await flushPromises()
    expect(operationText()).toContain("99%")
    expect(wrapper.text()).not.toContain("生成完成")
    expect(showMessage).not.toHaveBeenCalled()
    expect(wrapper.findAll(".el-progress")).toHaveLength(0)
    transport.send({ requestPhase: { requestId: request.id, phase: "committing" } })
    await flushPromises()
    expect(operationCancel()!.attributes("disabled")).toBeDefined()
    const progress = document.querySelector(".operation-message [role=progressbar]")!
    transport.finish()
    await flushPromises()
    expect(progress.getAttribute("aria-valuenow")).toBe("100")
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(wrapper.text()).not.toContain("100%")
    expect(wrapper.get("form").element).toBe(form)
    expect(wrapper.get(".sample-footer").element).toBe(footer)
    expect(wrapper.text()).toContain("T00_产品表")
    expect(wrapper.text()).toContain("1.0 KB")
    expect(showMessage).toHaveBeenCalledExactlyOnceWith(expect.stringContaining("模拟"), "info")
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    wrapper.unmount()
    bridge.dispose()
})
it.each([
    ["zh-cn", "full", "示例数据生成完成"],
    ["en-us", "full", "Sample data generated"],
    ["zh-cn", "incremental", "增量更新完成"],
    ["en-us", "incremental", "Incremental update completed"],
] as const)("%s 的 %s 完成先关闭进度, 再展示一次最终纯文本", async (locale, mode, message) => {
    setLocale(locale)
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.state = { ...transport.state, hasData: true, lastFactDate: "2026-01-31", isSimulation: false }
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await wrapper.findAllComponents(ElDatePicker)[0]!.setValue("2026-02-01")
    await wrapper.findAllComponents(ElDatePicker)[1]!.setValue("2026-05-01")
    const lingeringProgress: number[] = []
    vi.mocked(showMessage).mockImplementationOnce(() => {
        lingeringProgress.push(document.querySelectorAll(".operation-message .el-progress").length)
    })
    if (mode === "full") await wrapper.get("form").trigger("submit")
    else await wrapper.findAll(".sample-actions button")[1]!.trigger("click")
    const request = transport.requests.at(-1)!
    expect(request.params.mode).toBe(mode)
    transport.send({ event: { requestId: request.id, sequence: 1, type: "sampleProgress", payload: { percent: 61, stage: "stageOrders" } } })
    await flushPromises()
    expect(document.querySelectorAll(".operation-message .el-progress")).toHaveLength(1)
    expect(operationText()).toContain("61%")
    expect(showMessage).not.toHaveBeenCalled()
    transport.finish()
    await flushPromises()
    expect(lingeringProgress).toEqual([0])
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(showMessage).toHaveBeenCalledExactlyOnceWith(message, "success")
    expect(wrapper.text()).not.toContain(message)
    wrapper.unmount()
    bridge.dispose()
})
it("生成失败关闭唯一进度并保留错误, 权威统计回读结束后可重试且不提前显示完成", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await wrapper.get("form").trigger("submit")
    const request = transport.requests.at(-1)!
    transport.send({ event: { requestId: request.id, sequence: 1, type: "sampleProgress", payload: { percent: 87, stage: "stageOrders" } } })
    await flushPromises()
    const progress = document.querySelector(".operation-message [role=progressbar]")!
    transport.deferStatistics = true
    transport.send({ id: request.id, error: { code: "SAMPLE_DATA_CONFLICT", message: "示例产物已被其他程序修改" } })
    await flushPromises()
    expect(progress.getAttribute("aria-valuenow")).toBe("87")
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("示例产物已被其他程序修改"), "error")
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
    expect(showMessage).toHaveBeenCalledExactlyOnceWith("示例产物已被其他程序修改", "error")
    transport.reply(transport.requests.at(-1)!.id, structuredClone(transport.state))
    await flushPromises()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    vi.mocked(showMessage).mockClear()
    await wrapper.get("form").trigger("submit")
    expect(operationText()).toContain("0%")
    expect(wrapper.text()).not.toContain("示例产物已被其他程序修改")
    transport.finish()
    await flushPromises()
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(showMessage).toHaveBeenCalledExactlyOnceWith(expect.stringContaining("模拟"), "info")
    wrapper.unmount()
    bridge.dispose()
})
it("增量必须接续事实截止日, 目录管理重读并清空统计", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    transport.state = { ...transport.state, hasData: true, lastFactDate: "2026-01-31", tables: [{ name: "T04_订单主表", rows: 2, size: 10 }] }
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await button(wrapper, "增量更新").trigger("click")
    expect(transport.requests).toHaveLength(1)
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("增量起始日期应为 2026-02-01"), "error")
    await button(wrapper, "使用增量起始日期").trigger("click")
    await wrapper.findAllComponents(ElDatePicker)[1]!.setValue("2026-05-01")
    await button(wrapper, "增量更新").trigger("click")
    expect(transport.requests.at(-1)?.params).toMatchObject({ mode: "incremental", startDate: "2026-02-01", endDate: "2026-05-01" })
    transport.finish()
    await flushPromises()
    await button(wrapper, "清空数据").trigger("click")
    await flushPromises()
    expect(wrapper.find(".sample-results .el-table").exists()).toBe(false)
    expect(button(wrapper, "清空数据").attributes("disabled")).toBeDefined()
    await button(wrapper, "指定存放目录").trigger("click")
    await flushPromises()
    expect(transport.requests.slice(-2).map((item) => ({ method: item.method, params: item.params }))).toEqual([
        { method: "sample.chooseDirectory", params: {} },
        { method: "sample.status", params: {} },
    ])
    wrapper.unmount()
    bridge.dispose()
})
it("取消只提交关联 ID, 取消终态回读产物且不显示生成成功", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await wrapper.get("form").trigger("submit")
    await flushPromises()
    const request = transport.requests.at(-1)!
    const footer = wrapper.get(".sample-footer").element
    const progress = document.querySelector(".operation-message [role=progressbar]")!
    await operationCancel()!.trigger("click")
    await flushPromises()
    expect(transport.requests.at(-1)).toMatchObject({ method: "request.cancel", params: { requestId: request.id } })
    expect(operationCancel()!.attributes("disabled")).toBeDefined()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
    expect(wrapper.get(".sample-footer").element).toBe(footer)
    transport.send({ id: request.id, error: { code: "CANCELLED", message: "已取消" } })
    await flushPromises()
    expect(transport.requests.at(-1)?.method).toBe("sample.status")
    expect(wrapper.find(".sample-progress").exists()).toBe(false)
    expect(progress.getAttribute("aria-valuenow")).toBe("0")
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    expect(wrapper.text()).not.toContain("示例数据生成完成")
    expect(showMessage).not.toHaveBeenCalled()
    wrapper.unmount()
    bridge.dispose()
})
it("选择后的 CSV 统计失败保留目录与清空入口, 可重新生成修复", async () => {
    const transport = new SampleTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(SamplePanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    transport.state = { ...transport.state, outputPath: "C:/fixture/损坏示例", hasData: true }
    transport.corruptStatistics = true
    await button(wrapper, "指定存放目录").trigger("click")
    await flushPromises()
    expect(wrapper.get('input[aria-label="存放目录"]').element).toHaveProperty("value", "C:/fixture/损坏示例")
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("示例 CSV 损坏"), "error")
    expect(button(wrapper, "清空数据").attributes("disabled")).toBeUndefined()
    expect(button(wrapper, "生成示例数据").attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})
