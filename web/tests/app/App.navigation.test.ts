/**
 * FilePath    : pbi-utils\web\tests\app\App.navigation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证侧栏顺序、简洁工作区和独立业务导航, 保留各页面的在途锁.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import App from "@/app/App.vue"
import SamplePanel from "@/app/SamplePanel.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import { MockTransport } from "@/bridge/mock"
import { BridgeClient } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import { ElMessage } from "element-plus"

afterEach(() => {
    ElMessage.closeAll()
    vi.unstubAllGlobals()
})

it.each([
    ["zh-cn", ["连接", "模型", "新建", "示例", "学习", "设置"]],
    ["en-us", ["Connections", "Model", "New", "Sample", "Learn", "Settings"]],
] as const)("%s 侧栏按产品顺序排列, 每页移除冗余页脚和说明且折叠后保留导航名称", async (locale, labels) => {
    const transport = new MockTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale }).promise
    preferences.dispose()
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        expect(wrapper.findComponent(ConnectionPanel).exists()).toBe(true)
        expect(wrapper.findAll(".sidebar nav button").map((button) => button.attributes("aria-label"))).toEqual(labels)
        expect(wrapper.find(".sidebar-footer .dot").exists()).toBe(false)
        expect(wrapper.find(".sidebar-footer p").exists()).toBe(false)
        await wrapper.get(`.sidebar nav button[aria-label="${labels[0]}"]`).trigger("click")
        await flushPromises()
        wrapper.getComponent(ConnectionPanel).vm.$emit("connected", structuredClone(previewProject))
        await flushPromises()
        for (const label of labels) {
            // oxlint-disable-next-line no-await-in-loop -- 导航共享同一工作区状态, 必须完成当前页面的切换再访问下一页.
            await wrapper.get(`nav button[aria-label="${label}"]`).trigger("click")
            // oxlint-disable-next-line no-await-in-loop -- 等待当前页面的桥接与挂载结束, 不能并发激活多个页面.
            await flushPromises()
            expect(wrapper.get(`nav button[aria-label="${label}"]`).attributes("aria-current")).toBe("page")
            expect(wrapper.find(".workspace-footer").exists()).toBe(false)
            expect(wrapper.find(".content .eyebrow, .content .intro").exists()).toBe(false)
        }
        await wrapper.get(".collapse-button").trigger("click")
        expect(wrapper.find(".shell.collapsed").exists()).toBe(true)
        expect(wrapper.findAll(".sidebar nav button").map((button) => button.attributes("aria-label"))).toEqual(labels)
    } finally {
        wrapper.unmount()
    }
})

it("示例独立挂载, 导航在操作期间锁定且不再提供用户或关于入口", async () => {
    const transport = new MockTransport()
    const requests = vi.spyOn(transport, "postMessage")
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        expect(wrapper.find('nav button[aria-label="用户"]').exists()).toBe(false)
        expect(wrapper.find('nav button[aria-label="关于"]').exists()).toBe(false)
        await wrapper.get('nav button[aria-label="示例"]').trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(SamplePanel).exists()).toBe(true)
        expect(wrapper.findAll('[role="tab"]')).toHaveLength(0)
        expect(requests.mock.calls.some(([message]) => (message as { method: string }).method === "scaffold.templates")).toBe(false)
        const sample = wrapper.getComponent(SamplePanel)
        sample.vm.$emit("busy", true)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="新建"]').attributes("disabled")).toBeDefined()
        await wrapper.get('nav button[aria-label="新建"]').trigger("click")
        expect(wrapper.get('nav button[aria-label="示例"]').attributes("aria-current")).toBe("page")
        sample.vm.$emit("busy", false)
        await flushPromises()
        await wrapper.get('nav button[aria-label="新建"]').trigger("click")
        await flushPromises()
        expect(wrapper.find(".scaffold-panel").exists()).toBe(true)
        expect(wrapper.find("#tab-new").exists()).toBe(false)
        expect(wrapper.find("#tab-sample").exists()).toBe(false)
        expect(wrapper.findComponent(SamplePanel).exists()).toBe(false)
    } finally {
        wrapper.unmount()
    }
})
