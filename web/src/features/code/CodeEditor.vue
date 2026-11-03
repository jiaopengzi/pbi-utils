<!--
 * FilePath    : pbi-utils\web\src\features\code\CodeEditor.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享 CodeMirror 编辑器, 保留受控原文, 输入法, 撤销和本地语言高亮.
-->

<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue"
import { defaultKeymap, history, historyKeymap, invertedEffects, isolateHistory } from "@codemirror/commands"
import { bracketMatching, indentUnit, syntaxHighlighting } from "@codemirror/language"
import {
    search,
    searchKeymap,
    highlightSelectionMatches,
    closeSearchPanel,
    openSearchPanel,
    searchPanelOpen,
    getSearchQuery,
    setSearchQuery,
} from "@codemirror/search"
import { Annotation, Compartment, EditorState, Prec, StateEffect, type Transaction } from "@codemirror/state"
import {
    drawSelection,
    dropCursor,
    EditorView,
    highlightActiveLine,
    highlightActiveLineGutter,
    highlightSpecialChars,
    keymap,
    lineNumbers,
    rectangularSelection,
    placeholder as editorPlaceholder,
    type ViewUpdate,
} from "@codemirror/view"
import { codeLanguage, normalizeLanguage } from "./codeLanguages"
import { codeHighlighter } from "./codeTheme"
import { editorPhrases } from "./editorPhrases"
import { detectSeparator, sourceText, editorText, replacement, boundedComposition } from "./editorText"

/** 原文由父级拥有; 语言配置只负责本地高亮, 格式化结果由父级回填. */
const { locale, t } = useI18n()
const props = withDefaults(
    defineProps<{
        language?: string
        label?: string
        disabled?: boolean
        readonly?: boolean
        rows?: number
        maxlength?: number | null
        placeholder?: string
        submitMode?: "enter" | "ctrl-enter"
    }>(),
    {
        language: "dax",
        rows: 7,
        maxlength: 30000,
    },
)
const model = defineModel<string>({ required: true })
const emit = defineEmits<{ submit: []; blur: [event: FocusEvent] }>()
const host = ref<HTMLElement>()
const composing = ref(false)
/** 默认无障碍名称与编辑语言绑定, 调用方可提供具体表单名称. */
const languageLabelKeys: Record<string, string> = {
    dax: "model.daxExpression",
    m: "model.mExpression",
    tmdl: "model.tmdlDefinition",
    markdown: "model.markdownDocument",
}
const editorLabel = computed(() => props.label ?? t(languageLabelKeys[normalizeLanguage(props.language)] ?? "model.code"))
const editorRows = computed(() => (Number.isFinite(props.rows) ? Math.max(1, Math.floor(props.rows)) : 7))
const languageCompartment = new Compartment()
const editableCompartment = new Compartment()
const attributesCompartment = new Compartment()
const separatorCompartment = new Compartment()
const localeCompartment = new Compartment()
const placeholderCompartment = new Compartment()
const externalUpdate = Annotation.define<boolean>()
const separatorEffect = StateEffect.define<string>()
let view: EditorView | undefined
let compositionBase = ""
let pendingExternal: string | undefined
let pendingConfiguration = false
let compositionTimer: ReturnType<typeof setTimeout> | undefined
let configuredLocale = locale.value

/** 可选提交模式优先于默认换行处理; 组合输入、搜索栏、长按和修饰组合不发送, 其他编辑器保持原快捷键. */
function submitKey(event: KeyboardEvent, editor: EditorView): boolean {
    if (!props.submitMode || event.key !== "Enter" || !editor.contentDOM.contains(event.target as Node)) return false
    if (props.disabled || props.readonly) return true
    if (composing.value || editor.composing || event.isComposing || event.keyCode === 229 || event.repeat || event.shiftKey || event.altKey || event.metaKey)
        return false
    if ((props.submitMode === "ctrl-enter") === event.ctrlKey) {
        event.preventDefault()
        emit("submit")
        return true
    }
    if (event.ctrlKey) {
        event.preventDefault()
        editor.dispatch({ ...editor.state.replaceSelection(editor.state.lineBreak), userEvent: "input" })
        return true
    }
    return false
}

/** 将焦点交给已挂载的原编辑视图, 调用不替换 DOM、选区或撤销历史. */
function focus(): void {
    view?.focus()
}
/** 将混合剪贴板中的文字插入当前选区, 复用权限、长度与撤销约束; 禁用或组合输入时不修改. */
function insertText(text: string): void {
    if (!view || props.disabled || props.readonly || composing.value || !text) return
    view.dispatch({ ...view.state.replaceSelection(sourceText(text)), userEvent: "input.paste", scrollIntoView: true })
}
defineExpose({ focus, insertText })

/** 将可撤销的分隔符效果映射到静态配置, 历史恢复也经过这一扩展器. */
function updateSeparator(transaction: Transaction) {
    const effect = transaction.effects.findLast((item) => item.is(separatorEffect))
    return effect ? { effects: separatorCompartment.reconfigure(EditorState.lineSeparator.of(effect.value)) } : null
}

