/**
 * FilePath    : pbi-utils\web\src\features\code\editorText.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供编辑器原文、换行、最小替换与输入法长度边界的纯计算.
 */

import { Text, type ChangeSpec, type EditorState } from "@codemirror/state"

/** 检测 value 的首次换行; 没有换行时返回 fallback, 初始默认 LF. */
export function detectSeparator(value: string, fallback = "\n") {
    return value.match(/\r\n|\r|\n/)?.[0] ?? fallback
}

/** 将 value 的不同换行统一为 CodeMirror 文档行, 返回 Text, 避免把 CR 写成可见字符. */
export function sourceText(value: string) {
    return Text.of(value.split(/\r\n|\r|\n/))
}

/** 按 state 绑定的分隔符返回完整原文; 不把 CRLF 静默替换成 LF. */
export function editorText(state: EditorState) {
    return state.doc.sliceString(0, state.doc.length, state.lineBreak)
}

/** 对比 state 与新原文 value, 返回保留未变区域选区映射的最小替换; 文档相同时返回 undefined. */
export function replacement(state: EditorState, value: string): ChangeSpec | undefined {
    const current = state.doc.toString()
    const next = sourceText(value).toString()
    if (current === next) return undefined
    let from = 0
    while (from < current.length && from < next.length && current[from] === next[from]) from++
    let to = current.length
    let nextEnd = next.length
    while (to > from && nextEnd > from && current[to - 1] === next[nextEnd - 1]) {
        to--
        nextEnd--
    }
    return { from, to, insert: Text.of(next.slice(from, nextEnd).split("\n")) }
}

/** 对比 value 与组合起点 compositionBase, 按 UTF-16 上限 limit 返回裁剪原文, 保留旧文本并避免截断 Unicode 代理对和 CRLF. */
export function boundedComposition(value: string, compositionBase: string, limit: number) {
    if (value.length <= limit || value.length <= compositionBase.length) return value
    if (compositionBase.length > limit) return compositionBase
    let start = 0
    while (start < compositionBase.length && start < value.length && compositionBase[start] === value[start]) start++
    let oldEnd = compositionBase.length
    let newEnd = value.length
    while (oldEnd > start && newEnd > start && compositionBase[oldEnd - 1] === value[newEnd - 1]) {
        oldEnd--
        newEnd--
    }
    let capacity = Math.max(0, limit - compositionBase.length + oldEnd - start)
    const inserted = value.slice(start, newEnd)
    if (capacity > 0 && /[\uD800-\uDBFF]/.test(inserted[capacity - 1] ?? "") && /[\uDC00-\uDFFF]/.test(inserted[capacity] ?? "")) capacity--
    if (inserted[capacity - 1] === "\r" && inserted[capacity] === "\n") capacity--
    return compositionBase.slice(0, start) + inserted.slice(0, capacity) + compositionBase.slice(oldEnd)
}
