/**
 * FilePath    : pbi-utils\web\tests\app\ModelEditPanel.selection.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证原生表说明选区的事件捕获、精确字段恢复及用户操作/目标变更/卸载取消边界, 不模拟原生撤销栈.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElButton, ElForm, ElSelect } from "element-plus"
import type { FormInstance } from "element-plus"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import type { ModelEditSession } from "@/features/model/modelEditSession"

const draft = "Draft selection stays."

/** 使用真实表单和 select/blur 事件生成会话; 返回独立模型/桥接和卸载前已捕获的会话, 失败清理桥接. */
async function savedSelection(event: "select" | "blur" = "select") {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const project = structuredClone(previewProject)
    project.model.tables.push({ ...structuredClone(project.model.tables[0]!), name: "另一张表", description: draft })
    const wrapper = mount(ModelEditPanel, { props: { bridge, project, initialOperation: "table.set" } })
    try {
        await wrapper.findAllComponents(ElSelect)[0]!.setValue("销售")
        await wrapper.get("textarea").setValue(draft)
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        input.setSelectionRange(2, 7, "forward")
        await wrapper.get("textarea").trigger(event)
        const session = wrapper.emitted("draft-change")!.at(-1)![0] as ModelEditSession
        return { bridge, project, session }
    } catch (cause) {
        bridge.dispose()
        throw cause
    } finally {
        wrapper.unmount()
    }
}

