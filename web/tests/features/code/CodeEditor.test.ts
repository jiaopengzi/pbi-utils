/**
 * FilePath    : pbi-utils\web\tests\features\code\CodeEditor.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证共享编辑器的受控原文, 撤销, 权限, 组合输入和长度边界.
 */
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { mount, type VueWrapper } from "@vue/test-utils"
import { indentMore, redo, undo, undoDepth } from "@codemirror/commands"
import { EditorSelection, EditorState } from "@codemirror/state"
import { EditorView } from "@codemirror/view"
import { openSearchPanel, getSearchQuery, SearchQuery, setSearchQuery } from "@codemirror/search"
import { setLocale } from "@/i18n"
import CodeEditor from "@/features/code/CodeEditor.vue"

const mounted: VueWrapper[] = []

/** 创建真实 CodeMirror 视图, 通过公共 DOM 查找 API 读取测试状态. */
function createEditor(props: {
    modelValue: string
    language?: string
    label?: string
    disabled?: boolean
    readonly?: boolean
    maxlength?: number
    rows?: number
    submitMode?: "enter" | "ctrl-enter"
    placeholder?: string
}) {
    const wrapper = mount(CodeEditor, { props, attachTo: document.body })
    mounted.push(wrapper)
    const view = EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)
    if (!view) throw new Error("CodeMirror 视图未挂载")
    return { wrapper, view }
}

it("Markdown 发送快捷键在默认换行之前处理, 组合输入和长按不提交, 普通编辑仍可撤销", async () => {
    const { wrapper, view } = createEditor({ modelValue: "草稿", language: "markdown", submitMode: "ctrl-enter", placeholder: "输入内容" })
    const press = (options: KeyboardEventInit) =>
        view.contentDOM.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, cancelable: true, key: "Enter", code: "Enter", ...options }))
    expect(wrapper.find(".cm-placeholder").exists()).toBe(false)
    view.dispatch({ selection: { anchor: 2 } })
    press({ ctrlKey: true })
    expect(wrapper.emitted("submit")).toHaveLength(1)
    expect(text(view)).toBe("草稿")
    press({ ctrlKey: true, isComposing: true })
    press({ ctrlKey: true, repeat: true })
    press({ ctrlKey: true, shiftKey: true })
    expect(wrapper.emitted("submit")).toHaveLength(1)
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: "草稿" }, selection: { anchor: 2 } })
    await wrapper.setProps({ submitMode: "enter" })
    press({ ctrlKey: true })
    expect(text(view)).toBe("草稿\n")
    undo(view)
    expect(text(view)).toBe("草稿")
    press({})
    expect(wrapper.emitted("submit")).toHaveLength(2)
    await wrapper.setProps({ disabled: true })
    press({})
    expect(wrapper.emitted("submit")).toHaveLength(2)
})

/** 读出受控原文, 让 CRLF 测试明确使用文档绑定的分隔符. */
function text(view: EditorView) {
    return view.state.doc.sliceString(0, view.state.doc.length, view.state.lineBreak)
}

beforeEach(() => {
    // jsdom 不执行页面排版, 这些几何桩只支持视图测量, 不模拟编辑逻辑.
    vi.spyOn(Range.prototype, "getClientRects").mockReturnValue([] as unknown as DOMRectList)
    vi.spyOn(Range.prototype, "getBoundingClientRect").mockReturnValue(new DOMRect())
})

afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount()
    vi.restoreAllMocks()
    vi.useRealTimers()
})

