/**
 * FilePath    : pbi-utils\web\src\features\code\languages\tmdl.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为 TMDL 缩进属性和内嵌 DAX, M, 原始表达式提供本地词法高亮.
 */
import { LanguageSupport, StreamLanguage, type StreamParser, type StringStream } from "@codemirror/language"
import { daxToken } from "./dax"
import { powerQueryToken } from "./powerQuery"
import { codeTokenTable, consumeQuoted, createLexicalState, type LexicalState } from "./tokens"

type ExpressionLanguage = "dax" | "m" | "raw"

/** 保留当前父对象的缩进和分区源语言, 不构造 TOM 对象或验证属性. */
interface ObjectContext {
    indent: number
    type: string
    sourceLanguage: ExpressionLanguage
}

/** 每个表达式块独立持有词法状态, 在缩进或反引号边界释放. */
interface EmbeddedExpression {
    language: ExpressionLanguage
    minimumIndent: number
    fenced: boolean
    lexical: LexicalState
}

/** 当前行的结构信息, 用于区分对象名称, 属性名称和表达式赋值. */
interface LineStructure {
    headFrom: number
    headTo: number
    headToken: "keyword" | "property"
    equalsAt: number
    language: ExpressionLanguage | null
    minimumIndent: number
}

/** TMDL 词法状态只跟踪可见结构, 不承担完整语义或缩进校验. */
interface TmdlState {
    contexts: ObjectContext[]
    expression: EmbeddedExpression | null
    structure: LineStructure | null
    lineExpression: boolean
    closingFence: boolean
    indentUnit: number
    lexical: LexicalState
}

const objectTypes = new Set(
    "database model table column measure function partition expression namedexpression relationship hierarchy level role tablepermission columnpermission perspective perspectivetable perspectivecolumn perspectivemeasure perspectivehierarchy culture translation datasource annotation extendedproperty jsonextendedproperty stringextendedproperty calculationgroup calculationitem calculationexpression calculationgroupexpression formatstringdefinition detailrowsdefinition datacoveragedefinition refreshpolicy basicrefreshpolicy kpi variation alternateof changedproperty querygroup linguisticmetadata externalmodelrolemember".split(
        " ",
    ),
)
const daxDefaults = new Set(
    "measure function column calculationitem calculationexpression calculationgroupexpression formatstringdefinition detailrowsdefinition datacoveragedefinition tablepermission".split(
        " ",
    ),
)
const daxProperties = new Set("statusexpression targetexpression trendexpression filterexpression".split(" "))
const mProperties = new Set(["sourceexpression", "pollingexpression"])

/**
 * 创建当前文档的 TMDL 状态.
 * @returns 独立缩进上下文及空表达式状态; 不读取文件或连接模型.
 */
function createTmdlState(): TmdlState {
    return { contexts: [], expression: null, structure: null, lineExpression: false, closingFence: false, indentUnit: 4, lexical: createLexicalState() }
}

/**
 * 深拷贝流解析状态, 使增量解析检查点不共享可变对象.
 * @param state 上一个解析检查点.
 * @returns 具有独立上下文, 表达式和字符串状态的副本.
 */
function copyTmdlState(state: TmdlState): TmdlState {
    return {
        ...state,
        contexts: state.contexts.map((context) => ({ ...context })),
        expression: state.expression ? { ...state.expression, lexical: { ...state.expression.lexical } } : null,
        structure: state.structure ? { ...state.structure } : null,
        lexical: { ...state.lexical },
    }
}

/**
 * 按对象或属性选择表达式语言; 未知或原始内容保持文本标签.
 * @param name 当前对象或属性的小写名称.
 * @param object 当前行是否为对象声明.
 * @param parent 可见的最近父对象.
 * @returns DAX, M 或原始文本语言; 分区源类型声明返回空值.
 */
function expressionLanguage(name: string, object: boolean, parent?: ObjectContext): ExpressionLanguage | null {
    if (object) {
        if (name === "partition" || name === "datasource" || name === "columnpermission" || name === "changedproperty") return null
        if (daxDefaults.has(name)) return "dax"
        if (name === "expression" || name === "namedexpression") return "m"
        return "raw"
    }
    if (mProperties.has(name)) return "m"
    if (daxProperties.has(name)) return "dax"
    if (name === "source") return parent?.type === "partition" ? parent.sourceLanguage : "raw"
    if (name === "expression") {
        if (parent?.type === "expression" || parent?.type === "namedexpression") return "m"
        if (parent?.type === "partition") return parent.sourceLanguage
        return "dax"
    }
    return "raw"
}

/**
 * 根据可见缩进准备当前行; 反引号结束和缩进回退均先释放内嵌词法状态.
 * @param stream 位于行首的输入流.
 * @param state 当前文档状态.
 * @returns 无返回值; 更新当前行的词法分发信息.
 */
