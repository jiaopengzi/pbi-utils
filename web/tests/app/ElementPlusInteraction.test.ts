/**
 * FilePath    : pbi-utils\web\tests\app\ElementPlusInteraction.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用真实组件与受控桥接验证字段校验、级联选择及模型切换.
 */
import { expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElInputNumber, ElSelect } from "element-plus"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import UiSettingsPanel from "@/app/UiSettingsPanel.vue"
import { expectConsoleWarnings } from "../support/testDiagnostics"

/** 仅返回固定内存预览, 不生成工程或调用外部模型. */
class InteractionTransport extends MockTransport {
    requests: { method: string; params: Record<string, string> }[] = []
    /** 记录请求, 返回显式模拟响应. */
    override postMessage(message: unknown) {
        this.requests.push(message as { method: string; params: Record<string, string> })
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 结构化桌面测试桥接.
        super.postMessage(message)
    }
}

it("必填数字清空后显示当前有效值, 不将 undefined 带入模型配置", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(ModelEditPanel, { props: { bridge: markRaw(bridge), project: previewProject, template: "calendar" } })
    try {
        const field = wrapper.findAllComponents(ElInputNumber)[0]!
        await field.setValue(2025)
        const input = field.get("input")
        await input.setValue("")
        await input.trigger("change")
        await input.trigger("blur")
        await flushPromises()
        expect(field.props("modelValue")).toBe(2025)
        expect(input.element.value).toBe("2025")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("常规设置的表单标签使用 Element Plus 支持的 for 属性关联控件", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(UiSettingsPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        expect(wrapper.get('label[for="ui-language"]').text()).not.toBe("")
        expect(wrapper.get('label[for="ui-recent-files"]').text()).not.toBe("")
        expect(wrapper.get('label[for="ui-dax-format-style"]').text()).not.toBe("")
        expect(wrapper.find("#ui-language").exists()).toBe(true)
        expect(wrapper.find("#ui-recent-files").exists()).toBe(true)
        expect(wrapper.find("#ui-dax-format-style").exists()).toBe(true)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("所属表切换清空旧目标和值, 不向新表发送旧对象", async () => {
    const transport = new InteractionTransport(),
        bridge = new BridgeClient(transport)
    const project = structuredClone(previewProject)
    project.model.tables.push({ ...project.model.tables[0]!, name: "另一张表" })
    const wrapper = mount(ModelEditPanel, { props: { bridge: markRaw(bridge), project } })
    await wrapper.findAllComponents(ElSelect)[1]!.setValue(project.model.measures[0]!.table)
    await wrapper.findAllComponents(ElSelect)[2]!.setValue(project.model.measures[0]!.name)
    await wrapper.get("form").trigger("submit")
    await flushPromises()
    expect(wrapper.emitted("updated")).toHaveLength(1)
    // 清空旧目标触发一次必填诊断; 无效目标的再次提交由守卫直接阻止.
    await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], async () => {
        await wrapper.findAllComponents(ElSelect)[1]!.setValue("另一张表")
        await flushPromises()
        expect(wrapper.findAllComponents(ElSelect)[2]!.props("modelValue")).toBe("")
        expect(wrapper.get("textarea").element.value).toBe("")
        await wrapper.get("form").trigger("submit")
        await flushPromises()
    })
    expect(transport.requests.filter((r) => r.method === "model.edit")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})
