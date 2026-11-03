/**
 * FilePath    : pbi-utils\web\tests\app\MeasuresPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值选择回填、批量属性、格式化与保存失败交互测试.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { EditorView } from "@codemirror/view"
import { undo } from "@codemirror/commands"
import { markRaw } from "vue"
import { ElSwitch } from "element-plus"
import { BridgeClient, type MeasureChange } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import MeasuresPanel from "@/app/MeasuresPanel.vue"
import MeasureFields from "@/app/MeasureFields.vue"
import { setLocale, setUiSettings } from "@/i18n"

afterEach(() => vi.restoreAllMocks())

it.each([
    ["edit", "long", "\nDIVIDE ( 1, 2 )", "DIVIDE ( 1, 2 )"],
    ["edit", "short", "\nDIVIDE (\n    1,\n    2\n)", "DIVIDE (\n    1,\n    2\n)"],
    ["create", "long", "\nDIVIDE ( 1, 2 )", "DIVIDE ( 1, 2 )"],
    ["create", "short", "\nDIVIDE (\n    1,\n    2\n)", "DIVIDE (\n    1,\n    2\n)"],
] as const)("%s 的 %s 格式化响应回填后保存正文顶格且只保留一个定义头换行", async (mode, style, stored, editor) => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: style })
        if (mode === "create") {
            await button("新建度量值").trigger("click")
            await wrapper.get('input[aria-label="度量值名称"]').setValue("1500_预测_表达式")
        }
        await editDax(wrapper, "DIVIDE(1,2)")
        request.mockImplementationOnce(
            () => ({ id: "format-flush-left", promise: Promise.resolve({ expressions: [stored] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(request.mock.calls).toEqual([["dax.format", { value: '["DIVIDE(1,2)"]', style }]])
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe(editor)
        expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!.state.doc.toString()).toBe(editor)
        await button(mode === "create" ? "创建并保存" : "保存").trigger("click")
        await flushPromises()
        const saves = request.mock.calls.filter(([method]) => method === "model.edit")
        expect(saves).toHaveLength(1)
        const items = JSON.parse((saves[0]![1] as { value: string }).value).items as MeasureChange[]
        expect(items).toHaveLength(1)
        expect(items[0]).toMatchObject({ action: mode === "create" ? "create" : "update", expression: stored })
        const updated = wrapper.emitted("updated")!.at(-1)![0] as typeof previewProject
        expect(updated.model.measures.find((measure) => measure.name === items[0]!.name)!.expression).toBe(stored)
    } finally {
        cleanup()
    }
})

it("外部工作区锁撤销待确认选择并保留草稿, 迟到格式化结果不回填", async () => {
    const { wrapper, request, button, cleanup } = await setup()
    try {
        await wrapper.get('textarea[aria-label="说明"]').setValue("外部切换时保留")
        await button("清空").trigger("click")
        await flushPromises()
        expect(document.querySelector('[role="dialog"]')?.textContent).toContain("未保存的修改")
        await wrapper.setProps({ locked: true })
        await flushPromises()
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "外部切换时保留")
        expect(wrapper.text()).toContain("已选 1 个")
        await wrapper.setProps({ locked: false })
        let finish!: (value: { expressions: string[] }) => void
        request.mockImplementationOnce(
            () =>
                ({
                    id: "format-pending",
                    promise: new Promise((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        const before = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!.state.doc.toString()
        await button("格式化").trigger("click")
        await wrapper.setProps({ locked: true })
        finish({ expressions: ["999999"] })
        await flushPromises()
        expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)!.state.doc.toString()).toBe(before)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "外部切换时保留")
    } finally {
        cleanup()
    }
})

it("未保存时切换行先确认, 取消保留输入和编辑器, 放弃才切换", async () => {
    const { wrapper, request, cleanup } = await setup()
    try {
        await wrapper.get('textarea[aria-label="说明"]').setValue("保留草稿")
        const editor = wrapper.get(".cm-content").element
        const profit = () =>
            wrapper
                .findAll(".measure-row")
                .find((row) => row.get("strong").text() === "利润")!
                .get("button")
        await profit().trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("保留草稿")
        expect(document.querySelector('[role="dialog"]')?.textContent).toContain("未保存的修改")
        await dialogButton("取消").trigger("click")
        await flushPromises()
        expect(wrapper.get(".cm-content").element).toBe(editor)
        await profit().trigger("click")
        await flushPromises()
        await dialogButton("放弃修改").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("profit")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("未保存清空时保存失败保留草稿和原选择, 成功后才清空", async () => {
    const { wrapper, request, button, cleanup } = await setup()
    try {
        await wrapper.get('textarea[aria-label="说明"]').setValue("待保存")
        await button("清空").trigger("click")
        await flushPromises()
        request.mockImplementationOnce(() => ({ id: "failed", promise: Promise.reject(new Error("保存失败测试")) }) as ReturnType<BridgeClient["request"]>)
        await dialogButton("保存").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("待保存")
        expect(wrapper.text()).toContain("已选 1 个")
        await dialogButton("保存").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).exists()).toBe(false)
        expect(wrapper.text()).toContain("已选 0 个")
    } finally {
        cleanup()
    }
})

it.each(["row", "checkbox", "clear", "all", "new", "back"] as const)("新增草稿经 %s 导航均先确认, 取消后保留全部输入", async (navigation) => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        await button("新建度量值").trigger("click")
        await wrapper.get('input[aria-label="度量值名称"]').setValue("新增待保存")
        await editDax(wrapper, "123")
        if (navigation === "row") await wrapper.get(".measure-row button").trigger("click")
        if (navigation === "checkbox") await wrapper.findAll(".measure-row input[type=checkbox]")[1]!.setValue(true)
        if (navigation === "clear") await button("清空").trigger("click")
        if (navigation === "all") await button("全选").trigger("click")
        if (navigation === "new") await button("新建度量值").trigger("click")
        if (navigation === "back") await button("返回编辑").trigger("click")
        await flushPromises()
        const dialog = new DOMWrapper(document.body).get('[role="dialog"]')
        expect(dialog.text()).toContain("未保存的修改")
        await dialog
            .findAll("button")
            .find((node) => node.text() === "取消")!
            .trigger("click")
        await flushPromises()
        expect(wrapper.get('input[aria-label="度量值名称"]').element).toHaveProperty("value", "新增待保存")
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("123")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("批量格式预览保留勾选属性, 确认保存一次提交全部修改", async () => {
    const { wrapper, button, request, selectAll, cleanup } = await setup()
    try {
        await selectAll()
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:enabled", ["description"])
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "批量说明" })
        await flushPromises()
        request.mockImplementationOnce(
            () => ({ id: "format", promise: Promise.resolve({ expressions: ["\n2 + 2", "\n3 + 3"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("批量格式化 DAX").trigger("click")
        await flushPromises()
        expect(wrapper.find(".format-preview").exists()).toBe(true)
        await button("清空").trigger("click")
        await flushPromises()
        const dialog = new DOMWrapper(document.body).get('[role="dialog"]')
        await dialog
            .findAll("button")
            .find((node) => node.text() === "保存")!
            .trigger("click")
        await flushPromises()
        const writes = request.mock.calls.filter(([method]) => method === "model.edit")
        expect(writes).toHaveLength(1)
        const items = JSON.parse((writes[0]![1] as { value: string }).value).items as MeasureChange[]
        expect(items.map((item) => item.description)).toEqual(["批量说明", "批量说明"])
        expect(items.map((item) => item.expression)).toEqual(["\n2 + 2", "\n3 + 3"])
        expect(wrapper.findComponent(MeasureFields).exists()).toBe(false)
    } finally {
        cleanup()
    }
})

it("仅批量格式化的未保存预览在外部更新后保留, 冲突保存拒绝写入并继续确认", async () => {
    const { wrapper, project, button, request, selectAll, cleanup } = await setup()
    try {
        await selectAll()
        request.mockImplementationOnce(
            () => ({ id: "format", promise: Promise.resolve({ expressions: ["\n2 + 2", "\n3 + 3"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("批量格式化 DAX").trigger("click")
        await flushPromises()
        const fresh = structuredClone(project)
        fresh.baselineId = "external-conflict"
        fresh.model.measures[0]!.expression = "42"
        await wrapper.setProps({ project: fresh })
        expect(wrapper.find(".format-preview").exists()).toBe(true)
        await button("清空").trigger("click")
        await flushPromises()
        request.mockImplementation((method) => ({ id: method, promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        const dialog = new DOMWrapper(document.body).get('[role="dialog"]')
        await dialog
            .findAll("button")
            .find((node) => node.text() === "保存")!
            .trigger("click")
        await flushPromises()
        expect(wrapper.find(".format-preview").exists()).toBe(true)
        expect(dialog.text()).toContain("未保存的修改")
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("格式化输出字符数有效但真实保存 UTF-8 信封超限时保留原草稿", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        await editDax(wrapper, "1+1")
        await wrapper.get('textarea[aria-label="说明"]').setValue("说".repeat(2000))
        const expression = "//" + "中".repeat(20000) + "\n1 + 1"
        request.mockImplementationOnce(
            () => ({ id: "format", promise: Promise.resolve({ expressions: ["\n" + expression] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("1+1")
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "说".repeat(2000))
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(0)
        expect(document.querySelector(".el-message--error")?.textContent).toContain("批次内容过大时请分批处理")
    } finally {
        cleanup()
    }
})

it("格式偏好来自设置, 切换样式和语言保留编辑器、选区与撤销历史且不触发请求", async () => {
    const { wrapper, request, cleanup } = await setup()
    try {
        expect(wrapper.find(".dax-format-style").exists()).toBe(false)
        const node = wrapper.get(".cm-content").element as HTMLElement
        const view = EditorView.findFromDOM(node)!
        const original = view.state.doc.toString()
        await editDax(wrapper, "DIVIDE(1,2)")
        view.dispatch({ selection: { anchor: 2, head: 7 } })
        setUiSettings({ locale: "en-us", showRecentFiles: true, daxFormatStyle: "short" })
        await flushPromises()
        expect(wrapper.get(".cm-content").element).toBe(node)
        expect(view.state.doc.toString()).toBe("DIVIDE(1,2)")
        expect(view.state.selection.main).toMatchObject({ anchor: 2, head: 7 })
        expect(undo(view)).toBe(true)
        expect(view.state.doc.toString()).toBe(original)
        expect(request).not.toHaveBeenCalled()
    } finally {
        setLocale("zh-cn")
        cleanup()
    }
})

it("单行工具栏使用文字内嵌的展开折叠开关, 移除适宽按钮且切换保留草稿", async () => {
    const { wrapper, request, cleanup } = await setup()
    try {
        expect(wrapper.findAll("button").some((node) => node.text() === "适应内容宽度")).toBe(false)
        const toggle = wrapper.findComponent(ElSwitch)
        expect(toggle.exists()).toBe(true)
        expect(toggle.props()).toMatchObject({ inlinePrompt: true, activeText: "展开", inactiveText: "折叠", modelValue: true })
        await wrapper.get('textarea[aria-label="说明"]').setValue("开关切换保留")
        await toggle.get('[role="switch"]').trigger("click")
        expect(wrapper.findAll(".measure-row")).toHaveLength(0)
        expect(toggle.props("modelValue")).toBe(false)
        await toggle.get('[role="switch"]').trigger("click")
        expect(wrapper.findAll(".measure-row")).toHaveLength(2)
        await wrapper
            .findAll(".measure-folder")
            .find((node) => node.text() === "test")!
            .trigger("click")
        expect(toggle.props("modelValue")).toBe(false)
        await toggle.get('[role="switch"]').trigger("click")
        expect(wrapper.findAll(".measure-row")).toHaveLength(2)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "开关切换保留")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("目录整组选择保留搜索隐藏选择, 祖先半选状态准确且折叠后可选择全部后代", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        const directory = (path: string) =>
            wrapper.findAll(".measure-folder-selection").find((node) => node.attributes("aria-label") === `选择目录 ${path} 下的度量值`)!
        const table = () => directory("销售")
        expect(table().find(".is-indeterminate").exists()).toBe(true)
        const search = wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]')
        await search.setValue("test")
        await directory("销售\\test").get("input").setValue(true)
        expect(wrapper.text()).toContain("已选 2 个")
        await search.setValue("")
        expect(table().get<HTMLInputElement>("input").element.checked).toBe(true)
        await directory("销售\\test").get("input").setValue(false)
        expect(wrapper.text()).toContain("已选 1 个")
        expect(table().find(".is-indeterminate").exists()).toBe(true)
        await button("清空").trigger("click")
        await wrapper.findComponent(ElSwitch).get('[role="switch"]').trigger("click")
        expect(wrapper.findAll(".measure-row")).toHaveLength(0)
        await table().get("input").setValue(true)
        expect(wrapper.text()).toContain("已选 2 个")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})

it("搜索展开后的目录仍可折叠, 清除查询后恢复原目录状态与选择", async () => {
    const { wrapper, cleanup } = await setup()
    try {
        const search = wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]')
        await search.setValue("test")
        const folder = wrapper.findAll(".measure-folder").find((node) => node.text() === "test")!
        await folder.trigger("click")
        expect(folder.attributes("aria-expanded")).toBe("false")
        expect(wrapper.findAll(".measure-row")).toHaveLength(0)
        await search.setValue("")
        expect(wrapper.findAll(".measure-row")).toHaveLength(2)
        expect(wrapper.text()).toContain("已选 1 个")
    } finally {
        cleanup()
    }
})

it("按表与多级文件夹展示, 搜索文件夹路径保留祖先且不改变选择或编辑草稿", async () => {
    const { project, wrapper, button, cleanup } = await setup()
    try {
        const hierarchy = structuredClone(project)
        hierarchy.baselineId = "hierarchy"
        hierarchy.model.measures[0]!.displayFolder = ""
        hierarchy.model.measures[1]!.displayFolder = "财务\\收入;经营\\收入"
        await wrapper.setProps({ project: hierarchy })
        expect(wrapper.findAll(".measure-folder").map((folder) => folder.text())).toEqual(["销售", "财务", "收入", "经营", "收入"])
        expect(wrapper.findAll(".measure-row")).toHaveLength(3)
        await wrapper.get('textarea[aria-label="说明"]').setValue("保留草稿")
        await wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]').setValue("财务 收入")
        expect(wrapper.findAll(".measure-folder").map((folder) => folder.text())).toEqual(["销售", "财务", "收入"])
        expect(wrapper.findAll(".measure-row")).toHaveLength(1)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "保留草稿")
        await button("全选").trigger("click")
        await discardPendingDraft()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("profit")
    } finally {
        cleanup()
    }
})

it("折叠和临时搜索展开不改变选择, 清除搜索恢复折叠, 同名跨表保持准确操作身份", async () => {
    const { project, wrapper, button, request, cleanup } = await setup()
    try {
        const hierarchy = structuredClone(project)
        hierarchy.baselineId = "same-names"
        hierarchy.model.measures.push({ ...hierarchy.model.measures[0]!, table: "其他表", displayFolder: "test" })
        await wrapper.setProps({ project: hierarchy })
        await wrapper.findComponent(ElSwitch).get('[role="switch"]').trigger("click")
        expect(wrapper.findAll(".measure-row")).toHaveLength(0)
        expect(wrapper.text()).toContain("已选 1 个")
        await wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]').setValue("其他表")
        expect(wrapper.findAll(".measure-row")).toHaveLength(1)
        await wrapper.get(".measure-row button").trigger("click")
        await wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]').setValue("")
        expect(wrapper.findAll(".measure-row")).toHaveLength(0)
        await wrapper.findComponent(ElSwitch).get('[role="switch"]').trigger("click")
        request.mockImplementationOnce(() => ({ id: "delete", promise: Promise.resolve(hierarchy) }) as ReturnType<BridgeClient["request"]>)
        await button("删除（1）").trigger("click")
        await flushPromises()
        const call = request.mock.calls.find(([method]) => method === "model.edit")!
        expect(JSON.parse((call[1] as { value: string }).value).items).toEqual([{ action: "delete", table: "其他表", name: "销售额" }])
    } finally {
        cleanup()
    }
})

it("修改显示文件夹后的权威结果重建层级并保留当前准确选择", async () => {
    const { wrapper, button, cleanup } = await setup()
    try {
        await wrapper.get('input[aria-label="显示文件夹"]').setValue("新分类\\子目录")
        await button("保存").trigger("click")
        await flushPromises()
        await wrapper.setProps({ project: wrapper.emitted("updated")!.at(-1)![0] as typeof previewProject })
        expect(wrapper.findAll(".measure-folder").map((folder) => folder.text())).toContain("新分类")
        expect(wrapper.findAll(".measure-folder").map((folder) => folder.text())).toContain("子目录")
        expect(wrapper.findComponent(MeasureFields).props("modelValue").displayFolder).toBe("新分类\\子目录")
        expect(wrapper.text()).toContain("已选 1 个")
    } finally {
        cleanup()
    }
})

it("Desktop 外部变化后先读取最新基线, 保留编辑意图并一次提交", async () => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        const original = { ...project, projectPath: "model://desktop", connection: { kind: "desktop" as const, label: "Desktop", instanceId: "instance" } }
        await wrapper.setProps({ project: original })
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "用户输入" })
        await flushPromises()
        const fresh = { ...original, baselineId: "external-refresh" }
        request.mockImplementation(
            (method) =>
                ({ id: method, promise: Promise.resolve(method === "project.inspect" ? fresh : { ...fresh, saveResult: null }) }) as ReturnType<
                    BridgeClient["request"]
                >,
        )
        await button("保存").trigger("click")
        await flushPromises()
        expect(request.mock.calls.map(([method]) => method)).toEqual(["project.inspect", "model.edit"])
        const payload = JSON.parse((request.mock.calls[1]![1] as { value: string }).value)
        expect(payload).toEqual({ baselineId: "external-refresh", items: [{ action: "update", table: "销售", name: "销售额", description: "用户输入" }] })
    } finally {
        cleanup()
    }
})

it.each(["changed", "removed"])("所选度量值在外部变化 %s 时更新读取结果并保留输入, 拒绝写入", async (change) => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        const original = { ...project, projectPath: "model://desktop", connection: { kind: "desktop" as const, label: "Desktop", instanceId: "instance" } }
        await wrapper.setProps({ project: original })
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "保留输入" })
        await flushPromises()
        const fresh = structuredClone(original)
        fresh.baselineId = "external-change"
        fresh.model.measures[0]!.expression = "42"
        if (change === "removed") fresh.model.measures.shift()
        request.mockImplementation((method) => ({ id: method, promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        await button("保存").trigger("click")
        await flushPromises()
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
        expect(wrapper.emitted("updated")?.at(-1)?.[0]).toEqual(fresh)
        await wrapper.setProps({ project: fresh })
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("保留输入")
        if (change === "removed") expect(button("保存").attributes("disabled")).toBeDefined()
    } finally {
        cleanup()
    }
})

it("同对象冲突重复保存不覆盖外部定义, 复制草稿并重新选择同一行后可手工合并提交一次", async () => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        const original = { ...project, projectPath: "model://desktop", connection: { kind: "desktop" as const, label: "Desktop", instanceId: "instance" } }
        await wrapper.setProps({ project: original })
        await wrapper.get('textarea[aria-label="说明"]').setValue("需要保留的用户草稿")
        const fresh = structuredClone(original)
        fresh.baselineId = "conflict-recovery-baseline"
        fresh.model.measures[0]!.expression = "42"
        await wrapper.setProps({ project: fresh })
        request.mockImplementation((method) => ({ id: method, promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        await button("保存").trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(0)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "需要保留的用户草稿")
        await button("保存").trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(0)
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", "需要保留的用户草稿")
        expect(document.querySelector(".el-message--error")?.textContent).toContain("先复制需要保留的草稿")
        const copiedDraft = wrapper.findComponent(MeasureFields).props("modelValue").description
        await wrapper
            .findAll(".measure-row")
            .find((row) => row.get("strong").text() === "销售额")!
            .get("button")
            .trigger("click")
        await flushPromises()
        await discardPendingDraft()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("42")
        expect(wrapper.get('textarea[aria-label="说明"]').element).toHaveProperty("value", fresh.model.measures[0]!.description)
        await wrapper.get('textarea[aria-label="说明"]').setValue(`${copiedDraft}; 已合并外部定义`)
        await button("保存").trigger("click")
        await flushPromises()
        const writes = request.mock.calls.filter(([method]) => method === "model.edit")
        expect(writes).toHaveLength(1)
        expect(JSON.parse((writes[0]![1] as { value: string }).value)).toEqual({
            baselineId: "conflict-recovery-baseline",
            items: [{ action: "update", table: "销售", name: "销售额", description: "需要保留的用户草稿; 已合并外部定义" }],
        })
    } finally {
        cleanup()
    }
})

it.each(["selected", "unrelated"] as const)("工作区自动同步 %s 度量后仍按用户开始编辑的定义检查冲突", async (change) => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        const original = { ...project, projectPath: "model://desktop", connection: { kind: "desktop" as const, label: "Desktop", instanceId: "instance" } }
        await wrapper.setProps({ project: original })
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "保留自动同步前输入" })
        await flushPromises()
        const fresh = structuredClone(original)
        fresh.baselineId = "automatically-synchronized-baseline"
        fresh.model.measures[change === "selected" ? 0 : 1]!.expression = "42"
        await wrapper.setProps({ project: fresh })
        request.mockImplementation((method) => ({ id: method, promise: Promise.resolve(fresh) }) as ReturnType<BridgeClient["request"]>)
        await button("保存").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("保留自动同步前输入")
        const writes = request.mock.calls.filter(([method]) => method === "model.edit")
        if (change === "selected") {
            expect(writes).toHaveLength(0)
            expect(document.querySelector(".el-message--error")?.textContent).toContain("度量值已在外部变化")
        } else {
            expect(writes).toHaveLength(1)
            expect(JSON.parse((writes[0]![1] as { value: string }).value)).toEqual({
                baselineId: "automatically-synchronized-baseline",
                items: [{ action: "update", table: "销售", name: "销售额", description: "保留自动同步前输入" }],
            })
        }
    } finally {
        cleanup()
    }
})

it("Desktop 预检查失败不会写入或清空新增草稿", async () => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        await wrapper.setProps({ project: { ...project, projectPath: "model://desktop", connection: { kind: "desktop", label: "Desktop" } } })
        await button("新建度量值").trigger("click")
        await wrapper.get('input[aria-label="度量值名称"]').setValue("保留新建")
        await editDax(wrapper, "123")
        request.mockImplementation(() => ({ id: "inspect", promise: Promise.reject(new Error("读取失败")) }) as ReturnType<BridgeClient["request"]>)
        await button("创建并保存").trigger("click")
        await flushPromises()
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
        expect(wrapper.get('input[aria-label="度量值名称"]').element).toHaveProperty("value", "保留新建")
    } finally {
        cleanup()
    }
})

it.each(["target", "pending", "late"])("Desktop 预检查 %s 不会继续发送旧编辑", async (state) => {
    const { project, wrapper, request, button, cleanup } = await setup()
    try {
        const original = { ...project, projectPath: "model://desktop", connection: { kind: "desktop" as const, label: "Desktop", instanceId: "instance" } }
        await wrapper.setProps({ project: original })
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "保留输入" })
        await flushPromises()
        let resolve!: (value: typeof original) => void
        request.mockImplementation(
            () =>
                ({
                    id: "inspect",
                    promise: new Promise<typeof original>((done) => {
                        resolve = done
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await button("保存").trigger("click")
        await flushPromises()
        if (state === "late") await wrapper.setProps({ project: { ...original, projectId: "other" } })
        resolve(state === "target" ? { ...original, projectId: "other" } : state === "pending" ? { ...original, hasPendingChanges: true } : original)
        await flushPromises()
        expect(request.mock.calls.map(([method]) => method)).toEqual(["project.inspect"])
    } finally {
        cleanup()
    }
})

it("新建期间外部删除旧选择仍保留新增输入且允许继续创建", async () => {
    const { project, wrapper, button, cleanup } = await setup()
    try {
        await button("新建度量值").trigger("click")
        await wrapper.get('input[aria-label="度量值名称"]').setValue("新增草稿")
        await editDax(wrapper, "123")
        const fresh = structuredClone(project)
        fresh.baselineId = "external-deletion"
        fresh.model.measures.shift()
        await wrapper.setProps({ project: fresh })
        expect(wrapper.get('input[aria-label="度量值名称"]').element).toHaveProperty("value", "新增草稿")
        expect(button("创建并保存").attributes("disabled")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("已编辑的度量值主动删除成功后清除选择, 不作为外部删除冲突保留", async () => {
    const { wrapper, request, button, cleanup } = await setup()
    try {
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "未保存输入" })
        await flushPromises()
        await button("删除（1）").trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(1)
        const saved = wrapper.emitted("updated")!.at(-1)![0] as typeof previewProject
        await wrapper.setProps({ project: saved })
        expect(wrapper.findComponent(MeasureFields).exists()).toBe(false)
        expect(wrapper.text()).not.toContain("已被删除或重命名")
    } finally {
        cleanup()
    }
})

/** 通过真实编辑器交易输入草稿, 继续验证组件到保存请求的完整链路. */
async function editDax(wrapper: VueWrapper, value: string, index = 0) {
    const node = wrapper.findAll('.cm-content[aria-label="DAX 表达式"]')[index]!.element as HTMLElement
    const view = EditorView.findFromDOM(node)
    if (!view) throw new Error("DAX 编辑器未挂载")
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value }, userEvent: "input" })
    await flushPromises()
}
/** 在模拟桥接中建立两个实际可操作的对象, 页面和宿主快照保持一致. */
async function setup() {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const project = await bridge.request("model.edit", {
        operation: "measure.batch",
        objectName: "batch",
        property: "items",
        table: "",
        pageId: "",
        value: JSON.stringify({
            baselineId: previewProject.baselineId,
            items: [
                {
                    action: "create",
                    table: "销售",
                    name: "利润",
                    expression: "1+2",
                    description: "profit",
                    formatString: "0%",
                    displayFolder: "test",
                    isHidden: true,
                },
            ],
        }),
    }).promise
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(MeasuresPanel, { props: { project, bridge } })
    const button = (label: string) => wrapper.findAll("button").find((b) => b.text() === label)!
    const selectAll = async () => {
        await button("全选").trigger("click")
        await flushPromises()
    }
    const cleanup = () => {
        wrapper.unmount()
        bridge.dispose()
    }
    return { bridge, project, wrapper, button, request, selectAll, cleanup }
}

