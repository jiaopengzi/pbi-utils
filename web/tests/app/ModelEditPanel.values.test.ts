/**
 * FilePath    : pbi-utils\web\tests\app\ModelEditPanel.values.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型编辑目标值同步、草稿保持、失效目标守卫及失败回读, 不修改真实模型.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { EditorView } from "@codemirror/view"
import { ElButton, ElSelect } from "element-plus"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import { expectConsoleWarnings } from "../support/testDiagnostics"

/** 使用完整快照和真实表单, 仅隔离外部宿主请求; cleanup 释放组件和桥接. */
function setup(options: { operation?: string; report?: boolean } = {}) {
    const project = structuredClone(previewProject)
    project.model.tables.push({ ...structuredClone(project.model.tables[0]!), name: "空说明表", description: null })
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ModelEditPanel, { props: { project, bridge, initialOperation: options.operation, report: options.report } })
    return {
        project,
        wrapper,
        request,
        /** 按实际无障碍标签选择目标, 等待组件派生值完成同步. */
        select: async (label: string, value: string) => {
            wrapper
                .findAllComponents(ElSelect)
                .find((select) => select.props("ariaLabel") === label)!
                .vm.$emit("update:modelValue", value)
            await flushPromises()
        },
        cleanup: () => {
            wrapper.unmount()
            bridge.dispose()
            vi.restoreAllMocks()
        },
    }
}

it("表说明切换带入对应当前值, 无说明时为空, 表说明采用2000上限并使用独立应用文案", async () => {
    const { wrapper, select, cleanup } = setup({ operation: "table.set" })
    try {
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(wrapper.text()).not.toContain("修改直接提交到当前连接模型")
        expect(wrapper.findAll(".el-form-item__label").map((label) => label.text())).toContain("属性")
        await select("目标对象", "销售")
        expect(wrapper.get<HTMLTextAreaElement>('textarea[aria-label="值"]').element.value).toBe("虚构数据")
        expect(wrapper.get("textarea").attributes("maxlength")).toBe("2000")
        expect(wrapper.get('button[type="submit"]').text()).toBe("应用")
        await wrapper.get("textarea").setValue("尚未应用的编辑")
        await select("目标对象", "空说明表")
        expect(wrapper.get("textarea").element.value).toBe("")
        await select("目标对象", "销售")
        expect(wrapper.get("textarea").element.value).toBe("虚构数据")
    } finally {
        cleanup()
    }
})

it("度量值属性动态带入说明、格式、文件夹和完整长DAX, 切对象类型后清空旧值", async () => {
    const { project, wrapper, select, cleanup } = setup()
    project.model.measures[0]!.expression = "1 + ".repeat(750) + "1"
    try {
        await select("所属表", "销售")
        await select("目标对象", "销售额")
        expect(wrapper.get("textarea").element.value).toBe("模拟指标，未读取真实文件")
        await select("属性", "formatString")
        expect(wrapper.get("textarea").element.value).toBe("0.00")
        await select("属性", "displayFolder")
        expect(wrapper.get("textarea").element.value).toBe("基础指标")
        await select("属性", "expression")
        const editor = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!
        expect(editor.state.doc.toString()).toBe(project.model.measures[0]!.expression)
        editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: "2 + ".repeat(750) + "2" }, userEvent: "input" })
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(requestValue(wrapper.emitted("updated")?.at(-1)?.[0] as ProjectSnapshot)).toBe("2 + ".repeat(750) + "2")
        await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], () => select("对象类型", "table.set"))
        expect(wrapper.get("textarea").element.value).toBe("")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
    } finally {
        cleanup()
    }
})

/** 从真实模拟桥接提交结果中读取已应用表达式, 不复制产品的草稿推导逻辑. */
function requestValue(project: ProjectSnapshot) {
    return project.model.measures[0]!.expression
}

