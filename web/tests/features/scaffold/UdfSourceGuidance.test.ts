/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfSourceGuidance.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证缺少更新来源的显式提示, 内置来源边界与分类管理入口.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElSelect } from "element-plus"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import { createConfigurationDraft } from "@/features/configuration/draft"
import type { UdfSourceEntry } from "@/features/scaffold/contracts"
import * as messages from "@/ui/message"
import { setLocale } from "@/i18n"

/** 仅替换来源列表, 保留其他宿主协议与缓存行为. */
class SourceTransport extends MockTransport {
    replacements = new Map<(event: MessageEvent) => void, (event: MessageEvent) => void>()
    sourceRequests = new Set<string>()
    constructor(private sources: UdfSourceEntry[]) {
        super()
    }
    /** 来源响应采用指定事实, 其他响应原样转发. */
    override addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = (event: MessageEvent) => {
            if (this.sourceRequests.has(event.data.id)) listener(new MessageEvent("message", { data: { ...event.data, result: { items: this.sources } } }))
            else listener(event)
        }
        this.replacements.set(listener, wrapped)
        super.addEventListener(type, wrapped)
    }
    /** 释放本次文档对应的观察者. */
    override removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        super.removeEventListener(type, this.replacements.get(listener)!)
        this.replacements.delete(listener)
    }
    /** 记录来源读取身份, 不模拟远端更新成功. */
    override postMessage(value: unknown): void {
        const request = value as { id: string; method: string }
        if (request.method === "udf.sources") this.sourceRequests.add(request.id)
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 类型化宿主传输不是浏览器 postMessage.
        super.postMessage(value)
    }
}

const local: UdfSourceEntry = { id: "local", name: "Local", kind: "local", baseUrl: null, baseline: null, protocol: "git" }
const withoutUrl: UdfSourceEntry = { id: "daxlib", name: "DAXLib", kind: "builtin", baseUrl: null, baseline: null, protocol: "git" }

it("新建配置默认可见, 仅在进入已启用的 UDF 步骤后提示首次更新", async () => {
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new MockTransport())
    const draft = createConfigurationDraft("zh-cn")
    draft.udfsEnabled = true
    const wrapper = mount(ConfigurationFields, {
        props: {
            bridge: markRaw(bridge),
            draft,
            step: "measures",
            visited: new Set(["udfs"]),
            idPrefix: "test",
            disabled: false,
            locked: false,
            definitions: [],
        },
    })
    try {
        await flushPromises()
        expect(feedback.mock.calls.some(([message]) => message.includes("首次更新"))).toBe(false)
        await wrapper.setProps({ step: "udfs" })
        await flushPromises()
        expect(feedback).toHaveBeenCalledWith("当前来源尚未完成首次更新。请点击“更新来源”，完成后即可浏览和搜索函数。", "warning")
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
    }
})

it.each(["zh-cn", "en-us"] as const)("%s 首次未更新时主动提示, 隐藏或禁用时延后且不自动更新", async (language) => {
    setLocale(language)
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: true, active: false, modelValue: [] } })
    const notice =
        language === "zh-cn"
            ? "当前来源尚未完成首次更新。请点击“更新来源”，完成后即可浏览和搜索函数。"
            : "This source has not been updated yet. Click Update source to browse and search its functions."
    try {
        await flushPromises()
        expect(feedback).not.toHaveBeenCalledWith(notice, "warning")
        await wrapper.setProps({ disabled: false })
        await flushPromises()
        expect(feedback).not.toHaveBeenCalledWith(notice, "warning")
        await wrapper.setProps({ active: true })
        await flushPromises()
        expect(feedback).toHaveBeenCalledWith(notice, "warning")
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
        await wrapper.setProps({ active: false })
        await wrapper.setProps({ active: true })
        setLocale(language === "zh-cn" ? "en-us" : "zh-cn")
        await flushPromises()
        expect(feedback.mock.calls.filter(([message]) => message === notice)).toHaveLength(1)
        await wrapper.get('[data-test="udf-refresh"]').trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(1)
        wrapper.unmount()
        feedback.mockClear()
        const reopened = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
        await flushPromises()
        expect(feedback.mock.calls.some(([message]) => message.includes("首次更新") || message.includes("has not been updated"))).toBe(false)
        reopened.unmount()
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
        setLocale("zh-cn")
    }
})