it.each(["select", "blur"] as const)("原生%s事件捕获的选区能在重新挂载后恢复, 不自动聚焦", async (event) => {
    const saved = await savedSelection(event)
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        await flushPromises()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        expect(input.value).toBe(draft)
        expect([input.selectionStart, input.selectionEnd, input.selectionDirection]).toEqual([2, 7, "forward"])
        expect(document.activeElement).not.toBe(input)
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("相同文本的另一字段不继承旧选区", async () => {
    const saved = await savedSelection()
    const session = { ...saved.session, objectName: "另一张表" }
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: session, initialOperation: "table.set" } })
    try {
        await flushPromises()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        expect(input.value).toBe(draft)
        expect([input.selectionStart, input.selectionEnd]).toEqual([draft.length, draft.length])
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("权威值替换未编辑文本时不恢复旧文本选区", async () => {
    const saved = await savedSelection()
    const project = structuredClone(saved.project)
    project.model.tables[0]!.description = "最新值"
    const session: ModelEditSession = { ...saved.session, sourceValue: draft }
    const wrapper = mount(ModelEditPanel, { props: { bridge: saved.bridge, project, editSession: session, initialOperation: "table.set" } })
    try {
        await flushPromises()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        expect(input.value).toBe("最新值")
        expect([input.selectionStart, input.selectionEnd]).toEqual([3, 3])
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("等待DOM回填期间用户已改变选区时不迟到覆盖, 即使尚未收到select事件", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        input.setSelectionRange(1, 1)
        await flushPromises()
        expect([input.selectionStart, input.selectionEnd]).toEqual([1, 1])
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("恢复等待期间切换到相同文本的另一目标时撤销旧选区恢复", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        wrapper.findAllComponents(ElSelect)[0]!.vm.$emit("update:modelValue", "另一张表")
        await flushPromises()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        expect(wrapper.findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("另一张表")
        expect(input.value).toBe(draft)
        expect([input.selectionStart, input.selectionEnd]).toEqual([draft.length, draft.length])
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("恢复等待期间用户输入新文本时不写回旧文本的选区", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        input.value = "New input"
        input.setSelectionRange(1, 1)
        input.dispatchEvent(new Event("input", { bubbles: true }))
        await flushPromises()
        expect(input.value).toBe("New input")
        expect([input.selectionStart, input.selectionEnd]).toEqual([1, 1])
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("相同文本的不同字段使用不同textarea, 不让原生编辑历史跨目标复用", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        await flushPromises()
        const previous = wrapper.get<HTMLTextAreaElement>("textarea").element
        await wrapper.findAllComponents(ElSelect)[0]!.setValue("另一张表")
        await flushPromises()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        expect(input.value).toBe(draft)
        expect(input).not.toBe(previous)
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("隐藏编辑器真实禁用且select/提交/恢复事件不触发宿主或丢失冲突输入", async () => {
    const saved = await savedSelection()
    const request = vi.spyOn(saved.bridge, "request")
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set", active: false } })
    try {
        const project = structuredClone(saved.project)
        project.model.tables[0]!.description = "隐藏期间的当前值"
        await wrapper.setProps({ project })
        await flushPromises()
        expect(wrapper.get("textarea").attributes("disabled")).toBeDefined()
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(document.querySelector(".el-message--warning")).toBeNull()
        await wrapper.get("form").trigger("submit")
        const reload = wrapper.findAllComponents(ElButton).find((button) => button.text() === "载入当前值")!
        reload.vm.$emit("click", new MouseEvent("click"))
        await flushPromises()
        expect(wrapper.get("textarea").element).toHaveProperty("value", draft)
        expect(request).not.toHaveBeenCalled()
        await wrapper.setProps({ active: true })
        reload.vm.$emit("click", new MouseEvent("click"))
        await flushPromises()
        expect(wrapper.get("textarea").element).toHaveProperty("value", "隐藏期间的当前值")
    } finally {
        wrapper.unmount()
        request.mockRestore()
        saved.bridge.dispose()
    }
})

it.each(["hidden", "unmounted"] as const)("表单校验等待期间编辑器%s, 迟到有效校验也不能开始提交", async (state) => {
    const saved = await savedSelection()
    const request = vi.spyOn(saved.bridge, "request")
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    let complete!: (valid: boolean) => void
    const validation = new Promise<boolean>((resolve) => {
        complete = resolve
    })
    let restoreValidation: (() => void) | undefined
    let unmounted = false
    try {
        const form = wrapper.getComponent(ElForm).vm.$.exposed as Pick<FormInstance, "validate">
        const validate = vi.spyOn(form, "validate").mockImplementation(() => validation)
        restoreValidation = () => validate.mockRestore()
        const input = wrapper.get<HTMLTextAreaElement>("textarea").element
        await wrapper.get("form").trigger("submit")
        if (state === "hidden") await wrapper.setProps({ active: false })
        else {
            wrapper.unmount()
            unmounted = true
        }
        complete(true)
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
        expect(wrapper.emitted("busy")).toBeUndefined()
        expect(input.value).toBe(draft)
    } finally {
        complete(true)
        restoreValidation?.()
        request.mockRestore()
        if (!unmounted) wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("目标已切换但DOM尚未更新时blur不把旧控件选区绑定给新字段", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    try {
        await flushPromises()
        wrapper.findAllComponents(ElSelect)[0]!.vm.$emit("update:modelValue", "另一张表")
        wrapper.get("textarea").element.dispatchEvent(new FocusEvent("blur"))
        await flushPromises()
        const session = wrapper.emitted("draft-change")!.at(-1)![0] as ModelEditSession
        const next = mount(ModelEditPanel, { props: { ...saved, editSession: session, initialOperation: "table.set" } })
        try {
            await flushPromises()
            const input = next.get<HTMLTextAreaElement>("textarea").element
            expect(input.value).toBe(draft)
            expect([input.selectionStart, input.selectionEnd]).toEqual([draft.length, draft.length])
        } finally {
            next.unmount()
        }
    } finally {
        wrapper.unmount()
        saved.bridge.dispose()
    }
})

it("卸载取消尚未完成的恢复, 不写入已脱离的textarea", async () => {
    const saved = await savedSelection()
    const wrapper = mount(ModelEditPanel, { props: { ...saved, editSession: saved.session, initialOperation: "table.set" } })
    const restore = vi.spyOn(wrapper.get<HTMLTextAreaElement>("textarea").element, "setSelectionRange")
    try {
        wrapper.unmount()
        await flushPromises()
        expect(restore).not.toHaveBeenCalled()
    } finally {
        restore.mockRestore()
        saved.bridge.dispose()
    }
})