it("单选自动填充实际属性, 多选全部清空, 搜索不改变操作目标", async () => {
    const { wrapper, button, selectAll, cleanup } = await setup()
    try {
        expect(wrapper.findComponent(MeasureFields).props("modelValue")).toMatchObject({ expression: "SUM ( '销售'[金额] )", formatString: "0.00" })
        await selectAll()
        expect(wrapper.findComponent(MeasureFields).props("modelValue")).toEqual({
            expression: "",
            description: "",
            formatString: "",
            displayFolder: "",
            isHidden: false,
        })
        expect(wrapper.findComponent(MeasureFields).props("enabled")).toEqual([])
        await wrapper.get('input[aria-label="搜索名称、文件夹或 DAX"]').setValue("利润")
        expect(wrapper.text()).toContain("已选 2 个")
        await wrapper.findAll(".measure-row button")[0]!.trigger("click")
        expect(wrapper.findComponent(MeasureFields).props("modelValue")).toMatchObject({ expression: "1+2", description: "profit", isHidden: true })
        await button("清空").trigger("click")
        expect(wrapper.findComponent(MeasureFields).exists()).toBe(false)
    } finally {
        cleanup()
    }
})

it("批量更新只包含明确勾选属性, 可清空文件夹而保留 DAX", async () => {
    const { wrapper, button, request, selectAll, cleanup } = await setup()
    try {
        await selectAll()
        wrapper.findComponent(MeasureFields).vm.$emit("update:enabled", ["displayFolder"])
        await flushPromises()
        await button("保存").trigger("click")
        await flushPromises()
        const call = request.mock.calls.find(([method]) => method === "model.edit")!
        const value = JSON.parse((call[1] as { value: string }).value) as { items: MeasureChange[] }
        expect(value.items).toEqual([
            { action: "update", table: "销售", name: "销售额", displayFolder: "" },
            { action: "update", table: "销售", name: "利润", displayFolder: "" },
        ])
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(1)
        expect(wrapper.emitted("updated")?.[0]?.[0]).toMatchObject({ hasPendingChanges: false })
    } finally {
        cleanup()
    }
})

