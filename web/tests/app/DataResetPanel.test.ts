/**
 * FilePath    : pbi-utils\web\tests\app\DataResetPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证重置入口不依赖 其他设置初始化, 两次宿主确认和取消不影响本地配置.
 */
import { expect, it, vi } from "vitest"
import { markRaw } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import { BridgeClient, BridgeError } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import SettingsWorkspace from "@/app/SettingsWorkspace.vue"
import HostDialog from "@/app/HostDialog.vue"

it("MCP 配置读取失败时重置按钮仍可用, 设置挂载不自动请求重置", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const original = bridge.request.bind(bridge)
    const requests = vi
        .spyOn(bridge, "request")
        .mockImplementation((...args) =>
            args[0] === "mcp.configuration"
                ? ({ id: "failed-mcp", promise: Promise.reject(new BridgeError("MCP_UNAVAILABLE", "无法读取 MCP 配置")) } as ReturnType<typeof bridge.request>)
                : original(...args),
        )
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    try {
        await flushPromises()
        expect(wrapper.get(".data-reset-button").attributes("disabled")).toBeUndefined()
        expect(requests.mock.calls.some(([method]) => method === "app.resetUserData")).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it.each([1, 2])("第 %i 次宿主确认取消后不重置, 再次打开从第一步开始", async (cancelAt) => {
    const bridge = markRaw(new BridgeClient(new MockTransport(true)))
    const before = await bridge.request("ui.settings", {}).promise
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    const dialog = mount(HostDialog, { props: { bridge }, attachTo: document.body })
    try {
        await flushPromises()
        await wrapper.get(".data-reset-button").trigger("click")
        await flushPromises()
        expect(document.body.textContent).toContain("重置全部本地数据")
        expect(document.body.textContent).toContain("备份")
        expect(wrapper.get(".data-reset-button").attributes("disabled")).toBeDefined()
        if (cancelAt === 2) {
            document.querySelector<HTMLButtonElement>(".el-dialog__footer .el-button--primary")!.click()
            await flushPromises()
            expect(document.body.textContent).toContain("最终确认重置")
        }
        document.querySelector<HTMLButtonElement>(".el-dialog__footer button")!.click()
        await flushPromises()
        expect(wrapper.get(".data-reset-button").attributes("disabled")).toBeUndefined()
        expect(await bridge.request("ui.settings", {}).promise).toEqual(before)
        await wrapper.get(".data-reset-button").trigger("click")
        await flushPromises()
        expect(document.body.textContent).toContain("重置全部本地数据")
        expect(document.body.textContent).not.toContain("最终确认重置")
        document.querySelector<HTMLButtonElement>(".el-dialog__footer button")!.click()
        await flushPromises()
    } finally {
        wrapper.unmount()
        dialog.unmount()
        bridge.dispose()
    }
})

it("模拟两次确认后只报告演示结果, 不改变现有配置或重载页面", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport(true)))
    const before = await bridge.request("ui.settings", {}).promise
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    const dialog = mount(HostDialog, { props: { bridge }, attachTo: document.body })
    try {
        await flushPromises()
        await wrapper.get(".data-reset-button").trigger("click")
        await flushPromises()
        document.querySelector<HTMLButtonElement>(".el-dialog__footer .el-button--primary")!.click()
        await flushPromises()
        expect(document.body.textContent).toContain("最终确认重置")
        document.querySelector<HTMLButtonElement>(".el-dialog__footer .el-button--primary")!.click()
        await flushPromises()
        expect(document.body.textContent).toContain("未删除任何本地数据")
        expect(await bridge.request("ui.settings", {}).promise).toEqual(before)
        expect(wrapper.get(".data-reset-button").attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
        dialog.unmount()
        bridge.dispose()
    }
})
