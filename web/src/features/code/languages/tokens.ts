/**
 * FilePath    : pbi-utils\web\src\features\code\languages\tokens.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供三种代码语言共用的词元标签和容错字符串扫描.
 */
import type { StringStream } from "@codemirror/language"
import { Tag, tags } from "@lezer/highlight"

/** 独立语言主题标签随词元进入 Markdown 围栏, TMDL 继续使用未附加主题的标准标签. */
export const daxThemeTag = Tag.define()
export const powerQueryThemeTag = Tag.define()

/** 词法分类映射到标准 Lezer 标签, 供编辑器和静态代码预览共用. */
export const codeTokenTable = {
    keyword: tags.keyword,
    literal: tags.bool,
    null: tags.null,
    number: tags.number,
    string: tags.string,
    comment: tags.comment,
    identifier: tags.variableName,
    name: tags.special(tags.variableName),
    function: tags.function(tags.variableName),
    type: tags.typeName,
    operator: tags.operator,
    punctuation: tags.punctuation,
    property: tags.propertyName,
    escape: tags.escape,
}

/**
 * 为语言词元附加纯样式身份, 不更改原有分类或解析行为.
 * @param theme 当前语言的主题标签.
 * @returns 各词元的标准标签与主题标签, 供嵌入解析器复用同一配色.
 */
export function themedTokenTable(theme: Tag): Record<string, readonly Tag[]> {
    return Object.fromEntries(Object.entries(codeTokenTable).map(([name, tag]) => [name, [tag, theme]]))
}

/** 记录跨行文本中的结束符, 空值表示当前处于普通代码. */
export interface LexicalState {
    quote: '"' | "'" | "]" | null
    quoteToken: "string" | "name"
    blockComment: boolean
}

/** 创建独立词法状态, 避免不同文档或嵌入块共享着色状态. */
export function createLexicalState(): LexicalState {
    return { quote: null, quoteToken: "string", blockComment: false }
}

/**
 * 扫描到结束符或行尾; 连写结束符视为转义, 未完成输入保留状态供下一行继续.
 * @param stream 当前行的输入流.
 * @param state 当前文本或名称的词法状态.
 * @returns 当前文本或名称的标签; 不验证或执行源代码.
 */
export function consumeQuoted(stream: StringStream, state: LexicalState): "string" | "name" {
    const token = state.quoteToken
    while (!stream.eol()) {
        if (stream.next() !== state.quote) continue
        if (stream.peek() === state.quote) stream.next()
        else {
            state.quote = null
            break
        }
    }
    return token
}

/**
 * 扫描非嵌套块注释, 行尾未结束时保留状态.
 * @param stream 当前行的输入流.
 * @param state 当前注释的词法状态.
 * @returns 注释标签; 不会处理注释中的代码.
 */
export function consumeBlockComment(stream: StringStream, state: LexicalState): "comment" {
    while (!stream.eol()) {
        if (stream.next() === "*" && stream.eat("/")) {
            state.blockComment = false
            break
        }
    }
    return "comment"
}

/** Unicode 名称匹配遵循 M 的字母, 数字, 组合, 连接和格式字符类别. */
export const identifierPattern = /^[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]*(?:\.[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]*)*/u

/**
 * 判断当前名称之后是否出现调用括号; 只查看当前行, 不执行名称解析.
 * @param stream 已消费名称的输入流.
 * @returns 当前名称是否处于可见的函数调用位置.
 */
export function followsCall(stream: StringStream): boolean {
    return /^\s*\(/.test(stream.string.slice(stream.pos))
}
