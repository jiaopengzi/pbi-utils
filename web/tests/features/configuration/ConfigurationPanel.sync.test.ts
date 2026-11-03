/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.sync.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证隐藏配置回读向工作区发布新快照, 同时阻止取消和错误目标的迟到发布.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import ConfigurationRecovery from "@/features/configuration/ConfigurationRecovery.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import { previewProject } from "@/bridge/preview"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import type { ConfigurationContext, ProjectSnapshot } from "@/bridge/client"

type PendingState = "pending" | "saveFailed" | "verification"

/** 构造当前草稿、保存失败或提交待核验事实; 不用相同基线推定可自动清除恢复状态. */
function pendingSnapshot(project: ProjectSnapshot, state: PendingState): ProjectSnapshot {
    return {
        ...project,
        hasPendingChanges: state === "pending",
        saveResult:
            state === "pending"
                ? undefined
                : {
                      modelSaved: state === "verification",
                      fileSaved: false,
                      desktopSynchronized: false,
                      message: state,
                      errorCode: state === "saveFailed" ? "SAVE_FAILED" : undefined,
                  },
    }
}

/** 建立完整本机目标, 只控制宿主回读时间, 返回真实配置组件及其读取结果交接入口. */
async function setup(state?: PendingState) {
    const connected: ProjectSnapshot = {
        ...structuredClone(previewProject),
        projectPath: "model://configuration-sync",
        connection: { kind: "desktop", label: "Desktop", instanceId: "sync-instance", modelId: "sync-model" },
    }
    const project = state ? pendingSnapshot(connected, state) : connected
    const pending: ((value: ConfigurationContext) => void)[] = []
    const request = vi.fn((method: string) => ({
        id: method,
        promise:
            method === "scaffold.templates"
                ? Promise.resolve(new ScaffoldMock().handle(method, {}))
                : method === "configuration.context"
                  ? new Promise<ConfigurationContext>((done) => pending.push(done))
                  : Promise.resolve({ accepted: true }),
    }))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: { request } as never, project, section: "measures", active: false } })
    await flushPromises()
    /** 返回同一宿主目标的完整配置上下文, 不用名称推测身份或省略权限字段. */
    function reply(next: ProjectSnapshot, index = 0): void {
        pending[index]!({
            targetId: "configuration-sync-target",
            project: next,
            details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
            canWrite: true,
            canGenerateSample: false,
            isCreatedTarget: false,
            isSimulation: false,
        })
    }
    return { project, wrapper, request, reply }
}

