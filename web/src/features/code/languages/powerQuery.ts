/**
 * FilePath    : pbi-utils\web\src\features\code\languages\powerQuery.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为大小写敏感的 Power Query M 提供本地 CodeMirror 词法扩展.
 */
import { LanguageSupport, StreamLanguage, type StreamParser, type StringStream } from "@codemirror/language"
import {
    consumeBlockComment,
    consumeQuoted,
    createLexicalState,
    followsCall,
    identifierPattern,
    powerQueryThemeTag,
    themedTokenTable,
    type LexicalState,
} from "./tokens"

const keywords = new Set(
    "and as each else error if in is let meta not or otherwise section shared then try type #binary #date #datetime #datetimezone #duration #sections #shared #table #time".split(
        " ",
    ),
)
const literals = new Set(["true", "false", "#infinity", "#nan"])
const types = new Set("any anynonnull binary date datetime datetimezone duration function list logical none number record table text time nullable".split(" "))

/**
 * 分类 M 词元, 保留名称大小写和 Unicode; 注释不嵌套, 文本支持双引号转义.
 * @param stream 当前行的输入流.
 * @param state 跨行字符串或注释状态.
 * @returns 本地词元标签, 不验证 M 语义或访问数据源.
 */
export function powerQueryToken(stream: StringStream, state: LexicalState): string | null {
    if (state.blockComment) return consumeBlockComment(stream, state)
    if (state.quote) return consumeQuoted(stream, state)
    if (stream.eatSpace()) return null
    if (stream.match("//")) {
        stream.skipToEnd()
        return "comment"
    }
    if (stream.match("/*")) {
        state.blockComment = true
        return consumeBlockComment(stream, state)
    }
    if (stream.match('#"')) {
        state.quote = '"'
        state.quoteToken = "name"
        return consumeQuoted(stream, state)
    }
    if (stream.match(/^(?:#!)?"/)) {
        state.quote = '"'
        state.quoteToken = "string"
        return consumeQuoted(stream, state)
    }
    if (stream.match(/^(?:0[xX][\da-fA-F]+|(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?)/)) {
        if (stream.match(/^[\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]+/u)) return "identifier"
        return "number"
    }
    const intrinsic = stream.match(/^#[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]*/u)
    if (Array.isArray(intrinsic)) return keywords.has(intrinsic[0]) ? "keyword" : literals.has(intrinsic[0]) ? "literal" : "identifier"
    const word = stream.match(identifierPattern)
    if (Array.isArray(word)) {
        const name = word[0]
        if (keywords.has(name)) return "keyword"
        if (name === "null") return "null"
        if (literals.has(name)) return "literal"
        if (types.has(name)) return "type"
        return followsCall(stream) ? "function" : "identifier"
    }
    if (stream.match(/^(?:=>|\.\.\.|\.\.|\?\?|<=|>=|<>|[-+*/&=<>@!?])/)) return "operator"
    if (stream.match(/^[()[\]{},;:.]/)) return "punctuation"
    stream.next()
    return null
}

/** M 词法流定义, 不引用远程函数目录或运行时解析器. */
export const powerQueryParser: StreamParser<LexicalState> = {
    name: "m",
    startState: createLexicalState,
    token: powerQueryToken,
    tokenTable: themedTokenTable(powerQueryThemeTag),
    languageData: { commentTokens: { line: "//", block: { open: "/*", close: "*/" } } },
}

/** 可供编辑和静态预览共用的 Power Query M 语言. */
export const powerQueryLanguage = StreamLanguage.define(powerQueryParser)

/**
 * 创建 Power Query M 编辑语言支持.
 * @returns 纯本地的 CodeMirror 语言扩展, 不包含格式化器或执行器.
 */
export function powerQuery(): LanguageSupport {
    return new LanguageSupport(powerQueryLanguage)
}
