/**
 * FilePath    : pbi-utils\web\tests\app\PowerBiPanel.reasons.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证混合 Desktop 实例的静默发现、实际可连接数量和显式原因反馈.
 */
import { afterEach, expect, it, vi } from "vitest"
import { ElMessage } from "element-plus"
import { flushPromises, mount } from "@vue/test-utils"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import ModelInstanceCard from "@/app/ModelInstanceCard.vue"
import { BridgeClient, type PowerBiInstance, type PowerBiSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"

let wrapper: ReturnType<typeof mount> | undefined
let bridge: BridgeClient | undefined
afterEach(() => {
    wrapper?.unmount()
    bridge?.dispose()
    wrapper = undefined
    bridge = undefined
    ElMessage.closeAll()
    setLocale("zh-cn")
})

/** 创建不同进程的实例事实, 不把空模型当作连接成功; overrides 用于等待和失败边界. */
function instance(processId: number, overrides: Partial<PowerBiInstance> = {}): PowerBiInstance {
    return {
        processId,
        instanceId: `desktop-${processId}`,
        title: "同名报表",
        port: 4000 + processId,
        status: "unavailable",
        models: [],
        errorCode: "DESKTOP_NO_LOCAL_MODEL",
        message: "实例没有可用的本地端口",
        ...overrides,
    }
}

/** 创建一次真实扫描形状的响应, 返回对象可用于模拟自动补扫或主动刷新. */
function snapshot(instances: PowerBiInstance[]): PowerBiSnapshot {
    return { timestamp: "2026-11-01T12:00:00Z", instances }
}

/** 挂载实际面板与共享消息组件, 只替换实例发现回复, 返回请求观察器. */
async function mountInstances(instances: PowerBiInstance[]) {
    bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "initial", promise: Promise.resolve(snapshot(instances)) })
    wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    return request
}

/** 混合实例与全部不可连接都静默, 明确原因只归属点击的卡片, 正常连接不受旁观实例影响. */
it.each([
    { locale: "zh-cn", available: 0 },
    { locale: "zh-cn", available: 1 },
    { locale: "en-us", available: 0 },
    { locale: "en-us", available: 1 },
] as const)("$locale 可连接 $available 个时静默发现并按实例查看原因", async ({ locale, available }) => {
    setLocale(locale)
    const instances = [instance(101), instance(102, { errorCode: "POWER_BI_CONNECTION_FAILED", message: "无法读取模型" })]
    if (available)
        instances.push(
            instance(103, {
                status: "connected",
                errorCode: null,
                message: null,
                models: [{ name: "本地模型", compatibilityLevel: 1600, tableCount: 3, measureCount: 7 }],
            }),
        )
    const request = await mountInstances(instances)
    const panel = wrapper!
    const expectedSummary =
        locale === "zh-cn" ? `发现 ${instances.length} 个实例 · 可连接 ${available} 个` : `Found ${instances.length} instances · ${available} connectable`
    expect(panel.get(".scan-summary").text()).toContain(expectedSummary)
    expect(document.querySelector(".el-message")).toBeNull()
    const cards = panel.findAll(".instance-card")
    expect(cards[0]!.get(".instance-status").text()).toBe(locale === "zh-cn" ? "无本地模型" : "No local model")
    expect(cards[1]!.get(".instance-status").text()).toBe(locale === "zh-cn" ? "读取失败" : "Read failed")
    expect(cards[0]!.get(".instance-connect-button").attributes("disabled")).toBeDefined()
    const reason = cards[0]!.get(".instance-reason-button")
    expect(reason.element.tagName).toBe("BUTTON")
    expect(reason.attributes("disabled")).toBeUndefined()
    expect(reason.attributes("aria-label")).toContain("101")
    await reason.trigger("click")
    expect(document.querySelectorAll(".el-message")).toHaveLength(1)
    expect(document.querySelector(".el-message")?.textContent).toContain("101")
    expect(document.querySelector(".el-message")?.textContent).toContain("同名报表")
    expect(request.mock.calls.filter(([method]) => method === "project.connectLocal")).toHaveLength(0)
    ElMessage.closeAll()
    await flushPromises()
    // 切换语言不重放详情, 自动补扫和主动刷新也不逐卡片通知.
    setLocale(locale === "zh-cn" ? "en-us" : "zh-cn")
    await flushPromises()
    expect(document.querySelector(".el-message")).toBeNull()
    request.mockReturnValueOnce({ id: "automatic-refresh", promise: Promise.resolve(snapshot(structuredClone(instances))) })
    await panel.setProps({ refreshKey: "changed" })
    await flushPromises()
    expect(document.querySelector(".el-message")).toBeNull()
    request.mockReturnValueOnce({ id: "manual-refresh", promise: Promise.resolve(snapshot(structuredClone(instances))) })
    await panel.get(".instance-refresh-button").trigger("click")
    await flushPromises()
    expect(document.querySelector(".el-message")).toBeNull()
    if (available) {
        request.mockReturnValueOnce({ id: "connect", promise: Promise.resolve(previewProject) })
        await cards[2]!.get(".instance-connect-button").trigger("click")
        await flushPromises()
        expect(request.mock.calls.at(-1)?.[0]).toBe("project.connectLocal")
        expect(request.mock.calls.at(-1)?.[1]).toEqual({ processId: "103", instanceId: "desktop-103" })
    }
})

/** 刷新中的旧卡片不能发布过期原因, 整体扫描失败保留全局错误且继续禁用旧原因入口. */
it("刷新在途和失败旧快照禁止查看旧原因, 整体失败仍提示", async () => {
    const request = await mountInstances([instance(101)])
    const panel = wrapper!
    let reject!: (error: Error) => void
    request.mockReturnValueOnce({
        id: "pending",
        promise: new Promise<PowerBiSnapshot>((_, fail) => {
            reject = fail
        }),
    })
    await panel.get(".instance-refresh-button").trigger("click")
    expect(panel.get(".instance-reason-button").attributes("disabled")).toBeDefined()
    panel.getComponent(ModelInstanceCard).vm.$emit("reason")
    await flushPromises()
    expect(document.querySelector(".el-message")).toBeNull()
    reject(new Error("整体扫描失败"))
    await flushPromises()
    expect(document.querySelector(".el-message--error")?.textContent).toBe("整体扫描失败")
    expect(panel.get(".instance-reason-button").attributes("disabled")).toBeDefined()
    ElMessage.closeAll()
    await flushPromises()
    panel.getComponent(ModelInstanceCard).vm.$emit("reason")
    await flushPromises()
    expect(document.querySelector(".el-message")).toBeNull()
})

/** 等待与多模型保持各自事实, 未读模型及歧义模型均不计入可连接数量. */
it("等待和多模型展示各自原因且不误计可连接数量", async () => {
    await mountInstances([
        instance(101, { status: "starting", errorCode: null, message: null }),
        instance(102, {
            status: "connected",
            errorCode: null,
            message: null,
            models: [
                { name: "甲", compatibilityLevel: 1600, tableCount: 1, measureCount: 1 },
                { name: "乙", compatibilityLevel: 1600, tableCount: 1, measureCount: 1 },
            ],
        }),
    ])
    expect(wrapper!.get(".scan-summary").text()).toContain("可连接 0 个")
    expect(wrapper!.findAll(".instance-status").map((item) => item.text())).toEqual(["等待模型", "多个模型"])
    expect(wrapper!.findAll(".instance-reason-button")).toHaveLength(2)
    expect(document.querySelector(".el-message")).toBeNull()
})
