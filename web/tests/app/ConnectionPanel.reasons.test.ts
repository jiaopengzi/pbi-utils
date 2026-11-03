/**
 * FilePath    : pbi-utils\web\tests\app\ConnectionPanel.reasons.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证共享卡片移除自动通知后, Service 原因仍按完整目录去重且不因切页或刷新重放.
 */
import { expect, it, vi } from "vitest"
import { ElMessage } from "element-plus"
import { flushPromises, mount } from "@vue/test-utils"
import { defineComponent, h, markRaw, ref } from "vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"

/** 同一已连接模型的目录原因仅变化时通知, 切来源/刷新/同应用重挂均不重复; 保留新原因反馈. */
it("Service 同一模型原因不因切页、目录刷新或重挂而重复提示", async () => {
    setLocale("zh-cn")
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const original = bridge.request.bind(bridge)
    let reason = "当前模型访问权限受限"
    vi.spyOn(bridge, "request").mockImplementation((method, params) => {
        const result =
            method === "powerbi.serviceStatus"
                ? {
                      account: { username: "preview@example.com", environmentName: "GlobalCloud", environmentLabel: "全球版" },
                      recentLogins: [],
                      isSimulation: false,
                  }
                : method === "powerbi.serviceRecentModels"
                  ? { items: [] }
                  : method === "powerbi.serviceModels"
                    ? [{ id: "service-current", name: "原模型", workspace: "工作区", workspaceId: "workspace", unavailableReason: reason }]
                    : method === "powerbi.instances"
                      ? { timestamp: new Date().toISOString(), instances: [] }
                      : undefined
        return result === undefined ? original(method, params) : { id: method, promise: Promise.resolve(result) }
    })
    const visible = ref(true)
    const activeProject: ProjectSnapshot = { ...previewProject, connection: { kind: "powerbi-service", label: "Power BI Service", modelId: "service-current" } }
    const harness = defineComponent({ setup: () => () => (visible.value ? h(ConnectionPanel, { bridge, activeProject }) : null) })
    const wrapper = mount(harness)
    try {
        await flushPromises()
        expect(document.querySelector(".el-message")?.textContent).toContain(reason)
        ElMessage.closeAll()
        await flushPromises()
        await wrapper.findAll('.connection-source-tabs [role="tab"]')[0]!.trigger("click")
        await flushPromises()
        await wrapper.findAll('.connection-source-tabs [role="tab"]')[1]!.trigger("click")
        await flushPromises()
        expect(document.querySelector(".el-message")).toBeNull()
        await wrapper.get(".connection-account button").trigger("click")
        await flushPromises()
        expect(document.querySelector(".el-message")).toBeNull()
        visible.value = false
        await flushPromises()
        visible.value = true
        await flushPromises()
        expect(document.querySelector(".el-message")).toBeNull()
        reason = "当前模型访问权限已变化"
        await wrapper.get(".connection-account button").trigger("click")
        await flushPromises()
        expect(document.querySelector(".el-message")?.textContent).toContain(reason)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        ElMessage.closeAll()
    }
})
