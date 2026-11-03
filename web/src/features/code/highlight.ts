/**
 * FilePath    : pbi-utils\web\src\features\code\highlight.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用 CodeMirror 同源语法树安全绘制 DAX/M/TMDL 代码预览.
 */
import { highlightTree } from "@lezer/highlight"
import { getCodeLanguage, normalizeLanguage } from "./codeLanguages"
import { codeHighlighter } from "./codeTheme"

export { normalizeLanguage }

/** 从原文创建纯文本和着色节点; 超限或解析失败恢复完整原文, 不执行 HTML. */
export function paintCode(node: HTMLElement, code: string, language: string) {
    node.textContent = code
    const normalized = normalizeLanguage(language)
    node.className = `language-${normalized}`
    const grammar = getCodeLanguage(normalized)
    if (!grammar || code.length > 30000) return
    try {
        const fragment = node.ownerDocument.createDocumentFragment()
        let position = 0
        highlightTree(grammar.parser.parse(code), codeHighlighter, (from, to, classes) => {
            if (from > position) fragment.append(node.ownerDocument.createTextNode(code.slice(position, from)))
            const token = node.ownerDocument.createElement("span")
            token.className = classes
            token.textContent = code.slice(from, to)
            fragment.append(token)
            position = to
        })
        if (position < code.length) fragment.append(node.ownerDocument.createTextNode(code.slice(position)))
        node.replaceChildren(fragment)
    } catch {
        node.textContent = code
    }
}
