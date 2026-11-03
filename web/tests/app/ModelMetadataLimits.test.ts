/**
 * FilePath    : pbi-utils\web\tests\app\ModelMetadataLimits.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 Service 架构快照不将未提供的 DAX、关系与 UDF 显示为完整定义.
 */
import { expect, it } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import { markRaw } from "vue"
import ModelPanel from "@/app/ModelPanel.vue"
import CodeBlock from "@/features/code/CodeBlock.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"

it("架构只读模型明确显示未提供范围, 隐藏空 DAX 与零角色/函数的假结论", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const project = structuredClone(previewProject)
    project.projectPath = "model://limited"
    project.connection = { kind: "powerbi-service", label: "Power BI Service", isLimited: true }
    project.model.compatibilityLevel = 0
    project.model.measures[0]!.expression = ""
    const wrapper = mount(ModelPanel, { props: { project, bridge } })
    await flushPromises()
    expect(Array.from(document.querySelectorAll(".el-message"), (message) => message.textContent).join(" ")).toContain(
        "分区、兼容级别、关系、角色与 UDF 未提供",
    )
    expect(wrapper.findComponent(CodeBlock).exists()).toBe(false)
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "表格模型")!
        .trigger("click")
    expect(wrapper.text()).toContain("未提供关系与角色信息")
    expect(wrapper.text()).not.toContain("0 个角色")
    await wrapper
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "UDF")!
        .trigger("click")
    expect(wrapper.text()).toContain("不提供兼容级别或 UDF 定义")
    expect(wrapper.text()).not.toContain("0 个模型函数")
    wrapper.unmount()
    bridge.dispose()
})