it("快照无关变化和语言切换保留草稿, 当前字段外部变化时保留草稿并阻止覆盖", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("用户尚未应用")
        const unrelated = structuredClone(project)
        unrelated.baselineId = "another-baseline"
        unrelated.model.measures[0]!.expression = "42"
        unrelated.summary = { compatibilityLevel: 1702, tableCount: 2, measureCount: 1, reportPageCount: 1 }
        await wrapper.setProps({ project: unrelated })
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get<HTMLTextAreaElement>('textarea[aria-label="Value"]').element.value).toBe("用户尚未应用")
        expect(wrapper.get('button[type="submit"]').text()).toBe("Apply")
        const changed = structuredClone(unrelated)
        changed.model.tables[0]!.description = "外部最新说明"
        await wrapper.setProps({ project: changed })
        expect(wrapper.get("textarea").element.value).toBe("用户尚未应用")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("The current property has changed")
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
        await select("Target object", "空说明表")
        await select("Target object", "销售")
        expect(wrapper.get("textarea").element.value).toBe("外部最新说明")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("同目标快照删除选中对象后清空选择和值, 即使脚本提交也不请求修改", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("旧目标编辑")
        const removed = structuredClone(project)
        removed.model.tables.shift()
        await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], async () => {
            await wrapper.setProps({ project: removed })
            await flushPromises()
        })
        expect(wrapper.findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("")
        expect(wrapper.get("textarea").element.value).toBe("")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("模型切换即使对象同名也清空原选择和草稿", async () => {
    const { project, wrapper, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("旧模型编辑")
        await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], async () => {
            await wrapper.setProps({ project: { ...structuredClone(project), projectId: "another-model", projectPath: "另一个.pbip" } })
            await flushPromises()
        })
        expect(wrapper.findAllComponents(ElSelect)[0]!.props("modelValue")).toBe("")
        expect(wrapper.get("textarea").element.value).toBe("")
    } finally {
        cleanup()
    }
})

it("报表页面和视觉对象切换同步位置值, 保留数值0并将null显示为空", async () => {
    const { project, wrapper, select, cleanup } = setup({ report: true })
    project.pages[0]!.visuals[0]!.x = 0
    project.pages[0]!.visuals[0]!.y = null
    project.pages.push({
        ...structuredClone(project.pages[0]!),
        id: "other",
        displayName: "另页",
        visuals: [{ ...project.pages[0]!.visuals[0]!, id: "card2", x: 123 }],
    })
    try {
        await select("页面", "overview")
        await select("目标对象", "card1")
        expect(wrapper.get<HTMLInputElement>('input[aria-label="值"]').element.value).toBe("0")
        await select("属性", "y")
        expect(wrapper.get<HTMLInputElement>('input[aria-label="值"]').element.value).toBe("")
        await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], () => select("页面", "other"))
        expect(wrapper.get<HTMLInputElement>('input[aria-label="值"]').element.value).toBe("")
        await select("目标对象", "card2")
        await select("属性", "x")
        expect(wrapper.get<HTMLInputElement>('input[aria-label="值"]').element.value).toBe("123")
    } finally {
        cleanup()
    }
})

it("UDF名称带入既有表达式, 新名称从空白创建, 切回加载原表达式", async () => {
    const { project, wrapper, cleanup } = setup({ operation: "udf.set" })
    try {
        const input = wrapper.get<HTMLInputElement>('input[aria-label="函数名"]')
        await input.setValue("Demo.Double")
        await flushPromises()
        let editor = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!
        expect(editor.state.doc.toString()).toBe(project.model.functions[0]!.expression)
        await input.setValue("New.Function")
        await flushPromises()
        editor = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!
        expect(editor.state.doc.toString()).toBe("")
        await input.setValue("Demo.Double")
        await flushPromises()
        expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!.state.doc.toString()).toBe(project.model.functions[0]!.expression)
    } finally {
        cleanup()
    }
})