it("单选格式化只改变编辑栏, 失败时原输入保留, 不发保存请求", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        request.mockImplementationOnce(
            () => ({ id: "format", promise: Promise.resolve({ expressions: ["\nSUM ( '销售'[金额] )"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(request.mock.calls[0]).toEqual(["dax.format", { value: JSON.stringify(["SUM ( '销售'[金额] )"]), style: "long" }])
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("SUM ( '销售'[金额] )")
        setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "short" })
        request.mockImplementationOnce(
            () => ({ id: "format-fail", promise: Promise.reject(new Error("不支持的表达式")) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(request.mock.calls[1]).toEqual(["dax.format", { value: JSON.stringify(["SUM ( '销售'[金额] )"]), style: "short" }])
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("SUM ( '销售'[金额] )")
        expect(document.querySelector(".el-message--error")?.textContent).toContain("不支持的表达式")
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it.each(["long", "short"] as const)("多选以 %s 格式保留逐项公式并先预览, 点击保存才发送同一批次", async (style) => {
    const { wrapper, button, request, selectAll, cleanup } = await setup()
    try {
        await selectAll()
        if (style === "short") setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "short" })
        request.mockImplementationOnce(
            () => ({ id: "format", promise: Promise.resolve({ expressions: ["\nSUM ( '销售'[金额] )", "\n1 + 2"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("批量格式化 DAX").trigger("click")
        await flushPromises()
        expect(request.mock.calls[0]).toEqual(["dax.format", { value: JSON.stringify(["\nSUM ( '销售'[金额] )", "1+2"]), style }])
        expect(wrapper.text()).toContain("格式化预览 · 2 项")
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
        await button("保存格式化结果").trigger("click")
        await flushPromises()
        const call = request.mock.calls.find(([method]) => method === "model.edit")!
        expect(JSON.parse((call[1] as { value: string }).value).items.map((m: MeasureChange) => m.expression)).toEqual(["\nSUM ( '销售'[金额] )", "\n1 + 2"])
    } finally {
        cleanup()
    }
})

it.each(["long", "short"] as const)("新建批次使用 %s 格式, 只格式化新增草稿且不发保存请求", async (style) => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        if (style === "short") setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "short" })
        await button("新建度量值").trigger("click")
        await editDax(wrapper, "DIVIDE(1,2)")
        await button("再添加一个").trigger("click")
        await editDax(wrapper, "DIVIDE(3,4)", 1)
        request.mockImplementationOnce(
            () =>
                ({ id: "create-format", promise: Promise.resolve({ expressions: ["\nDIVIDE ( 1, 2 )", "\nDIVIDE ( 3, 4 )"] }) }) as ReturnType<
                    BridgeClient["request"]
                >,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(request.mock.calls).toEqual([["dax.format", { value: JSON.stringify(["DIVIDE(1,2)", "DIVIDE(3,4)"]), style }]])
        expect(wrapper.findAllComponents(MeasureFields).map((editor) => editor.props("modelValue").expression)).toEqual(["DIVIDE ( 1, 2 )", "DIVIDE ( 3, 4 )"])
    } finally {
        cleanup()
    }
})

it.each(["edit", "create"] as const)("%s 格式化输出及数组未超限但完整保存批次超限时保留全部输入", async (mode) => {
    const { wrapper, project, button, request, cleanup } = await setup()
    try {
        if (mode === "create") {
            await button("新建度量值").trigger("click")
            await wrapper.get('input[aria-label="度量值名称"]').setValue("  保存原名字  ")
        }
        await editDax(wrapper, "1+1")
        await wrapper.get('textarea[aria-label="说明"]').setValue("d".repeat(1000))
        const before = { ...wrapper.findComponent(MeasureFields).props("modelValue") }
        const expression = "1" + "\n+ 1".repeat(5800)
        const item: MeasureChange =
            mode === "create"
                ? { action: "create", table: "销售", name: "保存原名字", ...before, expression }
                : { action: "update", table: "销售", name: "销售额", expression, description: before.description }
        expect(expression.length).toBeLessThan(30000)
        expect(JSON.stringify([expression]).length).toBeLessThan(30000)
        expect(JSON.stringify({ baselineId: project.baselineId, items: [item] }).length).toBeGreaterThan(30000)
        request.mockImplementationOnce(
            () => ({ id: "format-over-save-budget", promise: Promise.resolve({ expressions: ["\n" + expression] }) }) as ReturnType<BridgeClient["request"]>,
        )

        await button("格式化").trigger("click")
        await flushPromises()

        expect(JSON.stringify(wrapper.findComponent(MeasureFields).props("modelValue")) === JSON.stringify(before)).toBe(true)
        if (mode === "create") expect(wrapper.get('input[aria-label="度量值名称"]').element).toHaveProperty("value", "  保存原名字  ")
        expect(wrapper.find(".format-preview").exists()).toBe(false)
        expect(document.querySelector(".el-message--error")?.textContent).toContain("批次内容过大时请分批处理")
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("多选再次格式化的完整保存批次超限时保留上一轮预览和批量属性", async () => {
    const { wrapper, project, button, request, selectAll, cleanup } = await setup()
    try {
        await selectAll()
        const editor = wrapper.findComponent(MeasureFields)
        editor.vm.$emit("update:enabled", ["description"])
        editor.vm.$emit("update:modelValue", { ...editor.props("modelValue"), description: "保留批量输入" })
        await flushPromises()
        request.mockImplementationOnce(
            () => ({ id: "format-first", promise: Promise.resolve({ expressions: ["\n1 + 2", "\n3 + 4"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("批量格式化 DAX").trigger("click")
        await flushPromises()
        const previousPreview = wrapper.get(".format-preview").text()
        const expressions = ["1 + 2", "1" + "\n+ 1".repeat(5980)]
        const items = project.model.measures.map((measure, index) => ({
            action: "update",
            table: measure.table,
            name: measure.name,
            expression: expressions[index]!,
        }))
        expect(JSON.stringify(expressions).length).toBeLessThan(30000)
        expect(JSON.stringify({ baselineId: project.baselineId, items }).length).toBeGreaterThan(30000)
        request.mockImplementationOnce(
            () => ({ id: "format-over-batch-budget", promise: Promise.resolve({ expressions }) }) as ReturnType<BridgeClient["request"]>,
        )

        await button("批量格式化 DAX").trigger("click")
        await flushPromises()

        expect(wrapper.get(".format-preview").text() === previousPreview).toBe(true)
        expect(editor.props("modelValue").description).toBe("保留批量输入")
        expect(editor.props("enabled")).toEqual(["description"])
        expect(document.querySelector(".el-message--error")?.textContent).toContain("批次内容过大时请分批处理")
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it.each(["edit", "create"] as const)("%s 格式化预算仅按实际保存属性计算且未超限时可继续保存", async (mode) => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        if (mode === "create") {
            await button("新建度量值").trigger("click")
            await wrapper.get('input[aria-label="度量值名称"]').setValue("  可保存格式  ")
        }
        await editDax(wrapper, "1+1")
        await wrapper.get('textarea[aria-label="说明"]').setValue("保留的说明")
        request.mockImplementationOnce(
            () => ({ id: "format-within-budget", promise: Promise.resolve({ expressions: ["\n1 + 1"] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("1 + 1")
        await button(mode === "create" ? "创建并保存" : "保存").trigger("click")
        await flushPromises()
        const call = request.mock.calls.find(([method]) => method === "model.edit")!
        const items = JSON.parse((call[1] as { value: string }).value).items as MeasureChange[]
        expect(items).toHaveLength(1)
        expect(items[0]).toMatchObject({
            action: mode === "create" ? "create" : "update",
            name: mode === "create" ? "可保存格式" : "销售额",
            expression: "\n1 + 1",
            description: "保留的说明",
        })
        if (mode === "edit") expect(items[0]).not.toHaveProperty("formatString")
        else expect(items[0]).toMatchObject({ formatString: "", displayFolder: "", isHidden: false })
    } finally {
        cleanup()
    }
})

it("单选格式化预算不重复计算未主动修改的长属性, 边界内仍可格式化和保存", async () => {
    const { wrapper, project, bridge, button, request, cleanup } = await setup()
    try {
        const updated = await bridge.request("model.edit", {
            operation: "measure.batch",
            objectName: "batch",
            property: "items",
            table: "",
            pageId: "",
            value: JSON.stringify({
                baselineId: project.baselineId,
                items: [{ action: "update", table: "销售", name: "销售额", description: "d".repeat(1000) }],
            }),
        }).promise
        await wrapper.setProps({ project: updated })
        request.mockClear()
        const expression = "1" + " + 1".repeat(7300)
        const item = { action: "update", table: "销售", name: "销售额", expression }
        expect(JSON.stringify({ baselineId: updated.baselineId, items: [item] }).length).toBeLessThan(30000)
        expect(JSON.stringify({ baselineId: updated.baselineId, items: [{ ...item, description: "d".repeat(1000) }] }).length).toBeGreaterThan(30000)
        request.mockImplementationOnce(
            () =>
                ({ id: "format-unchanged-properties", promise: Promise.resolve({ expressions: ["\n" + expression] }) }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(wrapper.findComponent(MeasureFields).props("modelValue").expression === expression).toBe(true)
        await button("保存").trigger("click")
        await flushPromises()
        const call = request.mock.calls.find(([method]) => method === "model.edit")!
        const items = JSON.parse((call[1] as { value: string }).value).items as MeasureChange[]
        expect(items).toHaveLength(1)
        expect(items[0]!.expression === "\n" + expression).toBe(true)
        expect(items[0]).not.toHaveProperty("description")
    } finally {
        cleanup()
    }
})

it("批量创建和删除通过单个保存请求, 取消新增没有写入", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        await button("新建度量值").trigger("click")
        await wrapper.get('input[aria-label="度量值名称"]').setValue("新增一")
        await editDax(wrapper, "1")
        await button("再添加一个").trigger("click")
        await wrapper.findAll('input[aria-label="度量值名称"]')[1]!.setValue("新增二")
        await editDax(wrapper, "2", 1)
        await button("创建并保存").trigger("click")
        await flushPromises()
        const updated = wrapper.emitted("updated")![0]![0] as typeof previewProject
        expect(updated.model.measures).toHaveLength(4)
        await wrapper.setProps({ project: updated })
        await flushPromises()
        await button("全选").trigger("click")
        await button("删除（4）").trigger("click")
        await flushPromises()
        expect((wrapper.emitted("updated")![1]![0] as typeof previewProject).model.measures).toHaveLength(0)
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(2)
        await button("新建度量值").trigger("click")
        await button("返回编辑").trigger("click")
        expect(request.mock.calls.filter(([method]) => method === "model.edit")).toHaveLength(2)
    } finally {
        cleanup()
    }
})

it("新增名称和属性表单的默认提交被持续阻止, 草稿保留且不触发模型写入", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    try {
        await button("新建度量值").trigger("click")
        const name = wrapper.get('input[aria-label="度量值名称"]')
        await name.setValue("保留新建草稿")
        for (const form of wrapper.findAll("form")) {
            for (let attempt = 0; attempt < 2; attempt++) {
                const event = new Event("submit", { bubbles: true, cancelable: true })
                form.element.dispatchEvent(event)
                expect(event.defaultPrevented).toBe(true)
            }
        }
        await flushPromises()
        expect((name.element as HTMLInputElement).value).toBe("保留新建草稿")
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("保存错误后的相同基线回读不清空用户输入, 有待保存状态时禁止再次提交", async () => {
    const { wrapper, project, button, request, cleanup } = await setup()
    try {
        await wrapper.get('textarea[aria-label="说明"]').setValue("保留输入")
        request.mockImplementationOnce(() => ({ id: "save-fail", promise: Promise.reject(new Error("取消保存")) }) as ReturnType<BridgeClient["request"]>)
        await button("保存").trigger("click")
        await flushPromises()
        await wrapper.setProps({ project: structuredClone(project) })
        expect(wrapper.findComponent(MeasureFields).props("modelValue").description).toBe("保留输入")
        await wrapper.setProps({ project: { ...project, hasPendingChanges: true } })
        expect(button("保存").attributes("disabled")).toBeDefined()
        expect(button("新建度量值").attributes("disabled")).toBeDefined()
    } finally {
        cleanup()
    }
})

it("只读来源没有创建编辑删除或格式化按钮", async () => {
    const { wrapper, project, cleanup } = await setup()
    try {
        await wrapper.setProps({ project: { ...project, projectPath: "model://remote", connection: { kind: "ssas-xmla", label: "SSAS" } } })
        expect(wrapper.findComponent(MeasureFields).exists()).toBe(false)
        expect(wrapper.findAll("button").some((b) => /新建|保存|删除所选|格式化/.test(b.text()))).toBe(false)
        expect(wrapper.find('[role="group"][aria-label="选择度量值"]').exists()).toBe(true)
    } finally {
        cleanup()
    }
})

it("格式化仅在全局消息更新进度, 取消确认后保留编辑节点并等待原请求终态", async () => {
    const { wrapper, button, request, cleanup } = await setup()
    let rejectFormat!: (cause: Error) => void
    try {
        const editor = wrapper.get(".cm-content").element
        expect(wrapper.find(".measure-hint").exists()).toBe(false)
        const childCount = wrapper.element.childElementCount
        const statuses = wrapper.findAll("[role=status]").length
        request.mockImplementationOnce(
            () =>
                ({
                    id: "format-pending",
                    promise: new Promise((_, reject) => {
                        rejectFormat = reject
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        expect(wrapper.findAll("[role=status]")).toHaveLength(statuses)
        expect(wrapper.text()).not.toContain("正在处理")
        expect(wrapper.element.childElementCount).toBe(childCount)
        expect(wrapper.get(".cm-content").element).toBe(editor)
        expect(wrapper.find(".measure-hint").exists()).toBe(false)
        expect(button("格式化").classes()).toContain("is-loading")
        expect(wrapper.find(".dax-format-style").exists()).toBe(false)
        expect(button("全选").attributes("disabled")).toBeDefined()
        const message = document.querySelector(".operation-message .operation-feedback")!
        expect(message.textContent).toContain("格式化")
        const cancel = new DOMWrapper(message.querySelector<HTMLButtonElement>(".operation-feedback__cancel")!)
        request.mockImplementationOnce(() => ({ id: "cancel-format", promise: Promise.resolve({ accepted: true }) }) as ReturnType<BridgeClient["request"]>)
        await cancel.trigger("click")
        await flushPromises()
        expect(request.mock.calls.at(-1)).toEqual(["request.cancel", { requestId: "format-pending" }])
        expect(cancel.attributes("disabled")).toBeDefined()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get(".cm-content").element).toBe(editor)
        rejectFormat(new Error("格式化已取消"))
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.get(".cm-content").element).toBe(editor)
        expect(document.querySelector(".el-message--error")?.textContent).toContain("格式化已取消")
        expect(button("全选").attributes("disabled")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it.each(["target", "unmount"] as const)("格式化响应迟于 %s 时不回填旧结果或发出保存", async (state) => {
    const { wrapper, project, request, button, cleanup } = await setup()
    let complete!: (value: { expressions: string[] }) => void
    try {
        const originalFields = wrapper.findComponent(MeasureFields).props("modelValue")
        const originalExpression = originalFields.expression
        request.mockImplementationOnce(
            () =>
                ({
                    id: "format-late",
                    promise: new Promise<{ expressions: string[] }>((resolve) => {
                        complete = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await button("格式化").trigger("click")
        await flushPromises()
        if (state === "target") {
            const next = structuredClone(project)
            next.projectId = "next-target"
            next.baselineId = "next-baseline"
            next.model.measures[0]!.expression = "99"
            await wrapper.setProps({ project: next })
        } else wrapper.unmount()
        complete({ expressions: ["OLD RESULT"] })
        await flushPromises()
        if (state === "target") expect(wrapper.findComponent(MeasureFields).props("modelValue").expression).toBe("99")
        else expect(originalFields.expression).toBe(originalExpression)
        expect(wrapper.emitted("updated")).toBeUndefined()
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

/** 通过真实模态动作丢弃当前草稿, 供原有选择行为用例显式表达新确认流程. */
async function discardPendingDraft(): Promise<void> {
    await flushPromises()
    const button = new DOMWrapper(document.body).findAll(".el-dialog button").find((node) => node.text() === "放弃修改")
    expect(button).toBeDefined()
    await button!.trigger("click")
    await flushPromises()
}

/** 按可见操作文案定位真实未保存模态按钮, 不缓存其他测试作用域的节点. */
function dialogButton(label: string) {
    return new DOMWrapper(document.body).findAll(".el-dialog button").find((node) => node.text() === label)!
}
