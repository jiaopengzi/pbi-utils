/**
 * FilePath    : pbi-utils\web\tests\app\ModelEditPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型编辑浮动进度、取消排空与宿主提交边界, 不提交真实模型修改.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElSelect } from "element-plus"
import { BridgeClient, type BridgePhase, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import ModelEditPanel from "@/app/ModelEditPanel.vue"

/** 建立受控保存响应, 只由测试推进终态和宿主提交阶段. */
async function setup() {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ModelEditPanel, { props: { project: structuredClone(previewProject), bridge, initialOperation: "table.set" } })
    wrapper.findAllComponents(ElSelect)[0]!.vm.$emit("update:modelValue", previewProject.model.tables[0]!.name)
    await flushPromises()
    await wrapper.get("textarea").setValue("保留模型说明草稿")
    await flushPromises()
    let resolveSave!: (project: ProjectSnapshot) => void
    let phase!: ((value: BridgePhase) => void) | undefined
    request.mockImplementationOnce((_method, _params, _event, onPhase) => {
        phase = onPhase
        return {
            id: "edit-pending",
            promise: new Promise<ProjectSnapshot>((resolve) => {
                resolveSave = resolve
            }),
        } as ReturnType<BridgeClient["request"]>
    })
    return {
        wrapper,
        request,
        finish: (project: ProjectSnapshot = structuredClone(previewProject)) => resolveSave(project),
        commit: () => phase?.("committing"),
        cleanup: () => {
            wrapper.unmount()
            bridge.dispose()
            vi.restoreAllMocks()
        },
    }
}

it("保存浮层不追加正文节点, 取消确认后表单仍锁定并等待权威终态", async () => {
    const { wrapper, request, finish, cleanup } = await setup()
    try {
        const form = wrapper.get("form").element
        const value = wrapper.get("textarea").element
        const childCount = wrapper.element.childElementCount
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(wrapper.element.childElementCount).toBe(childCount)
        expect(wrapper.find("[role=status]").exists()).toBe(false)
        expect(wrapper.text()).not.toContain("正在处理")
        expect(wrapper.get("form").element).toBe(form)
        expect(wrapper.get("textarea").element).toBe(value)
        expect(wrapper.get('button[type="submit"]').classes()).toContain("is-loading")
        const message = document.querySelector(".operation-message .operation-feedback")!
        expect(message.textContent).toContain("应用")
        const cancel = new DOMWrapper(message.querySelector<HTMLButtonElement>(".operation-feedback__cancel")!)
        request.mockImplementationOnce(() => ({ id: "cancel-edit", promise: Promise.resolve({ accepted: true }) }) as ReturnType<BridgeClient["request"]>)
        await cancel.trigger("click")
        await flushPromises()
        expect(request.mock.calls.at(-1)).toEqual(["request.cancel", { requestId: "edit-pending" }])
        expect(cancel.attributes("disabled")).toBeDefined()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get("textarea").attributes("disabled")).toBeDefined()
        finish()
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.emitted("updated")).toHaveLength(1)
        expect(wrapper.get("form").element).toBe(form)
        expect((value as HTMLTextAreaElement).value).toBe("保留模型说明草稿")
    } finally {
        cleanup()
    }
})

it("宿主进入提交阶段后浮层禁用取消, 仍等待真实保存结果", async () => {
    const { wrapper, request, commit, finish, cleanup } = await setup()
    try {
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        commit()
        await flushPromises()
        const cancel = new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-message .operation-feedback__cancel")!)
        expect(cancel.attributes("disabled")).toBeDefined()
        await cancel.trigger("click")
        expect(request.mock.calls.some(([method]) => method === "request.cancel")).toBe(false)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        finish()
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("连接模型提交成功不要求文件自动落盘, 立即回传模型并提示 Desktop 保存", async () => {
    const { wrapper, finish, cleanup } = await setup()
    try {
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        const project: ProjectSnapshot = {
            ...structuredClone(previewProject),
            projectPath: "model://desktop-direct",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "" },
        }
        finish(project)
        await flushPromises()
        expect(wrapper.emitted("updated")?.at(-1)?.[0]).toEqual(project)
        expect(document.querySelector(".el-message--success")?.textContent).toContain("当前连接模型已更新；请在 Power BI Desktop 保存报表。")
        expect(project.saveResult?.fileSaved).toBe(false)
    } finally {
        cleanup()
    }
})

it("连接模型提交后完整回读未证实时不显示成功, 即使宿主未返回错误", async () => {
    const { wrapper, finish, cleanup } = await setup()
    try {
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        finish({
            ...structuredClone(previewProject),
            projectPath: "model://unverified",
            connection: { kind: "desktop", label: "Power BI Desktop" },
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "" },
        })
        await flushPromises()
        expect(document.querySelector(".el-message--success")).toBeNull()
    } finally {
        cleanup()
    }
})
