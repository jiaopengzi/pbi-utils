/**
 * FilePath    : pbi-utils\web\tests\app\ConnectionsSemantics.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接页双语语义与移除能力探测后的只读实例扫描.
 */
import { mount, flushPromises } from "@vue/test-utils"
import { afterEach, expect, it, vi } from "vitest"
import App from "@/app/App.vue"
import SearchDialog from "@/app/SearchDialog.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale, t } from "@/i18n"

afterEach(() => vi.unstubAllGlobals())

it.each(["zh-cn", "en-us"] as const)("%s 连接语义和实例操作不包含UDF能力检查", async (locale) => {
    setLocale(locale)
    expect(t("app.connections")).toBe(locale === "zh-cn" ? "连接" : "Connections")
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.find(".udf-probe-button").exists()).toBe(false)
    expect(wrapper.find(".udf-capabilities").exists()).toBe(false)
    expect(wrapper.find(".desktop-file-button").exists()).toBe(true)
    expect(wrapper.find(".instance-refresh-button").exists()).toBe(true)
    expect(request.mock.calls.map(([method]) => method)).toEqual(["powerbi.instances"])
    wrapper.unmount()
    bridge.dispose()
})

/** 菜单、搜索和品牌入口实际到达同一连接页, 只执行显式模拟请求, 不选择或写入用户模型. */
it.each(["zh-cn", "en-us"] as const)("%s 连接页的默认导航、搜索与返回入口使用连接语义", async (locale) => {
    const transport = new MockTransport()
    const preferences = new BridgeClient(transport)
    await preferences.request("ui.saveSettings", { locale, showRecentFiles: true, daxFormatStyle: "long" }).promise
    preferences.dispose()
    vi.stubGlobal("chrome", { webview: transport })
    const wrapper = mount(App)
    try {
        await flushPromises()
        const connectionLabel = locale === "zh-cn" ? "连接" : "Connections"
        const connections = () => wrapper.get(`nav button[aria-label="${connectionLabel}"]`)
        expect(connections().attributes("aria-current")).toBe("page")
        expect(wrapper.find('.sidebar nav button[aria-label="AI"]').exists()).toBe(false)
        await connections().trigger("click")
        await flushPromises()
        expect(connections().attributes("aria-current")).toBe("page")
        expect(wrapper.find(".connections-content").exists()).toBe(true)
        expect(wrapper.findComponent(ConnectionPanel).exists()).toBe(true)
        await wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "模型" : "Model"}"]`).trigger("click")
        await flushPromises()
        await wrapper.get(".empty button").trigger("click")
        await flushPromises()
        expect(connections().attributes("aria-current")).toBe("page")
        const search = wrapper.getComponent(SearchDialog)
        await search.get(".nav-search").trigger("click")
        await flushPromises()
        const result = Array.from(document.querySelectorAll<HTMLButtonElement>(".search-results button")).find((button) =>
            button.textContent?.trim().startsWith(`${connectionLabel} ·`),
        )
        expect(result).toBeDefined()
        result!.click()
        await flushPromises()
        expect(search.emitted("select")?.at(-1)).toEqual(["connections", ""])
        expect(wrapper.findComponent(ConnectionPanel).exists()).toBe(true)
        await wrapper.get(`nav button[aria-label="${locale === "zh-cn" ? "模型" : "Model"}"]`).trigger("click")
        await flushPromises()
        await wrapper.get(".brand").trigger("click")
        await flushPromises()
        expect(wrapper.get(".brand").attributes("aria-label")).toBe(t("app.brandConnections"))
        expect(connections().attributes("aria-current")).toBe("page")
    } finally {
        wrapper.unmount()
    }
})