it("真实编辑视图保留危险文本, 行号和无障碍名称, Tab 不阻止焦点导航", () => {
    const raw = "SUM('销售'[金额])\n\"<img src=x>\" // <script>alert(1)</script>"
    const { wrapper, view } = createEditor({ modelValue: raw })
    expect(text(view)).toBe(raw)
    expect(view.contentDOM.getAttribute("aria-label")).toBe("DAX 表达式")
    expect(wrapper.find(".cm-lineNumbers").exists()).toBe(true)
    expect(wrapper.find("img, script").exists()).toBe(false)
    const tab = new KeyboardEvent("keydown", { key: "Tab", code: "Tab", cancelable: true, bubbles: true })
    view.contentDOM.dispatchEvent(tab)
    expect(tab.defaultPrevented).toBe(false)
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})
it("Markdown 描述高亮标题、强调、链接和 DAX/M 围栏, 编辑与切语言保留原文及历史", async () => {
    const raw =
        "# 说明\n\n**粗体** 与 [链接](https://example.com)\n\n```dax\nRETURN SUM('销售'[金额])\n```\n\n```m\nlet Value = Text.From(1) in Value\n```\n\n<img src=x onerror=alert(1)>"
    const { wrapper, view } = createEditor({ modelValue: raw, language: "md", label: "描述", maxlength: 8192 })
    expect(wrapper.get(".code-editor__surface").attributes("data-lang")).toBe("markdown")
    expect(wrapper.get(".pbi-code-heading").text()).toContain("说明")
    expect(wrapper.get(".pbi-code-strong").text()).toContain("粗体")
    expect(wrapper.find(".pbi-code-link").exists()).toBe(true)
    expect(wrapper.find(".pbi-code-function").exists()).toBe(true)
    expect(wrapper.find(".pbi-code-dax.pbi-code-function").text()).toBe("SUM")
    expect(wrapper.find(".pbi-code-m.pbi-code-function").text()).toBe("Text.From")
    expect(wrapper.find("img, script, a").exists()).toBe(false)
    expect(text(view)).toBe(raw)
    view.dispatch({ changes: { from: raw.length, insert: "\n- 备注" }, userEvent: "input" })
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([raw + "\n- 备注"])
    const selection = view.state.selection.toJSON()
    const depth = undoDepth(view.state)
    await wrapper.setProps({ language: "markdown", disabled: true })
    expect(view.state.selection.toJSON()).toEqual(selection)
    expect(undoDepth(view.state)).toBe(depth)
    expect(view.state.readOnly).toBe(true)
    view.dispatch({ changes: { from: 0, insert: "blocked" }, userEvent: "input" })
    expect(text(view)).toBe(raw + "\n- 备注")
    await wrapper.setProps({ disabled: false })
    undo(view)
    expect(text(view)).toBe(raw)
})
it("长行按可用宽度换行, 保留只读原文、搜索查询和选区", async () => {
    const raw = `let Value = "${"CalendarLongToken".repeat(80)}" in Value`
    const { wrapper, view } = createEditor({ modelValue: raw, language: "m", readonly: true })
    expect(view.lineWrapping).toBe(true)
    openSearchPanel(view)
    view.dispatch({ effects: setSearchQuery.of(new SearchQuery({ search: "CalendarLongToken" })), selection: EditorSelection.range(13, 30) })
    const selection = view.state.selection.toJSON()
    await wrapper.setProps({ rows: 14, label: "日期函数" })
    expect(text(view)).toBe(raw)
    expect(view.state.selection.toJSON()).toEqual(selection)
    expect(getSearchQuery(view.state).search).toBe("CalendarLongToken")
    expect(view.state.readOnly).toBe(true)
    expect(view.contentDOM.getAttribute("tabindex")).toBe("0")
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})
it("只读预览保留键盘焦点和搜索选择, 拒绝所有编辑并接受完整宿主回填", async () => {
    const { wrapper, view } = createEditor({ modelValue: "let\n    Value = 1\nin\n    Value", language: "m" })
    view.dispatch({ changes: { from: 0, insert: "// draft\n" }, userEvent: "input" })
    await wrapper.setProps({ readonly: true })
    const original = text(view)
    expect(view.state.readOnly).toBe(true)
    expect(view.contentDOM.getAttribute("tabindex")).toBe("0")
    expect(view.contentDOM.getAttribute("aria-readonly")).toBe("true")
    expect(view.contentDOM.getAttribute("aria-disabled")).toBe("false")
    view.contentDOM.focus()
    expect(document.activeElement).toBe(view.contentDOM)
    view.dispatch({ changes: { from: 0, insert: "forbidden" }, userEvent: "input.paste" })
    undo(view)
    redo(view)
    indentMore(view)
    view.contentDOM.dispatchEvent(new CompositionEvent("compositionstart", { bubbles: true }))
    view.dispatch({ changes: { from: 0, insert: "输入" }, userEvent: "input.type.compose" })
    view.contentDOM.dispatchEvent(new CompositionEvent("compositionend", { bubbles: true }))
    expect(text(view)).toBe(original)
    openSearchPanel(view)
    expect(wrapper.find(".cm-search").exists()).toBe(true)
    view.dispatch({ selection: { anchor: 0, head: original.length } })
    expect(view.state.selection.main.to).toBe(original.length)
    // 外部回填前移出编辑焦点, 避免 DOM 选区事件在回填期间重入视图测量.
    view.contentDOM.blur()
    await wrapper.setProps({ modelValue: "// " + "宿主原文".repeat(10000), maxlength: 1 })
    expect(text(view)).toBe("// " + "宿主原文".repeat(10000))
})
it("语言切换热更新搜索面板与默认名称, 保留原文、选区、撤销和搜索替换条件", async () => {
    const { wrapper, view } = createEditor({ modelValue: "SUM('销售'[金额])" })
    view.dispatch({ changes: { from: 0, to: 0, insert: "1 + " }, selection: { anchor: 4, head: 7 }, userEvent: "input" })
    const content = text(view)
    const selection = view.state.selection.toJSON()
    const history = undoDepth(view.state)
    openSearchPanel(view)
    view.dispatch({ effects: setSearchQuery.of(new SearchQuery({ search: "SUM", replace: "MAX", caseSensitive: true, regexp: true, wholeWord: true })) })
    const search = wrapper.get<HTMLInputElement>('.cm-search input[name="search"]')
    expect(search.attributes("aria-label")).toBe("查找")
    expect(wrapper.get('.cm-search button[name="replaceAll"]').text()).toBe("全部替换")
    search.element.focus()
    search.element.setSelectionRange(1, 2)
    setLocale("en-us")
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.cm-search input[name="search"]').attributes("aria-label")).toBe("Find")
    expect(wrapper.get('.cm-search input[name="replace"]').attributes("placeholder")).toBe("Replace")
    expect(wrapper.get('.cm-search button[name="replaceAll"]').text()).toBe("replace all")
    expect(wrapper.get('.cm-search button[name="close"]').attributes("aria-label")).toBe("close")
    expect(view.contentDOM.getAttribute("aria-label")).toBe("DAX expression")
    expect(view.state.phrase("replaced $ matches", 2)).toBe("replaced 2 matches")
    expect(getSearchQuery(view.state)).toMatchObject({ search: "SUM", replace: "MAX", caseSensitive: true, regexp: true, wholeWord: true })
    expect(wrapper.get<HTMLInputElement>('.cm-search input[name="search"]').element.selectionStart).toBe(1)
    expect(wrapper.get<HTMLInputElement>('.cm-search input[name="search"]').element.selectionEnd).toBe(2)
    expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)).toBe(view)
    expect(text(view)).toBe(content)
    expect(view.state.selection.toJSON()).toEqual(selection)
    expect(undoDepth(view.state)).toBe(history)
    setLocale("zh-cn")
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.cm-search input[name="search"]').attributes("aria-label")).toBe("查找")
    expect(wrapper.get('.cm-search button[name="replaceAll"]').text()).toBe("全部替换")
    expect(view.state.phrase("replaced $ matches", 2)).toBe("已替换 2 个匹配项")
    expect(view.contentDOM.getAttribute("aria-label")).toBe("DAX 表达式")
})

