/**
 * FilePath    : pbi-utils\web\tests\app\ModelEditing.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证来源读写、已返回 Service DAX、直接模型编辑、顶栏恢复与取消.
 */
import { expect, it, vi } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElMessageBox, type MessageBoxData } from "element-plus"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import App from "@/app/App.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import MeasuresPanel from "@/app/MeasuresPanel.vue"
import PowerBiPanel from "@/app/PowerBiPanel.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import CodeBlock from "@/features/code/CodeBlock.vue"
import * as messages from "@/ui/message"

it.each(["powerbi-service", "ssas-xmla"] as const)("%s 只读模式保留定义查看并隐藏创建操作", async (kind) => {
    const project: ProjectSnapshot = {
        ...structuredClone(previewProject),
        projectPath: "model://read-only",
        connection: { kind, label: kind, isLimited: kind === "powerbi-service" },
    }
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(ModelPanel, { props: { project, bridge, initialTab: "time" } })
    await flushPromises()
    expect(wrapper.findAll('[role="tab"]').map((t) => t.text())).toEqual(["度量值", "表格模型", "UDF"])
    expect(wrapper.findComponent(ModelEditPanel).exists()).toBe(false)
    expect(wrapper.findComponent(CodeBlock).props("code")).toBe(project.model.measures[0]!.expression)
    wrapper.unmount()
    bridge.dispose()
})

it("Desktop 可编辑且没有变更预览, 打开页面不会隐式保存", async () => {
    const project: ProjectSnapshot = {
        ...structuredClone(previewProject),
        projectPath: "model://desktop",
        connection: { kind: "desktop", label: "Power BI Desktop" },
    }
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ModelPanel, { props: { project, bridge } })
    expect(wrapper.text()).not.toContain("变更预览")
    expect(wrapper.findComponent(MeasuresPanel).exists()).toBe(true)
    expect(wrapper.findAll('[role="tab"]').map((t) => t.text())).toContain("日期 / 时间智能")
    expect(request.mock.calls.some(([method]) => method === "model.save")).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})

it("顶栏仅在保存未完成时提供重试, 放弃需要确认, 重试直接交给宿主", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const request = vi.spyOn(BridgeClient.prototype, "request")
    const confirm = vi.spyOn(ElMessageBox, "confirm")
    const wrapper = mount(App)
    try {
        await flushPromises()
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(PowerBiPanel).vm.$emit("connected", structuredClone(previewProject))
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(ModelPanel).vm.$emit("updated", { ...structuredClone(previewProject), hasPendingChanges: true })
        await flushPromises()
        const saveButton = () => wrapper.findAll(".header-actions button").find((b) => b.text() === "重试保存")
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(ConnectionPanel).props("disabled")).toBe(true)
        expect(wrapper.findAllComponents({ name: "ElTabPane" }).every((tab) => tab.props("disabled"))).toBe(true)
        expect(wrapper.get(".connection-center .desktop-file-button").attributes("disabled")).toBeDefined()
        await wrapper.get(".connection-center .desktop-file-button").trigger("click")
        expect(request.mock.calls.some(([method]) => method === "project.open")).toBe(false)
        expect(wrapper.get(".header-actions").text()).not.toContain("切换模型")
        confirm.mockRejectedValueOnce("cancel")
        await wrapper
            .findAll(".header-actions button")
            .find((b) => b.text() === "放弃修改")!
            .trigger("click")
        await flushPromises()
        expect(request.mock.calls.some(([method]) => method === "model.save")).toBe(false)
        expect(wrapper.findAll(".header-actions .el-tag").some((t) => t.text() === "未保存")).toBe(true)
        await saveButton()!.trigger("click")
        await flushPromises()
        expect(confirm).toHaveBeenCalledTimes(1)
        expect(request.mock.calls.filter(([method]) => method === "model.save")).toHaveLength(1)
        expect(saveButton()).toBeUndefined()
        expect(wrapper.findAll(".header-actions .el-tag").some((t) => t.text() === "未保存")).toBe(false)
    } finally {
        wrapper.unmount()
        request.mockRestore()
        confirm.mockRestore()
        vi.unstubAllGlobals()
    }
})

