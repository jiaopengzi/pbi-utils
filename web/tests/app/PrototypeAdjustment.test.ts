/**
 * FilePath    : pbi-utils\web\tests\app\PrototypeAdjustment.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证搜索二级定位、已移除功能不可检索及实例切换.
 */
import { expect, it, vi } from "vitest"
import { mount, flushPromises, DOMWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import SearchDialog from "@/app/SearchDialog.vue"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"

it("搜索描述命中二级目标, 无匹配时显示空态", async () => {
    const wrapper = mount(SearchDialog, { props: { disabled: false }, attachTo: document.body })
    const popup = new DOMWrapper(document.body)
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "/", bubbles: true }))
    await flushPromises()
    expect(popup.get('[role="dialog"]').isVisible()).toBe(true)
    await popup.get('[role="dialog"] input').setValue("UDF")
    expect(popup.findAll(".search-results button")).toHaveLength(1)
    await popup.get(".search-results button").trigger("click")
    expect(wrapper.emitted("select")?.[0]).toEqual(["model", "udf"])
    await popup.get('[role="dialog"] input').trigger("keydown", { key: "/" })
    await flushPromises()
    expect(popup.get('[role="dialog"]').isVisible()).toBe(false)
    await wrapper.get(".nav-search").trigger("click")
    await popup.get('[role="dialog"] input').setValue("RLS")
    expect(popup.findAll(".search-results button")).toHaveLength(0)
    await popup.get('[role="dialog"] input').setValue("关于")
    expect(popup.findAll(".search-results button")).toHaveLength(0)
    await popup.get('[role="dialog"] input').setValue("运行环境")
    expect(popup.findAll(".search-results button")).toHaveLength(1)
    await popup.get(".search-results button").trigger("click")
    expect(wrapper.emitted("select")?.at(-1)).toEqual(["system", "environment"])
    await wrapper.get(".nav-search").trigger("click")
    await popup.get('[role="dialog"] input').setValue("本地数据")
    expect(popup.findAll(".search-results button")).toHaveLength(1)
    await popup.get(".search-results button").trigger("click")
    expect(wrapper.emitted("select")?.at(-1)).toEqual(["system", "data"])
    await wrapper.get(".nav-search").trigger("click")
    await popup.get('[role="dialog"] input').setValue("not-found")
    expect(popup.text()).toContain("没有匹配结果")
    wrapper.unmount()
})

it("实例选择绑定已发现的 PID 与身份, 不允许网页指定连接地址", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(PowerBiPanel, { props: { bridge } })
    await flushPromises()
    await wrapper
        .findAll("button")
        .find((b) => b.text() === "连接此实例")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenCalledWith("project.connectLocal", { processId: "1000", instanceId: "mock-desktop-instance-1000" })
    expect(wrapper.emitted("connected")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})