it("用户缩进可撤销, 相同受控回显不添加历史, 后端回填独立撤销", async () => {
    const { wrapper, view } = createEditor({ modelValue: "VAR a = 1\nRETURN a\nnext" })
    view.dispatch({ selection: EditorSelection.range(19, 0) })
    indentMore(view)
    expect(text(view)).toBe("    VAR a = 1\n    RETURN a\nnext")
    expect(view.state.selection.main.anchor).toBeGreaterThan(view.state.selection.main.head)
    const depth = undoDepth(view.state)
    await wrapper.setProps({ modelValue: text(view) })
    expect(undoDepth(view.state)).toBe(depth)
    await wrapper.setProps({ modelValue: "VAR a = 1\n\nRETURN a\nnext" })
    expect(undoDepth(view.state)).toBe(depth + 1)
    undo(view)
    expect(text(view)).toBe("    VAR a = 1\n    RETURN a\nnext")
    undo(view)
    expect(text(view)).toBe("VAR a = 1\nRETURN a\nnext")
    redo(view)
    expect(text(view)).toBe("    VAR a = 1\n    RETURN a\nnext")
})

it("语言, 标签, 禁用和上限热更新保留视图, 选区和撤销历史", async () => {
    const { wrapper, view } = createEditor({ modelValue: "RETURN 1" })
    view.dispatch({ changes: { from: 7, to: 8, insert: "2" }, selection: EditorSelection.range(8, 7), userEvent: "input" })
    const node = view.contentDOM
    const selection = view.state.selection.toJSON()
    const depth = undoDepth(view.state)
    await wrapper.setProps({ language: "m", label: "查询表达式", disabled: true, maxlength: 5, rows: 9 })
    expect(EditorView.findFromDOM(wrapper.get(".cm-editor").element as HTMLElement)).toBe(view)
    expect(view.contentDOM).toBe(node)
    expect(view.state.selection.toJSON()).toEqual(selection)
    expect(undoDepth(view.state)).toBe(depth)
    expect(view.state.readOnly).toBe(true)
    expect(view.contentDOM.getAttribute("contenteditable")).toBe("false")
    expect(view.contentDOM.getAttribute("aria-label")).toBe("查询表达式")
    expect(view.state.facet(EditorState.tabSize)).toBe(4)
    view.dispatch({ changes: { from: 0, insert: "blocked" }, userEvent: "input.paste" })
    undo(view)
    expect(text(view)).toBe("RETURN 2")
    expect(undoDepth(view.state)).toBe(depth)
    await wrapper.setProps({ modelValue: "父级完整回填不截断" })
    expect(text(view)).toBe("父级完整回填不截断")
    await wrapper.setProps({ disabled: false })
    expect(view.state.readOnly).toBe(false)
    expect(view.contentDOM.getAttribute("contenteditable")).toBe("true")
})

