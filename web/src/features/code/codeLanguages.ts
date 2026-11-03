/**
 * FilePath    : pbi-utils\web\src\features\code\codeLanguages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 统一 DAX, Power Query M, TMDL 与 Markdown 的本地编辑及预览语言选择.
 */
import type { Extension } from "@codemirror/state"
import type { Language } from "@codemirror/language"
import { markdown, markdownLanguage } from "@codemirror/lang-markdown"
import { dax, daxLanguage } from "./languages/dax"
import { powerQuery, powerQueryLanguage } from "./languages/powerQuery"
import { tmdl, tmdlLanguage } from "./languages/tmdl"

export { dax, daxLanguage, powerQuery, powerQueryLanguage, tmdl, tmdlLanguage }

/** 产品支持的本地编辑语言, 其他标签按原始文本显示. */
export type CodeLanguage = "dax" | "m" | "tmdl" | "markdown" | "plaintext"

/** Markdown 与详情采用 GFM, 围栏只复用本地代码语言; 禁止自动补 HTML 和粘贴改写原文. */
const markdownSupport = markdown({
    base: markdownLanguage,
    codeLanguages(info) {
        const language = normalizeLanguage(info.trim().split(/\s+/)[0])
        return language === "markdown" ? null : getCodeLanguage(language)
    },
    completeHTMLTags: false,
    pasteURLAsLink: false,
})

/**
 * 将代码语言标签规范为白名单值.
 * @param info 代码块或编辑器的语言名称, 省略时使用原始文本.
 * @returns 白名单语言标识; 未知值不执行自动语言推断.
 */
export function normalizeLanguage(info?: string): CodeLanguage {
    const value = info?.trim().toLowerCase() ?? ""
    if (value === "dax") return "dax"
    if (["m", "pq", "powerquery", "power query", "power-query"].includes(value)) return "m"
    if (value === "tmdl") return "tmdl"
    if (value === "markdown" || value === "md") return "markdown"
    return "plaintext"
}

/**
 * 获取编辑器使用的本地语言扩展.
 * @param info 编辑器的语言名称或别名.
 * @returns CodeMirror 语言支持; 原始文本返回空扩展.
 */
export function codeLanguage(info?: string): Extension {
    const language = normalizeLanguage(info)
    if (language === "dax") return dax()
    if (language === "m") return powerQuery()
    if (language === "tmdl") return tmdl()
    if (language === "markdown") return markdownSupport
    return []
}

/**
 * 获取静态预览使用的相同词法解析器.
 * @param info 代码块的语言名称或别名.
 * @returns 对应本地语言, 原始文本返回空值.
 */
export function getCodeLanguage(info?: string): Language | null {
    const language = normalizeLanguage(info)
    if (language === "dax") return daxLanguage
    if (language === "m") return powerQueryLanguage
    if (language === "tmdl") return tmdlLanguage
    if (language === "markdown") return markdownSupport.language
    return null
}