it("保存失败后的同目标权威回读保留用户输入, 部分保存仍保留输入和锁定事实", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("需要保留的失败输入")
        const fresh = structuredClone(project)
        fresh.model.tables[0]!.description = "权威模型旧值已变化"
        fresh.hasPendingChanges = true
        request.mockImplementationOnce(() => ({ id: "failed-edit", promise: Promise.reject(new Error("受控提交失败")) }) as ReturnType<BridgeClient["request"]>)
        request.mockImplementationOnce(() => ({ id: "read-back", promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(wrapper.emitted("updated")?.at(-1)?.[0]).toEqual(fresh)
        await wrapper.setProps({ project: fresh })
        expect(wrapper.get("textarea").element.value).toBe("需要保留的失败输入")
        expect(wrapper.get("textarea").attributes("disabled")).toBeDefined()
        expect(document.querySelector(".el-message--success")).toBeNull()
    } finally {
        cleanup()
    }
})

it("未改动字段跟随同目标权威变化, 自己成功提交的相等回显不产生冲突", async () => {
    const { project, wrapper, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        const external = structuredClone(project)
        external.model.tables[0]!.description = "权威更新"
        await wrapper.setProps({ project: external })
        expect(wrapper.get("textarea").element.value).toBe("权威更新")
        await wrapper.get("textarea").setValue("本轮提交")
        const echo = structuredClone(external)
        echo.model.tables[0]!.description = "本轮提交"
        await wrapper.setProps({ project: echo })
        expect(wrapper.get("textarea").element.value).toBe("本轮提交")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeUndefined()
        expect(document.querySelector(".el-message--warning")).toBeNull()
    } finally {
        cleanup()
    }
})

it("表说明超过应用上限时保留完整输入并阻止请求, 缩回2000后允许应用", async () => {
    const { wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("说".repeat(2001))
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(wrapper.get("textarea").element.value).toHaveLength(2001)
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(request).not.toHaveBeenCalled()
        await wrapper.get("textarea").setValue("说".repeat(2000))
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request.mock.calls[0]?.slice(0, 2)).toEqual([
            "model.edit",
            { operation: "table.set", objectName: "销售", property: "description", value: "说".repeat(2000), table: "", pageId: "" },
        ])
    } finally {
        cleanup()
    }
})