it("输入, 粘贴, 多选交易和缩进统一遵守 maxlength, 超长原文允许缩短", async () => {
    const { wrapper, view } = createEditor({ modelValue: "abcd", maxlength: 6 })
    view.dispatch({ changes: { from: 4, insert: "ef" }, userEvent: "input.type" })
    expect(text(view)).toBe("abcdef")
    const accepted = wrapper.emitted("update:modelValue")?.length
    view.dispatch({ changes: { from: 6, insert: "x" }, userEvent: "input.type" })
    view.dispatch({ changes: { from: 2, insert: "长粘贴" }, userEvent: "input.paste" })
    view.dispatch({
        changes: [
            { from: 0, to: 1, insert: "12" },
            { from: 3, to: 4, insert: "34" },
        ],
        userEvent: "input",
    })
    indentMore(view)
    expect(text(view)).toBe("abcdef")
    expect(wrapper.emitted("update:modelValue")?.length).toBe(accepted)
    await wrapper.setProps({ modelValue: "123456789" })
    expect(text(view)).toBe("123456789")
    view.dispatch({ changes: { from: 7, to: 9 }, userEvent: "delete" })
    expect(text(view)).toBe("1234567")
    view.dispatch({ changes: { from: 7, insert: "8" }, userEvent: "input.type" })
    expect(text(view)).toBe("1234567")
})

it("中文组合期间不发送半成品, 提交后只发送最终文本并保留选区", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "" })
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 0, insert: "xiao" }, userEvent: "input.type.compose" })
    view.dispatch({ changes: { from: 0, to: 4, insert: "销售" }, selection: { anchor: 2 }, userEvent: "input.type.compose" })
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    expect(view.state.selection.main.head).toBe(2)
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(wrapper.emitted("update:modelValue")).toEqual([["销售"]])
    expect(view.state.selection.main.head).toBe(2)
    undo(view)
    expect(text(view)).toBe("")
})

it("组合输入临时允许超过上限, 最终裁剪新增部分且不截断代理对", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "原", maxlength: 4 })
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 1, insert: "中😀文" }, userEvent: "input.type.compose" })
    expect(text(view)).toBe("原中😀文")
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("原中😀")
    expect(wrapper.emitted("update:modelValue")).toEqual([["原中😀"]])
    undo(view)
    expect(text(view)).toBe("原")
    await wrapper.setProps({ modelValue: "原", maxlength: 3 })
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 1, insert: "中😀" }, userEvent: "input.type.compose" })
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("原中")
})

it("组合期间延迟父级替换和语言配置, 完成后使用最新回填并避免旧草稿覆盖", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "原" })
    const node = view.contentDOM
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 1, insert: "销售" }, userEvent: "input.type.compose" })
    await wrapper.setProps({ modelValue: "后端回填", language: "tmdl", label: "模型定义" })
    expect(text(view)).toBe("原销售")
    expect(view.contentDOM).toBe(node)
    expect(view.contentDOM.getAttribute("aria-label")).toBe("DAX 表达式")
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("后端回填")
    expect(view.contentDOM.getAttribute("aria-label")).toBe("模型定义")
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})