function prepareLine(stream: StringStream, state: TmdlState): void {
    const line = stream.string
    const indent = stream.indentation()
    state.lineExpression = false
    state.closingFence = false
    state.structure = null
    state.lexical = createLexicalState()
    if (state.expression) {
        if (state.expression.fenced && /^\s*```\s*$/.test(line)) {
            state.expression = null
            state.closingFence = true
            return
        }
        if (state.expression.fenced || !line.trim() || indent >= state.expression.minimumIndent) {
            state.lineExpression = true
            return
        }
        state.expression = null
    }
    const head = /^(\s*)([\p{L}_][\p{L}\p{N}_]*)/u.exec(line)
    if (!head || line.slice(head[1].length).startsWith("///")) return
    const name = head[2].toLowerCase()
    const object = objectTypes.has(name) && !/^\s*[=:]/.test(line.slice(head[0].length))
    const equalsAt = findAssignment(line, head[0].length)
    let parent = state.contexts.findLast((context) => context.indent < indent)
    if (object) {
        state.contexts = state.contexts.filter((context) => context.indent < indent)
        parent = state.contexts.at(-1)
        if (parent && indent > parent.indent) state.indentUnit = indent - parent.indent
        const partitionType =
            equalsAt >= 0
                ? line
                      .slice(equalsAt + 1)
                      .trim()
                      .toLowerCase()
                : undefined
        state.contexts.push({ indent, type: name, sourceLanguage: partitionType === "m" ? "m" : partitionType === "calculated" ? "dax" : "raw" })
    }
    state.structure = {
        headFrom: head[1].length,
        headTo: head[0].length,
        headToken: object || name === "ref" ? "keyword" : "property",
        equalsAt,
        language: equalsAt >= 0 ? expressionLanguage(name, object, parent) : null,
        minimumIndent: indent + state.indentUnit * (object ? 2 : 1),
    }
}

/**
 * 查找名称之后的赋值符, 跳过单引号名称及双引号文本中的转义符.
 * @param line 当前行原文.
 * @param from 已消费的对象或属性名称末尾.
 * @returns 未被引号包裹的第一个等号位置, 未找到时返回 -1.
 */
function findAssignment(line: string, from: number): number {
    let quote: string | null = null
    for (let index = from; index < line.length; index++) {
        const character = line[index]
        if (quote) {
            if (character === quote) {
                if (line[index + 1] === quote) index++
                else quote = null
            }
        } else if (character === "'" || character === '"') quote = character
        else if (character === "=") return index
        else if (character === ":") return -1
    }
    return -1
}

/**
 * 分发当前表达式词元; 原始块仅标记文本, 不猜测 SQL, JSON 等语言.
 * @param stream 当前行的输入流.
 * @param expression 当前独立表达式状态.
 * @returns 内嵌语言词元或原始文本标签.
 */
function embeddedToken(stream: StringStream, expression: EmbeddedExpression): string | null {
    if (expression.language === "dax") return daxToken(stream, expression.lexical)
    if (expression.language === "m") return powerQueryToken(stream, expression.lexical)
    stream.skipToEnd()
    return "string"
}

/**
 * 分类 TMDL 对象, 属性及表达式, 容忍编辑中缺失引号或结束反引号.
 * @param stream 当前行的输入流.
 * @param state 当前文档的缩进和表达式状态.
 * @returns 标准词元标签, 空白和未识别字符返回空值.
 */
function tmdlToken(stream: StringStream, state: TmdlState): string | null {
    if (stream.sol()) prepareLine(stream, state)
    if (state.closingFence) {
        if (stream.eatSpace()) return null
        stream.skipToEnd()
        return "punctuation"
    }
    if (state.lineExpression && state.expression) {
        if (!state.expression.fenced && /^\s*```\s*$/.test(stream.string.slice(stream.pos))) {
            if (stream.eatSpace()) return null
            stream.skipToEnd()
            state.expression.fenced = true
            return "punctuation"
        }
        return embeddedToken(stream, state.expression)
    }
    if (stream.eatSpace()) return null
    if (stream.match("///") || stream.match("//")) {
        stream.skipToEnd()
        return "comment"
    }
    if (state.structure && stream.pos === state.structure.headFrom) {
        stream.pos = state.structure.headTo
        return state.structure.headToken
    }
    if (state.structure && stream.pos === state.structure.equalsAt) {
        stream.next()
        if (state.structure.language) {
            state.expression = {
                language: state.structure.language,
                minimumIndent: state.structure.minimumIndent,
                fenced: false,
                lexical: createLexicalState(),
            }
            state.lineExpression = true
        }
        return "operator"
    }
    if (stream.eat("'")) {
        state.lexical.quote = "'"
        state.lexical.quoteToken = "name"
        return consumeQuoted(stream, state.lexical)
    }
    if (stream.eat('"')) {
        state.lexical.quote = '"'
        state.lexical.quoteToken = "string"
        return consumeQuoted(stream, state.lexical)
    }
    if (stream.match(/^[+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?/)) {
        if (stream.match(/^[\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}-]+/u)) return "name"
        return "number"
    }
    const word = stream.match(/^[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{N}\p{M}\p{Pc}\p{Cf}-]*/u)
    if (Array.isArray(word)) {
        const name = word[0].toLowerCase()
        if (name === "true" || name === "false") return "literal"
        if (state.structure?.headToken === "keyword" && /^\s*ref\s+$/i.test(stream.string.slice(0, stream.start)) && objectTypes.has(name)) return "keyword"
        return "name"
    }
    if (stream.match(/^[=:]/)) return "operator"
    if (stream.match(/^[.(),[\]{}]/)) return "punctuation"
    stream.next()
    return null
}

/** TMDL 流解析器显式复制嵌入状态, 便于编辑时进行安全的增量重解析. */
export const tmdlParser: StreamParser<TmdlState> = {
    name: "tmdl",
    startState: createTmdlState,
    copyState: copyTmdlState,
    token: tmdlToken,
    tokenTable: codeTokenTable,
    languageData: { commentTokens: { line: "///" } },
}

/** 可供 CodeMirror 编辑器和静态代码预览共用的 TMDL 语言. */
export const tmdlLanguage = StreamLanguage.define(tmdlParser)

/**
 * 创建 TMDL 编辑语言支持.
 * @returns 纯本地的 CodeMirror 扩展, 不连接模型或格式化内容.
 */
export function tmdl(): LanguageSupport {
    return new LanguageSupport(tmdlLanguage)
}