it.each([
    { name: "来源列表为空", sources: [] as UdfSourceEntry[], selectLocal: false },
    { name: "当前是本地函数", sources: [local], selectLocal: true },
    { name: "来源缺少更新地址", sources: [withoutUrl], selectLocal: false },
    { name: "更新地址只有空白", sources: [{ ...withoutUrl, baseUrl: "   " }], selectLocal: false },
])("$name 时更新入口可点击并提示先设置, 不发送更新请求", async ({ sources, selectLocal }) => {
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new SourceTransport(sources))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    try {
        await flushPromises()
        if (selectLocal) {
            const picker = wrapper.findComponent(ElSelect)
            await picker.setValue("local")
            picker.vm.$emit("change", "local")
            await flushPromises()
        }
        expect(feedback.mock.calls.some(([message]) => message.includes("请先选择或设置更新来源"))).toBe(false)
        const refresh = wrapper.get('[data-test="udf-refresh"]')
        expect(refresh.attributes("disabled")).toBeUndefined()
        await refresh.trigger("click")
        await flushPromises()
        expect(feedback).toHaveBeenCalledWith("请先选择或设置更新来源。可在“来源与本地管理”中打开“管理来源”；本地函数无需远端更新。", "warning")
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
    }
})

it("正常内置来源照常更新, 来源与本地管理集中折叠且所有入口保留", async () => {
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    try {
        await flushPromises()
        const management = wrapper.get("details.udf-management")
        expect(feedback).toHaveBeenCalledWith("当前来源尚未完成首次更新。请点击“更新来源”，完成后即可浏览和搜索函数。", "warning")
        expect(management.attributes("open")).toBeUndefined()
        expect(management.get("summary").text()).toBe("来源与本地管理")
        expect(management.findAll("button").map((button) => button.text())).toEqual(["管理来源", "管理本地函数"])
        await management.get("summary").trigger("click")
        expect(management.attributes("open")).toBeDefined()
        expect(wrapper.get(".udf-source-toolbar").findAll("button.el-button")).toHaveLength(1)
        expect(wrapper.get(".udf-browse-section h3").text()).toBe("浏览函数")
        await wrapper.get('[data-test="udf-refresh"]').trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(1)
        expect(feedback.mock.calls.some(([message]) => message.includes("请先选择或设置更新来源"))).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
    }
})

it("英文来源提示与分组随语言切换显示, 不额外发送更新", async () => {
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new SourceTransport([]))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    try {
        await flushPromises()
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get(".udf-source-section h3").text()).toBe("Source and updates")
        expect(wrapper.get(".udf-management summary").text()).toBe("Source and local management")
        await wrapper.get('[data-test="udf-refresh"]').trigger("click")
        expect(feedback).toHaveBeenCalledWith(
            "Choose or configure an update source first. Open Manage sources under Source and local management. Local functions do not require remote updates.",
            "warning",
        )
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
        setLocale("zh-cn")
    }
})

it("未启用的可选 UDF 不自动提示, 更新按钮保持真正禁用", async () => {
    const feedback = vi.spyOn(messages, "showMessage")
    const bridge = new BridgeClient(new SourceTransport([]))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: true, modelValue: [] } })
    try {
        await flushPromises()
        expect(wrapper.get('[data-test="udf-refresh"]').attributes("disabled")).toBeDefined()
        await wrapper.get('[data-test="udf-refresh"]').trigger("click")
        expect(feedback.mock.calls.some(([message]) => message.includes("请先选择或设置更新来源"))).toBe(false)
        expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        feedback.mockRestore()
    }
})
