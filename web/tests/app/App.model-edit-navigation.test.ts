/**
 * FilePath    : pbi-utils\web\tests\app\App.model-edit-navigation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证真实应用导航和设置语言保存后表说明会话保持、目标隔离与在途生命周期.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { ElSelect } from "element-plus"
import App from "@/app/App.vue"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import UiSettingsPanel from "@/app/UiSettingsPanel.vue"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"

afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
})

/** 挂载完整应用并由受控宿主连接到单表模型; 产品导航、设置和编辑组件保持真实. */
async function setup(visitTables = true) {
    vi.stubGlobal("chrome", { webview: new MockTransport() })
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get('nav button[aria-label="连接"]').trigger("click")
    await flushPromises()
    const connection = wrapper.getComponent(ConnectionPanel)
    const bridge = connection.props("bridge") as BridgeClient
    const project = structuredClone(previewProject)
    connection.vm.$emit("connected", project)
    await flushPromises()
    if (visitTables) {
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        const editor = wrapper.getComponent(ModelEditPanel)
        await editor.findAllComponents(ElSelect)[0]!.setValue("销售")
        await flushPromises()
    }
    let authoritative = project
    const original = bridge.request.bind(bridge)
    const request = vi.spyOn(bridge, "request").mockImplementation((method, ...args) => {
        if (method === "project.inspect") return { id: "inspect-current", promise: Promise.resolve(authoritative) }
        const response = original(method, ...args)
        if (method === "configuration.context") {
            const context = response as ReturnType<typeof bridge.request<"configuration.context">>
            return { ...context, promise: context.promise.then((value) => ({ ...value, project: authoritative })) }
        }
        return response
    })
    return {
        wrapper,
        bridge,
        project,
        request,
        setAuthoritative: (snapshot: ProjectSnapshot) => {
            authoritative = snapshot
        },
    }
}

/** 通过侧栏和真实设置下拉项保存English, 返回模型表格页; 不直接更改语言单例. */
async function navigateLanguage(wrapper: ReturnType<typeof mount>) {
    await wrapper.get('nav button[aria-label="设置"]').trigger("click")
    await flushPromises()
    expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
    await wrapper.getComponent(UiSettingsPanel).get("#ui-language").trigger("click")
    await flushPromises()
    await new DOMWrapper(document.body)
        .findAll(".el-select-dropdown__item")
        .find((option) => option.text() === "English")!
        .trigger("click")
    await flushPromises()
    await wrapper.get('nav button[aria-label="Model"]').trigger("click")
    await flushPromises()
    await wrapper.get("#tab-tables").trigger("click")
    await flushPromises()
}

it("度量值、表格与模板页往返不重读模型, 同基线刷新及断开重连重新读取", async () => {
    const { wrapper, request, project } = await setup()
    try {
        const reads = () => request.mock.calls.filter(([method]) => ["configuration.context", "model.tableStatistics", "model.exportLocation"].includes(method))
        /** 顺序完成一次页签切换及渲染, tab 为目标页签, 不并发导航以免绕过实际忙状态. */
        async function visit(tab: string): Promise<void> {
            await wrapper.get(`#tab-${tab}`).trigger("click")
            await flushPromises()
            expect(wrapper.get(`#tab-${tab}`).attributes("aria-selected")).toBe("true")
        }
        await visit("templates")
        await visit("measures")
        await visit("tables")
        await visit("measures")
        await visit("templates")
        await visit("tables")
        expect(reads()).toEqual([])
        expect(document.querySelector(".operation-feedback")).toBeNull()
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "刷新模型")!
            .trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "project.inspect")).toHaveLength(1)
        expect(reads().filter(([method]) => method === "configuration.context")).toHaveLength(1)
        expect(reads().filter(([method]) => method === "model.tableStatistics")).toHaveLength(1)
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "断开连接")!
            .trigger("click")
        await flushPromises()
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.getComponent(ConnectionPanel).vm.$emit("connected", structuredClone(project))
        await flushPromises()
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        expect(reads().filter(([method]) => method === "configuration.context")).toHaveLength(2)
        expect(reads().filter(([method]) => method === "model.tableStatistics")).toHaveLength(2)
    } finally {
        wrapper.unmount()
    }
})