it("隐藏配置读取新基线后同步工作区, 父级回填同一快照不会再读取或循环发布", async () => {
    const { project, wrapper, request, reply } = await setup()
    try {
        const fresh = { ...project, baselineId: "configuration-authoritative-baseline" }
        reply(fresh)
        await flushPromises()
        expect(wrapper.emitted("updated")).toEqual([[fresh]])
        await wrapper.setProps({ project: fresh })
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.context")).toHaveLength(1)
        expect(wrapper.emitted("updated")).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it("父级回填 JSON 等值但引用不同的完成保存事实不重复读取配置目标", async () => {
    const { project, wrapper, request, reply } = await setup()
    try {
        const saved = {
            ...project,
            baselineId: "completed-save-baseline",
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "saved" },
        }
        reply(saved)
        await flushPromises()
        expect(wrapper.emitted("updated")).toEqual([[saved]])
        await wrapper.setProps({ project: structuredClone(saved) })
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.context")).toHaveLength(1)
        expect(wrapper.emitted("updated")).toHaveLength(1)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed", "verification"] as const)("当前 %s 状态不能被自动配置读取的 clean 快照解除或恢复写能力", async (state) => {
    const { project, wrapper, request, reply } = await setup(state)
    try {
        reply({ ...project, baselineId: "automatic-clean-baseline", hasPendingChanges: false, saveResult: undefined })
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
        expect(wrapper.getComponent(ConfigurationRecovery).props("context")?.project).toEqual(project)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(true)
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
        const apply = wrapper.findAll("button").find((button) => button.text() === "写入模板度量值")!
        expect(apply.attributes("disabled")).toBeDefined()
        await apply.trigger("click")
        expect(request.mock.calls.some(([method]) => method === "configuration.apply")).toBe(false)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed", "verification"] as const)("读取开始后相同基线进入 %s, 迟到 clean 响应不能解除恢复状态", async (state) => {
    const { project, wrapper, request, reply } = await setup()
    try {
        const pending = pendingSnapshot(project, state)
        await wrapper.setProps({ project: pending })
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.context")).toHaveLength(2)
        reply(project)
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
        reply(project, 1)
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
        expect(wrapper.getComponent(ConfigurationRecovery).props("context")?.project).toEqual(pending)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(true)
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed", "verification"] as const)("自动配置读取返回真实 %s 时仍发布并展示恢复事实", async (state) => {
    const { project, wrapper, reply } = await setup()
    try {
        const pending = pendingSnapshot({ ...project, baselineId: "authoritative-pending-baseline" }, state)
        reply(pending)
        await flushPromises()
        expect(wrapper.emitted("updated")).toEqual([[pending]])
        expect(wrapper.getComponent(ConfigurationRecovery).props("context")?.project).toEqual(pending)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(true)
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed", "verification"] as const)("已有 %s 不能被其他普通 pending 回读替换或降级", async (state) => {
    const { project, wrapper, reply } = await setup(state)
    try {
        reply(pendingSnapshot({ ...project, baselineId: "different-pending-baseline" }, "pending"))
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
        expect(wrapper.getComponent(ConfigurationRecovery).props("context")?.project).toEqual(project)
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it.each(["pending", "saveFailed", "verification"] as const)("已绑定 clean 上下文在同基线进入 %s 时必须撤销写能力", async (state) => {
    const { project, wrapper, request, reply } = await setup()
    try {
        reply(project)
        await flushPromises()
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(false)
        const pending = pendingSnapshot(project, state)
        await wrapper.setProps({ project: pending })
        await flushPromises()
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
        expect(request.mock.calls.filter(([method]) => method === "configuration.context")).toHaveLength(2)
        reply(project, 1)
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
        expect(wrapper.getComponent(ConfigurationRecovery).props("context")?.project).toEqual(pending)
        expect(wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it("已绑定配置在相同项目和基线切换实际 modelId 时重新读取新模型", async () => {
    const { project, wrapper, request, reply } = await setup()
    try {
        reply(project)
        await flushPromises()
        const other = { ...project, connection: { ...project.connection!, modelId: "other-model" } }
        await wrapper.setProps({ project: other })
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.context")).toHaveLength(2)
        const fresh = { ...other, baselineId: "other-model-fresh" }
        reply(fresh, 1)
        await flushPromises()
        expect(wrapper.emitted("updated")).toEqual([[fresh]])
    } finally {
        wrapper.unmount()
    }
})

it("隐藏配置回读未变化快照不发布多余工作区更新", async () => {
    const { project, wrapper, reply } = await setup()
    try {
        reply(structuredClone(project))
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it.each(["projectPath", "instanceId", "modelId", "cancel", "unmount"] as const)("配置读取 %s 后的成功结果不能发布到原工作区", async (change) => {
    const { project, wrapper, reply } = await setup()
    try {
        const next = { ...project, baselineId: "late-configuration-baseline", connection: { ...project.connection! } }
        if (change === "projectPath") next.projectPath = "model://other"
        if (change === "instanceId") next.connection.instanceId = "other-instance"
        if (change === "modelId") next.connection.modelId = "other-model"
        if (change === "cancel") {
            await new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!).trigger("click")
            await flushPromises()
        }
        if (change === "unmount") wrapper.unmount()
        reply(next)
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
    } finally {
        if (change !== "unmount") wrapper.unmount()
    }
})
