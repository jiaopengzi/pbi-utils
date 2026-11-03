/**
 * FilePath    : pbi-utils\web\src\features\code\codeTheme.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将编辑和静态预览的语法标签映射到同一套本地主题样式.
 */
import { tags, tagHighlighter } from "@lezer/highlight"
import { daxThemeTag, powerQueryThemeTag } from "./languages/tokens"
import "./editor.scss"

/** 使用固定类名共享着色, 不把输入内容作为 HTML 或动态样式执行. */
export const codeHighlighter = tagHighlighter([
    { tag: daxThemeTag, class: "pbi-code-dax" },
    { tag: powerQueryThemeTag, class: "pbi-code-m" },
    { tag: tags.keyword, class: "pbi-code-keyword" },
    { tag: tags.comment, class: "pbi-code-comment" },
    { tag: tags.string, class: "pbi-code-string" },
    { tag: tags.number, class: "pbi-code-number" },
    { tag: tags.bool, class: "pbi-code-literal" },
    { tag: tags.null, class: "pbi-code-null" },
    { tag: tags.variableName, class: "pbi-code-identifier" },
    { tag: tags.typeName, class: "pbi-code-type" },
    { tag: [tags.function(tags.variableName), tags.standard(tags.variableName)], class: "pbi-code-function" },
    { tag: [tags.propertyName, tags.special(tags.variableName)], class: "pbi-code-name" },
    { tag: tags.operator, class: "pbi-code-operator" },
    { tag: tags.punctuation, class: "pbi-code-punctuation" },
    { tag: tags.heading, class: "pbi-code-heading" },
    { tag: tags.strong, class: "pbi-code-strong" },
    { tag: tags.emphasis, class: "pbi-code-emphasis" },
    { tag: [tags.link, tags.url], class: "pbi-code-link" },
    { tag: tags.monospace, class: "pbi-code-monospace" },
    { tag: tags.quote, class: "pbi-code-quote" },
    { tag: tags.list, class: "pbi-code-list" },
    { tag: tags.contentSeparator, class: "pbi-code-separator" },
])
