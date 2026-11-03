/**
 * FilePath    : pbi-utils\web\src\features\markdown\markdown.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : AI 回复与 UDF 文档共用的 Markdown 解析和白名单净化, 禁止原始 HTML 和媒体资源.
 */
import { Marked, TextRenderer } from "marked"
import DOMPurify from "dompurify"

/** 转义原文与属性, 原始 HTML 只能显示为文字, 不能成为活动节点. */
function escapeHtml(value: string): string {
    return value.replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]!)
}

/** 仅允许明确 HTTP(S) 或文档锚点; 相对路径、凭据和其他协议均保留为普通文字. */
function safeLink(value: string): boolean {
    if (/^#[\p{L}\p{N}_.%~-]+$/u.test(value)) return true
    if (!/^https?:\/\//i.test(value)) return false
    try {
        const url = new URL(value)
        return !url.username && !url.password && (url.protocol === "https:" || url.protocol === "http:")
    } catch {
        return false
    }
}

/** 为一次文档渲染创建独立解析器和标题身份集合, 不让其他文档或消息的重复标题影响本次锚点.
 * @param headingPrefix 调用方的标题锚点前缀, 输出时按属性文本转义.
 * @returns 禁用原始 HTML/媒体的同步解析器; 标题锚点只在当前文档内使用.
 */
function createMarkdown(headingPrefix: string): Marked {
    const headings = new Set<string>()
    const textRenderer = new TextRenderer()
    return new Marked({
        gfm: true,
        breaks: false,
        async: false,
        renderer: {
            heading({ tokens, depth }) {
                const text = this.parser.parseInline(tokens, textRenderer)
                const base =
                    text
                        .toLowerCase()
                        .trim()
                        .replace(/[^\p{L}\p{N}_ -]/gu, "")
                        .replace(/ /g, "-") || "section"
                let slug = base
                let suffix = 0
                while (headings.has(slug)) slug = `${base}-${++suffix}`
                headings.add(slug)
                return `<h${depth} id="${escapeHtml(headingPrefix + slug)}">${this.parser.parseInline(tokens)}</h${depth}>\n`
            },
            html({ text }) {
                return escapeHtml(text)
            },
            image({ text }) {
                return escapeHtml(text)
            },
            checkbox({ checked }) {
                return checked ? "[x] " : "[ ] "
            },
            link({ href, title, tokens }) {
                const text = this.parser.parseInline(tokens)
                if (!safeLink(href)) return text
                return `<a href="${escapeHtml(href)}"${title ? ` title="${escapeHtml(title)}"` : ""} rel="noopener noreferrer">${text}</a>`
            },
        },
    })
}

/** 将文档解析为可显示 HTML, 再经显式标签/属性白名单净化; 不请求资源或改变模型.
 * @param source AI 回复、README 或函数说明原文, 可以是尚未闭合的流式 Markdown 片段.
 * @param headingPrefix 标题锚点前缀, 供当前显示组件隔离局部标题查找.
 * @returns 仅含文本排版语义的净化 HTML, 原始标签及媒体不会激活.
 */
export function renderMarkdown(source: string, headingPrefix = "markdown-heading-"): string {
    const html = createMarkdown(headingPrefix).parse(source, { async: false })
    return DOMPurify.sanitize(html, {
        ALLOWED_TAGS: [
            "p",
            "br",
            "hr",
            "h1",
            "h2",
            "h3",
            "h4",
            "h5",
            "h6",
            "strong",
            "em",
            "del",
            "blockquote",
            "ul",
            "ol",
            "li",
            "pre",
            "code",
            "table",
            "thead",
            "tbody",
            "tr",
            "th",
            "td",
            "a",
        ],
        ALLOWED_ATTR: ["href", "title", "rel", "class", "start", "align", "id"],
        ALLOW_DATA_ATTR: false,
        ALLOW_ARIA_ATTR: false,
    })
}