it("表说明经过设置保存English后保留原textarea、草稿和选区, 只重建模型面板", async () => {
    const { wrapper, request } = await setup()
    try {
        await wrapper.get(".copy-change textarea").setValue("Draft stays across language")
        const previousInput = wrapper.get<HTMLTextAreaElement>(".copy-change textarea").element
        previousInput.setSelectionRange(6, 13, "backward")
        const previousPanel = wrapper.getComponent(ModelPanel).vm.$.uid
        await navigateLanguage(wrapper)
        expect(wrapper.getComponent(ModelPanel).vm.$.uid).not.toBe(previousPanel)
        const editor = wrapper.getComponent(ModelEditPanel)
        expect(editor.findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("销售")
        expect(editor.get<HTMLTextAreaElement>('textarea[aria-label="Value"]').element.value).toBe("Draft stays across language")
        const input = editor.get<HTMLTextAreaElement>("textarea").element
        expect(input).toBe(previousInput)
        expect([input.selectionStart, input.selectionEnd, input.selectionDirection]).toEqual([6, 13, "backward"])
        expect(editor.get("textarea").attributes("disabled")).toBeUndefined()
        expect(editor.get('button[type="submit"]').text()).toBe("Apply")
        expect(request.mock.calls.some(([method, params]) => method === "ui.saveSettings" && "locale" in params && params.locale === "en-us")).toBe(true)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        wrapper.unmount()
    }
})

it("表说明原有冲突经过设置导航仍保留草稿和禁用事实, 可以显式载入当前值恢复", async () => {
    const { wrapper, project, setAuthoritative } = await setup()
    try {
        await wrapper.get(".copy-change textarea").setValue("Must keep conflicted draft")
        wrapper.get<HTMLTextAreaElement>(".copy-change textarea").element.setSelectionRange(3, 10, "forward")
        const changed = structuredClone(project)
        changed.model.tables[0]!.description = "最新权威说明"
        setAuthoritative(changed)
        wrapper.getComponent(ModelPanel).vm.$emit("updated", changed)
        await flushPromises()
        expect(wrapper.get('.copy-change button[type="submit"]').attributes("disabled")).toBeDefined()
        await navigateLanguage(wrapper)
        expect(wrapper.get(".copy-change textarea").element).toHaveProperty("value", "Must keep conflicted draft")
        const input = wrapper.get<HTMLTextAreaElement>(".copy-change textarea").element
        expect([input.selectionStart, input.selectionEnd, input.selectionDirection]).toEqual([3, 10, "forward"])
        expect(wrapper.get('.copy-change button[type="submit"]').attributes("disabled")).toBeDefined()
        await wrapper
            .findAll(".copy-change button")
            .find((button) => button.text() === "Load current value")!
            .trigger("click")
        expect(wrapper.get(".copy-change textarea").element).toHaveProperty("value", "最新权威说明")
        expect(wrapper.get('.copy-change button[type="submit"]').attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it.each(["changed", "removed"] as const)("设置页隐藏期间权威字段%s, 返回时按保存基线核验而非静默覆盖草稿", async (kind) => {
    const { wrapper, project, setAuthoritative } = await setup()
    try {
        await wrapper.get(".copy-change textarea").setValue("Hidden-page draft")
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        await flushPromises()
        const changed = structuredClone(project)
        changed.baselineId = "hidden-change"
        if (kind === "removed") changed.model.tables = []
        else changed.model.tables[0]!.description = "隐藏期间最新说明"
        setAuthoritative(changed)
        await wrapper
            .findAll(".header-actions button")
            .find((button) => button.text() === "刷新模型")!
            .trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
        await wrapper.get('nav button[aria-label="模型"]').trigger("click")
        await flushPromises()
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        const editor = wrapper.getComponent(ModelEditPanel)
        expect(editor.get("textarea").element.value).toBe(kind === "removed" ? "" : "Hidden-page draft")
        expect(editor.findAllComponents(ElSelect)[0]!.props("modelValue")).toBe(kind === "removed" ? "" : "销售")
        expect(editor.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(editor.findAll("button").some((button) => button.text() === "载入当前值")).toBe(kind === "changed")
    } finally {
        wrapper.unmount()
    }
})

it("换绑和断开清空会话, 同名目标与迟到旧会话不能污染新模型", async () => {
    const { wrapper, project, setAuthoritative } = await setup()
    try {
        await wrapper.get(".copy-change textarea").setValue("Old model draft")
        const oldSession = wrapper.getComponent(ModelEditPanel).props("editSession")
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        const next = { ...structuredClone(project), projectId: "another-model", projectPath: "Another.pbip" }
        setAuthoritative(next)
        wrapper.getComponent(ConnectionPanel).vm.$emit("connected", next)
        await flushPromises()
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        wrapper.getComponent(ModelEditPanel).vm.$emit("draft-change", oldSession)
        await flushPromises()
        expect(wrapper.getComponent(ModelEditPanel).props("editSession")?.targetIdentity).not.toBe(oldSession?.targetIdentity)
        expect(wrapper.get(".copy-change textarea").element).toHaveProperty("value", "")
        await wrapper.getComponent(ModelEditPanel).findAllComponents(ElSelect)[0]!.setValue("销售")
        await flushPromises()
        await wrapper.get(".copy-change textarea").setValue("Before disconnect")
        await wrapper
            .findAll(".header-actions button")
            .find((button) => button.text() === "断开连接")!
            .trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
        expect(wrapper.findComponent(ModelEditPanel).exists()).toBe(false)
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        wrapper.getComponent(ConnectionPanel).vm.$emit("connected", next)
        await flushPromises()
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        expect(wrapper.get(".copy-change textarea").element).toHaveProperty("value", "")
        expect(wrapper.getComponent(ModelEditPanel).findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("")
    } finally {
        wrapper.unmount()
    }
})

it("属性编辑器首次Tables访问才创建, 子tab和设置导航只隐藏当前编辑器", async () => {
    const { wrapper, request } = await setup(false)
    try {
        expect(wrapper.findComponent(ModelEditPanel).exists()).toBe(false)
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        const editor = wrapper.getComponent(ModelEditPanel)
        await editor.findAllComponents(ElSelect)[0]!.setValue("销售")
        await editor.get("textarea").setValue("Only this editor persists")
        const input = editor.get("textarea").element
        await wrapper.get("#tab-measures").trigger("click")
        await flushPromises()
        expect(editor.isVisible()).toBe(false)
        expect(editor.get("textarea").attributes("disabled")).toBeDefined()
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(false)
        expect(wrapper.getComponent(ModelEditPanel).get("textarea").element).toBe(input)
        request.mockClear()
        await editor.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
        expect(editor.get("textarea").element).toHaveProperty("value", "Only this editor persists")
    } finally {
        wrapper.unmount()
    }
})

it("模型面板和属性编辑器busy独立汇总, 单个通道结束不能解除另一通道导航锁", async () => {
    const { wrapper } = await setup()
    try {
        const panel = wrapper.getComponent(ModelPanel)
        const editor = wrapper.getComponent(ModelEditPanel)
        const ownEvents = panel.emitted("busy")?.length ?? 0
        editor.vm.$emit("busy", true)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeDefined()
        await panel.get("#tab-measures").trigger("click")
        expect(panel.get("#tab-tables").attributes("aria-selected")).toBe("true")
        expect(panel.emitted("busy")?.length ?? 0).toBe(ownEvents)
        panel.vm.$emit("busy", false)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeDefined()
        panel.vm.$emit("busy", true)
        editor.vm.$emit("busy", false)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeDefined()
        panel.vm.$emit("busy", false)
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("相同projectId但完整宿主目标变化也销毁原生编辑器", async () => {
    const { wrapper, project, setAuthoritative } = await setup()
    try {
        const previous = wrapper.getComponent(ModelEditPanel).get("textarea").element
        await wrapper.get('nav button[aria-label="连接"]').trigger("click")
        await flushPromises()
        const next: ProjectSnapshot = { ...structuredClone(project), connection: { kind: "desktop", label: "Desktop", instanceId: "new-instance" } }
        setAuthoritative(next)
        wrapper.getComponent(ConnectionPanel).vm.$emit("connected", next)
        await flushPromises()
        expect(wrapper.findComponent(ModelEditPanel).exists()).toBe(false)
        await wrapper.get("#tab-tables").trigger("click")
        await flushPromises()
        expect(wrapper.getComponent(ModelEditPanel).get("textarea").element).not.toBe(previous)
        expect(wrapper.getComponent(ModelEditPanel).get("textarea").element).toHaveProperty("value", "")
    } finally {
        wrapper.unmount()
    }
})

it("新App实例不继承同模型的旧编辑会话", async () => {
    const first = await setup()
    await first.wrapper.get(".copy-change textarea").setValue("Another app must not inherit")
    first.wrapper.unmount()
    const second = await setup()
    try {
        expect(second.wrapper.get(".copy-change textarea").element).toHaveProperty("value", "虚构数据")
        expect(second.wrapper.getComponent(ModelEditPanel).findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("销售")
    } finally {
        second.wrapper.unmount()
    }
})

it("应用模型修改期间导航仍锁定, 取消确认不会把请求状态存入导航会话", async () => {
    const { wrapper, request, project } = await setup()
    let finish!: (snapshot: ProjectSnapshot) => void
    try {
        await wrapper.get(".copy-change textarea").setValue("Pending navigation draft")
        wrapper.get<HTMLTextAreaElement>(".copy-change textarea").element.setSelectionRange(4, 4)
        request.mockImplementationOnce(
            () =>
                ({
                    id: "edit-pending",
                    promise: new Promise<ProjectSnapshot>((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get(".copy-change form").trigger("submit")
        await flushPromises()
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeDefined()
        await wrapper.get('nav button[aria-label="设置"]').trigger("click")
        expect(wrapper.findComponent(ModelPanel).exists()).toBe(true)
        await new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!).trigger("click")
        await flushPromises()
        expect(request.mock.calls.some(([method, params]) => method === "request.cancel" && "requestId" in params && params.requestId === "edit-pending")).toBe(
            true,
        )
        expect(wrapper.get('nav button[aria-label="设置"]').attributes("disabled")).toBeDefined()
        finish(project)
        await flushPromises()
        await navigateLanguage(wrapper)
        expect(wrapper.get(".copy-change textarea").element).toHaveProperty("value", "Pending navigation draft")
        const input = wrapper.get<HTMLTextAreaElement>(".copy-change textarea").element
        expect([input.selectionStart, input.selectionEnd]).toEqual([4, 4])
        expect(wrapper.get('.copy-change button[type="submit"]').attributes("disabled")).toBeUndefined()
        expect(wrapper.get(".content").attributes("aria-busy")).toBe("false")
    } finally {
        finish?.(project)
        await flushPromises()
        wrapper.unmount()
    }
})