it.each(["disabled", "readonly"] as const)("组合期间设置%s不重建输入节点, 阻止粘贴和历史命令并恢复组合前原文", async (permission) => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "原" })
    view.dispatch({ changes: { from: 0, to: 1, insert: "初" }, userEvent: "input" })
    const emitted = wrapper.emitted("update:modelValue")?.length
    const node = view.contentDOM
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 1, insert: "销售" }, userEvent: "input.type.compose" })
    await wrapper.setProps({ [permission]: true })
    expect(view.contentDOM).toBe(node)
    view.dispatch({ changes: { from: 0, insert: "blocked" }, userEvent: "input.paste" })
    undo(view)
    expect(text(view)).toBe("初销售")
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("初")
    expect(view.state.readOnly).toBe(true)
    expect(wrapper.emitted("update:modelValue")?.length).toBe(emitted)
})

it("组合期间降低上限保留已有超长字符, 不裁成半个代理对", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "😀", maxlength: 4 })
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 0, to: 2, insert: "😃中" }, userEvent: "input.type.compose" })
    await wrapper.setProps({ maxlength: 1 })
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("😀")
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})

it("CRLF 的两个字符都计入上限, 组合裁剪不留下半个换行", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "a\r\nb", maxlength: 6 })
    view.dispatch({ changes: { from: 2, insert: "\r\n" }, userEvent: "input" })
    expect(text(view)).toBe("a\r\n\r\nb")
    view.dispatch({ changes: { from: view.state.doc.length, insert: "c" }, userEvent: "input" })
    expect(text(view)).toBe("a\r\n\r\nb")
    await wrapper.setProps({ modelValue: text(view) })
    await wrapper.setProps({ modelValue: "a\r\nb", maxlength: 5 })
    const emitted = wrapper.emitted("update:modelValue")?.length
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: view.state.doc.length, insert: "\r\nc" }, userEvent: "input.type.compose" })
    await wrapper.get(".cm-content").trigger("compositionend")
    await vi.advanceTimersByTimeAsync(1)
    expect(text(view)).toBe("a\r\nb")
    expect(wrapper.emitted("update:modelValue")?.length).toBe(emitted)
})

it("CRLF 和 CR 原文不因挂载或回显改变, 分隔符随外部更新一起撤销", async () => {
    const original = "VAR x = 1\r\nRETURN x\r\n"
    const { wrapper, view } = createEditor({ modelValue: original })
    expect(text(view)).toBe(original)
    expect(view.state.doc.lines).toBe(3)
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    await wrapper.setProps({ modelValue: original })
    expect(undoDepth(view.state)).toBe(0)
    await wrapper.setProps({ modelValue: "VAR x = 2\rRETURN x\r" })
    expect(view.state.lineBreak).toBe("\r")
    expect(text(view)).toBe("VAR x = 2\rRETURN x\r")
    undo(view)
    expect(view.state.lineBreak).toBe("\r\n")
    expect(text(view)).toBe(original)
    redo(view)
    expect(view.state.lineBreak).toBe("\r")
    expect(text(view)).toBe("VAR x = 2\rRETURN x\r")
})

it("仅换行分隔符变化也可撤销, 撤销和重做同步父级实际保存文本", async () => {
    const original = "VAR x = 1\r\nRETURN x"
    const changed = "VAR x = 1\nRETURN x"
    const { wrapper, view } = createEditor({ modelValue: original })
    await wrapper.setProps({ modelValue: changed })
    expect(view.state.doc.toString()).toBe(changed)
    expect(text(view)).toBe(changed)
    expect(undoDepth(view.state)).toBe(1)
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
    undo(view)
    expect(text(view)).toBe(original)
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([original])
    redo(view)
    expect(text(view)).toBe(changed)
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([changed])
})

it("搜索使用编辑器原文, 卸载释放视图并取消排队的组合提交", async () => {
    vi.useFakeTimers()
    const { wrapper, view } = createEditor({ modelValue: "SUM([金额])\nSUM([数量])" })
    expect(openSearchPanel(view)).toBe(true)
    expect(wrapper.find('.cm-search input[name="search"]').exists()).toBe(true)
    await wrapper.get(".cm-content").trigger("compositionstart")
    view.dispatch({ changes: { from: 0, insert: "最终" }, userEvent: "input.type.compose" })
    await wrapper.get(".cm-content").trigger("compositionend")
    const destroy = vi.spyOn(view, "destroy")
    wrapper.unmount()
    expect(destroy).toHaveBeenCalledOnce()
    await vi.advanceTimersByTimeAsync(1)
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})