it("连接 Desktop 完成快照不重复通知, 保留文件未保存事实且不显示恢复入口", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const message = vi.spyOn(messages, "showMessage")
    const wrapper = mount(App)
    try {
        await flushPromises()
        const project: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://desktop-direct",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "" },
            hasPendingChanges: false,
        }
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(PowerBiPanel).vm.$emit("connected", project)
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        const messagesBeforeReadback = message.mock.calls.length
        wrapper.findComponent(ModelPanel).vm.$emit("updated", project)
        await flushPromises()
        expect(message).toHaveBeenCalledTimes(messagesBeforeReadback)
        expect(wrapper.get(".content").text()).not.toContain("当前连接模型已更新；请在 Power BI Desktop 保存报表。")
        expect(wrapper.get(".header-actions").text()).not.toContain("重试保存")
        expect(wrapper.get(".header-actions").text()).not.toContain("放弃修改")
        expect(wrapper.get(".header-actions").text()).not.toContain("保存未完成")
        expect(wrapper.get(".header-actions").text()).not.toContain("未保存")
        expect(wrapper.findComponent(ModelPanel).props("project").saveResult?.fileSaved).toBe(false)
        project.warnings = ["日期表数据尚未刷新；请在 Power BI Desktop 显式刷新对应日期表。"]
        wrapper.findComponent(ModelPanel).vm.$emit("updated", { ...project })
        await flushPromises()
        expect(message).toHaveBeenCalledTimes(messagesBeforeReadback)
        expect(wrapper.get(".content").text()).not.toContain(project.warnings[0])
    } finally {
        wrapper.unmount()
        message.mockRestore()
        vi.unstubAllGlobals()
    }
})

it("Desktop 未知提交保持只读, 重新读取只调用恢复回读且使用准确确认文案", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const request = vi.spyOn(BridgeClient.prototype, "request")
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockResolvedValue("confirm" as MessageBoxData)
    const wrapper = mount(App)
    try {
        await flushPromises()
        const project: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://desktop-unknown",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            hasPendingChanges: true,
            saveResult: { modelSaved: false, fileSaved: false, desktopSynchronized: false, errorCode: "EXECUTION_NEEDS_VERIFICATION", message: "需要核验" },
        }
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(PowerBiPanel).vm.$emit("connected", project)
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        const header = wrapper.get(".header-actions")
        expect(header.text()).toContain("待核验")
        expect(header.text()).not.toContain("重试保存")
        expect(header.text()).not.toContain("放弃修改")
        const reload = header.findAll("button").find((button) => button.text() === "重新读取模型")!
        expect(reload).toBeDefined()
        await reload.trigger("click")
        await flushPromises()
        expect(confirm.mock.calls.at(-1)?.[0]).toContain("不会撤销已发生的修改")
        expect(request.mock.calls.filter(([method]) => method === "model.discard")).toHaveLength(1)
        expect(request.mock.calls.filter(([method]) => method === "model.save")).toHaveLength(0)
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        request.mockRestore()
        confirm.mockRestore()
        vi.unstubAllGlobals()
    }
})

it("Desktop 返回模型已提交但完整回读未证实时保持只读, 无错误码也不能误报成功", async () => {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    try {
        await flushPromises()
        const project: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://desktop-incomplete",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "" },
        }
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.findComponent(PowerBiPanel).vm.$emit("connected", project)
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        expect(wrapper.get(".header-actions").text()).toContain("待核验")
        expect(wrapper.get(".header-actions").text()).toContain("重新读取模型")
        expect(wrapper.get(".content").text()).not.toContain("当前连接模型已更新")
        expect(
            wrapper
                .findAll("button")
                .find((button) => button.text() === "新建度量值")
                ?.attributes("disabled"),
        ).toBeDefined()
    } finally {
        wrapper.unmount()
        vi.unstubAllGlobals()
    }
})
