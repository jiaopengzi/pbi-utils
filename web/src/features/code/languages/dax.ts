/**
 * FilePath    : pbi-utils\web\src\features\code\languages\dax.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为 DAX 提供容忍未完成输入的 CodeMirror 本地词法语言扩展.
 */
import { LanguageSupport, StreamLanguage, type StreamParser, type StringStream } from "@codemirror/language"
import {
    consumeBlockComment,
    consumeQuoted,
    createLexicalState,
    daxThemeTag,
    followsCall,
    identifierPattern,
    themedTokenTable,
    type LexicalState,
} from "./tokens"

const keywords = new Set("VAR RETURN DEFINE EVALUATE MEASURE COLUMN TABLE FUNCTION ORDER BY START AT ASC DESC IN NOT AS".split(" "))
const types = new Set("BOOLEAN CURRENCY DATETIME DOUBLE INTEGER INT64 NUMERIC SCALAR STRING VARIANT EXPR VAL".split(" "))

/**
 * 分类 DAX 注释, 文本, 转义名称及调用位置; 不进行语义校验或函数目录判断.
 * @param stream 当前行的输入流.
 * @param state 跨行字符串或注释状态.
 * @returns 词元标签, 空白返回空值.
 */
export function daxToken(stream: StringStream, state: LexicalState): string | null {
    if (state.blockComment) return consumeBlockComment(stream, state)
    if (state.quote) return consumeQuoted(stream, state)
    if (stream.eatSpace()) return null
    if (stream.match(/^(?:\/\/|--)/)) {
        stream.skipToEnd()
        return "comment"
    }
    if (stream.match("/*")) {
        state.blockComment = true
        return consumeBlockComment(stream, state)
    }
    if (stream.match(/^(?:DT)?"/i)) {
        state.quote = '"'
        state.quoteToken = "string"
        return consumeQuoted(stream, state)
    }
    if (stream.eat("'")) {
        state.quote = "'"
        state.quoteToken = "name"
        return consumeQuoted(stream, state)
    }
    if (stream.eat("[")) {
        state.quote = "]"
        state.quoteToken = "name"
        return consumeQuoted(stream, state)
    }
    if (stream.match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?/)) {
        if (stream.match(/^[\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]+/u)) return "identifier"
        return "number"
    }
    const word = stream.match(identifierPattern)
    if (Array.isArray(word)) {
        const name = word[0].toUpperCase()
        if (keywords.has(name)) return "keyword"
        if (types.has(name)) return "type"
        if (name === "TRUE" || name === "FALSE") return followsCall(stream) ? "function" : "literal"
        return followsCall(stream) ? "function" : "identifier"
    }
    if (stream.match(/^(?:&&|\|\||<=|>=|<>|==|:=|=>|[-+*/^&=<>])/)) return "operator"
    if (stream.match(/^[(){},;:.]/)) return "punctuation"
    stream.next()
    return null
}

/** DAX 词法流定义, 使用标准标签且不将用户代码传给外部工具. */
export const daxParser: StreamParser<LexicalState> = {
    name: "dax",
    startState: createLexicalState,
    token: daxToken,
    tokenTable: themedTokenTable(daxThemeTag),
    languageData: { commentTokens: { line: "//", block: { open: "/*", close: "*/" } } },
}

/** 可直接用于 CodeMirror 配置和静态预览解析的 DAX 语言. */
export const daxLanguage = StreamLanguage.define(daxParser)

/**
 * 创建 DAX 编辑语言支持.
 * @returns 纯本地的 CodeMirror 语言扩展, 不包含格式化器或执行器.
 */
export function dax(): LanguageSupport {
    return new LanguageSupport(daxLanguage)
}