it("UDF从候选快照删除后清空名称与表达式, 不将原更新静默改成创建", async () => {
    const { project, wrapper, request, cleanup } = setup({ operation: "udf.set" })
    try {
        await wrapper.get<HTMLInputElement>('input[aria-label="函数名"]').setValue("Demo.Double")
        await flushPromises()
        const removed = structuredClone(project)
        removed.model.functions = []
        await expectConsoleWarnings([["async-validator:", ["objectName is required"]]], async () => {
            await wrapper.setProps({ project: removed })
            await flushPromises()
        })
        expect(wrapper.get<HTMLInputElement>('input[aria-label="函数名"]').element.value).toBe("")
        expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!.state.doc.toString()).toBe("")
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("保存失败回读也保留未改动输入, 权威字段已变化时阻止继续覆盖", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        const fresh = structuredClone(project)
        fresh.model.tables[0]!.description = "另一操作已修改"
        request.mockImplementationOnce(() => ({ id: "failed-edit", promise: Promise.reject(new Error("受控提交失败")) }) as ReturnType<BridgeClient["request"]>)
        request.mockImplementationOnce(() => ({ id: "read-back", promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        await wrapper.setProps({ project: fresh })
        expect(wrapper.get("textarea").element.value).toBe("虚构数据")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
    } finally {
        cleanup()
    }
})

it.each([false, true])("父级表或页面从候选移除后清空整组选中字段, 报表模式=%s", async (report) => {
    const { project, wrapper, request, select, cleanup } = setup({ report })
    try {
        await select(report ? "页面" : "所属表", report ? "overview" : "销售")
        await select("目标对象", report ? "card1" : "销售额")
        const removed = structuredClone(project)
        if (report) removed.pages = []
        else removed.model.tables = []
        await expectConsoleWarnings(
            [
                ["async-validator:", [`${report ? "pageId" : "table"} is required`]],
                ["async-validator:", ["objectName is required"]],
            ],
            async () => {
                await wrapper.setProps({ project: removed })
                await flushPromises()
            },
        )
        expect(
            wrapper
                .findAllComponents(ElSelect)
                .find((item) => item.props("ariaLabel") === "目标对象")!
                .props("modelValue"),
        ).toBe("")
        expect(wrapper.get<HTMLInputElement | HTMLTextAreaElement>('[aria-label="值"]').element.value).toBe("")
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("单表模型冲突可直接载入当前值再编辑应用, 无须切换目标或重挂组件", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        const single = structuredClone(project)
        single.model.tables = [single.model.tables[0]!]
        await wrapper.setProps({ project: single })
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("保留的单表草稿")
        const changed = structuredClone(single)
        changed.model.tables[0]!.description = "权威最新说明"
        await wrapper.setProps({ project: changed })
        await select("目标对象", "销售")
        await select("属性", "description")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        expect(wrapper.get("textarea").element.value).toBe("保留的单表草稿")
        expect(wrapper.findAll("button").some((button) => button.text() === "载入当前值")).toBe(true)
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get("textarea").element.value).toBe("保留的单表草稿")
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "Load current value")!
            .trigger("click")
        expect(wrapper.get("textarea").element.value).toBe("权威最新说明")
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeUndefined()
        expect(request).not.toHaveBeenCalled()
        await wrapper.get("textarea").setValue("合并后应用的说明")
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request.mock.calls[0]?.slice(0, 2)).toEqual([
            "model.edit",
            { operation: "table.set", objectName: "销售", property: "description", value: "合并后应用的说明", table: "", pageId: "" },
        ])
    } finally {
        cleanup()
    }
})

it("载入当前值在旁观锁和待处理保存期间真实禁用, 直接触发事件也不丢草稿", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("锁定期间保留")
        const changed = structuredClone(project)
        changed.model.tables[0]!.description = "权威最新说明"
        await wrapper.setProps({ project: changed, locked: true })
        let reload = wrapper.findAllComponents(ElButton).find((button) => button.text() === "载入当前值")!
        expect(reload.get("button").attributes("disabled")).toBeDefined()
        reload.vm.$emit("click", new MouseEvent("click"))
        await flushPromises()
        expect(wrapper.get("textarea").element.value).toBe("锁定期间保留")
        await wrapper.setProps({ project: { ...changed, hasPendingChanges: true }, locked: false })
        reload = wrapper.findAllComponents(ElButton).find((button) => button.text() === "载入当前值")!
        expect(reload.get("button").attributes("disabled")).toBeDefined()
        reload.vm.$emit("click", new MouseEvent("click"))
        await flushPromises()
        expect(wrapper.get("textarea").element.value).toBe("锁定期间保留")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("模型应用在途时恢复入口保持禁用, 绕过按钮的事件也不覆盖输入", async () => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    let finish: ((snapshot: ProjectSnapshot) => void) | undefined
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("应用在途的输入")
        request.mockImplementationOnce(
            () =>
                ({
                    id: "pending-edit",
                    promise: new Promise<ProjectSnapshot>((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        const changed = structuredClone(project)
        changed.model.tables[0]!.description = "应用期间的外部值"
        await wrapper.setProps({ project: changed })
        const reload = wrapper.findAllComponents(ElButton).find((button) => button.text() === "载入当前值")!
        expect(reload.get("button").attributes("disabled")).toBeDefined()
        reload.vm.$emit("click", new MouseEvent("click"))
        await flushPromises()
        expect(wrapper.get("textarea").element.value).toBe("应用在途的输入")
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(1)
        finish!(changed)
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        finish?.(project)
        await flushPromises()
        cleanup()
    }
})

it.each(["locked", "pending"] as const)("有效目标在%s状态下Apply保持原生disabled, 不以显式false覆盖表单锁", async (state) => {
    const { project, wrapper, request, select, cleanup } = setup({ operation: "table.set" })
    try {
        await select("目标对象", "销售")
        await wrapper.get("textarea").setValue("锁定前草稿")
        await wrapper.setProps(state === "locked" ? { locked: true } : { project: { ...project, hasPendingChanges: true } })
        expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined()
        await wrapper.get("form").trigger("submit")
        await flushPromises()
        expect(request).not.toHaveBeenCalled()
        expect(wrapper.get("textarea").element.value).toBe("锁定前草稿")
    } finally {
        cleanup()
    }
})