/** 限制用户编辑的最终 UTF-16 长度, 外部回填和历史恢复保留完整原文. */
function acceptsEdit(transaction: Transaction) {
    if (!transaction.docChanged || transaction.annotation(externalUpdate)) return true
    if (props.readonly || (props.disabled && !(composing.value && transaction.isUserEvent("input.type.compose")))) return false
    if (composing.value || transaction.isUserEvent("undo") || transaction.isUserEvent("redo")) return true
    const length = transaction.newDoc.sliceString(0, transaction.newDoc.length, transaction.startState.lineBreak).length
    return length <= maximumLength() || length <= editorText(transaction.startState).length
}

/** 在状态交易阶段统一拦截键入, 粘贴, 搜索替换和多选改动. */
function filterEdit(transaction: Transaction) {
    return acceptsEdit(transaction) ? transaction : []
}

/** 再次核验跳过普通过滤的历史命令; 组合期间禁用也不得绕过权限. */
function dispatchEdits(transactions: readonly Transaction[], editor: EditorView) {
    if (transactions.every(acceptsEdit)) editor.update(transactions)
}

/** null 显式取消字符上限, 其他调用继续默认 30000; 外部超长文本完整保留. */
function maximumLength() {
    if (props.maxlength === null) return Number.POSITIVE_INFINITY
    return Number.isFinite(props.maxlength) ? Math.max(0, Math.floor(props.maxlength)) : 30000
}

/** 将后端格式化或父级替换作为独立撤销步骤回填, 相同回显不会新增交易. */
function applyExternal(value: string) {
    if (!view || editorText(view.state) === value) return
    const separator = detectSeparator(value, view.state.lineBreak)
    view.dispatch({
        changes: replacement(view.state, value),
        effects: separator !== view.state.lineBreak ? separatorEffect.of(separator) : [],
        annotations: [externalUpdate.of(true), isolateHistory.of("full")],
        userEvent: "input.external",
    })
}

/** 仅提交完整用户文本, 避免父级回显在输入法组合过程中重写内容. */
function emitDocument() {
    if (!view || props.disabled || props.readonly || composing.value || compositionTimer !== undefined) return
    const value = editorText(view.state)
    if (value !== model.value) model.value = value
}

/** 监听实际文档改动; 配置变更和外部回填不会产生新的编辑事件. */
function onViewUpdate(update: ViewUpdate) {
    if (
        (update.docChanged || update.startState.lineBreak !== update.state.lineBreak) &&
        !update.transactions.every((transaction) => transaction.annotation(externalUpdate))
    )
        emitDocument()
}

/** 创建可热更新的输入权限和无障碍属性, 禁用时保留读取及复制能力. */
function editorAttributes() {
    return EditorView.contentAttributes.of({
        "aria-label": editorLabel.value,
        "aria-disabled": String(Boolean(props.disabled)),
        "aria-readonly": String(Boolean(props.readonly)),
        spellcheck: "false",
        autocapitalize: "off",
        autocomplete: "off",
        autocorrect: "off",
        tabindex: props.disabled ? "-1" : "0",
    })
}

/** 只重新配置现有视图; 保留文档, 选区, 搜索状态和撤销历史. */
function configureEditor() {
    if (!view) return
    if (composing.value) {
        pendingConfiguration = true
        return
    }
    pendingConfiguration = false
    const refreshSearch = configuredLocale !== locale.value && searchPanelOpen(view.state)
    const query = refreshSearch ? getSearchQuery(view.state) : undefined
    const activeElement = view.root.activeElement as HTMLElement | null
    const activeName = view.dom.querySelector(".cm-search")?.contains(activeElement) ? activeElement?.getAttribute("name") : undefined
    const selection = activeElement instanceof HTMLInputElement ? [activeElement.selectionStart, activeElement.selectionEnd] : undefined
    // 搜索面板的内置标签在构造时读取 phrases, 只重新打开该面板, 保留整个编辑视图与查询.
    if (refreshSearch) closeSearchPanel(view)
    view.dispatch({
        effects: [
            languageCompartment.reconfigure(codeLanguage(props.language)),
            editableCompartment.reconfigure([
                EditorView.editable.of(!props.disabled && !props.readonly),
                EditorState.readOnly.of(Boolean(props.disabled || props.readonly)),
            ]),
            attributesCompartment.reconfigure(editorAttributes()),
            // CodeMirror 会因 phrases 引用改变重建整棵视图, 普通锁定不重建字典, 避免长行滚动测量反复失效.
            ...(configuredLocale !== locale.value ? [localeCompartment.reconfigure(EditorState.phrases.of(editorPhrases(t)))] : []),
            placeholderCompartment.reconfigure(props.placeholder ? editorPlaceholder(props.placeholder) : []),
        ],
    })
    configuredLocale = locale.value
    if (refreshSearch && query) {
        openSearchPanel(view)
        view.dispatch({ effects: setSearchQuery.of(query) })
        const nextActive = activeName ? view.dom.querySelector<HTMLElement>(`.cm-search [name="${activeName}"]`) : activeElement
        if (nextActive?.isConnected) {
            nextActive.focus()
            if (nextActive instanceof HTMLInputElement && nextActive.type === "text" && selection)
                nextActive.setSelectionRange(selection[0] ?? 0, selection[1] ?? 0)
        }
    }
}

/** 结束组合输入后统一限制和同步; 排队的父级替换具有最终优先权. */
function finishComposition() {
    if (compositionTimer !== undefined) clearTimeout(compositionTimer)
    compositionTimer = undefined
    if (!view) return
    const current = editorText(view.state)
    const finalValue = props.disabled || props.readonly ? compositionBase : boundedComposition(current, compositionBase, maximumLength())
    if (finalValue !== current) {
        view.dispatch({ changes: replacement(view.state, finalValue), annotations: externalUpdate.of(true), userEvent: "input.type.compose" })
    }
    composing.value = false
    if (pendingConfiguration) configureEditor()
    const external = pendingExternal
    pendingExternal = undefined
    if (external !== undefined) applyExternal(external)
    else emitDocument()
}

/** 记录组合起点; 不取消浏览器事件或替换输入节点. */
function startComposition() {
    if (!view || props.disabled || props.readonly) return false
    if (compositionTimer !== undefined) finishComposition()
    compositionBase = editorText(view.state)
    composing.value = true
    return false
}

/** 等待本轮最后的 DOM 输入交易后提交, 卸载时取消这一短期任务. */
function endComposition() {
    if (!composing.value) return false
    compositionTimer = setTimeout(finishComposition, 0)
    return false
}

watch(
    model,
    (value) => {
        if (!view) return
        if (composing.value) pendingExternal = value
        else applyExternal(value)
    },
    { flush: "sync" },
)
watch(() => [props.language, props.disabled, props.readonly, props.placeholder, editorLabel.value, locale.value], configureEditor, { flush: "sync" })

onMounted(() => {
    if (!host.value) return
    view = new EditorView({
        parent: host.value,
        dispatchTransactions: dispatchEdits,
        state: EditorState.create({
            doc: sourceText(model.value),
            extensions: [
                EditorView.cspNonce.of(__EDITOR_STYLE_NONCE__),
                separatorCompartment.of(EditorState.lineSeparator.of(detectSeparator(model.value))),
                EditorState.transactionExtender.of(updateSeparator),
                invertedEffects.of((transaction) =>
                    transaction.effects.some((effect) => effect.is(separatorEffect)) ? [separatorEffect.of(transaction.startState.lineBreak)] : [],
                ),
                languageCompartment.of(codeLanguage(props.language)),
                editableCompartment.of([
                    EditorView.editable.of(!props.disabled && !props.readonly),
                    EditorState.readOnly.of(Boolean(props.disabled || props.readonly)),
                ]),
                attributesCompartment.of(editorAttributes()),
                localeCompartment.of(EditorState.phrases.of(editorPhrases(t))),
                placeholderCompartment.of(props.placeholder ? editorPlaceholder(props.placeholder) : []),
                EditorState.allowMultipleSelections.of(true),
                EditorState.tabSize.of(4),
                // 只改变视图换行, 原文、搜索、选区与只读权限继续由同一视图维护.
                EditorView.lineWrapping,
                indentUnit.of("    "),
                history(),
                lineNumbers(),
                drawSelection(),
                dropCursor(),
                rectangularSelection(),
                highlightActiveLine(),
                highlightActiveLineGutter(),
                highlightSpecialChars(),
                bracketMatching(),
                syntaxHighlighting(codeHighlighter),
                highlightSelectionMatches(),
                search({ top: true }),
                Prec.highest(EditorView.domEventHandlers({ keydown: submitKey })),
                keymap.of([...defaultKeymap, ...historyKeymap, ...searchKeymap]),
                EditorState.transactionFilter.of(filterEdit),
                EditorView.updateListener.of(onViewUpdate),
                EditorView.domEventHandlers({
                    compositionstart: startComposition,
                    compositionend: endComposition,
                    blur: (event) => {
                        emit("blur", event)
                        return false
                    },
                }),
            ],
        }),
    })
})

onBeforeUnmount(() => {
    if (compositionTimer !== undefined) clearTimeout(compositionTimer)
    view?.destroy()
    view = undefined
})
</script>

<template>
    <div class="code-editor" :class="{ 'is-disabled': disabled, 'is-composing': composing }" :style="{ '--editor-rows': editorRows }">
        <div ref="host" class="code-editor__surface" :data-lang="normalizeLanguage(language)"></div>
    </div>
</template>

<style scoped lang="scss">
.code-editor {
    width: 100%;
    min-width: 0;
}
</style>
