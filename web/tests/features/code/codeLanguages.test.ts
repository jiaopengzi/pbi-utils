/**
 * FilePath    : pbi-utils\web\tests\features\code\codeLanguages.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 通过真实语法树高亮验证三种本地语言的分类, 增量编辑和状态边界.
 */
import { describe, expect, it } from "vitest"
import { EditorState } from "@codemirror/state"
import { HighlightStyle, LanguageSupport, syntaxTree } from "@codemirror/language"
import { highlightTree, tags } from "@lezer/highlight"
import { codeLanguage, getCodeLanguage, normalizeLanguage } from "@/features/code/codeLanguages"

const tokenStyle = HighlightStyle.define([
    { tag: tags.keyword, class: "keyword" },
    { tag: [tags.bool, tags.null], class: "literal" },
    { tag: tags.number, class: "number" },
    { tag: tags.string, class: "string" },
    { tag: tags.comment, class: "comment" },
    { tag: tags.variableName, class: "identifier" },
    { tag: tags.special(tags.variableName), class: "name" },
    { tag: tags.function(tags.variableName), class: "function" },
    { tag: tags.typeName, class: "type" },
    { tag: tags.operator, class: "operator" },
    { tag: tags.punctuation, class: "punctuation" },
    { tag: tags.propertyName, class: "property" },
])

interface HighlightedToken {
    from: number
    to: number
    type: string
}

/**
 * 使用正式语言解析器和 Lezer 高亮树收集分类.
 * @param code 不会被执行的本地合成测试文本.
 * @param language 白名单语言名称.
 * @returns 真实语法树对应的高亮范围.
 */
function tokens(code: string, language: string): HighlightedToken[] {
    const parser = getCodeLanguage(language)?.parser
    if (!parser) return []
    const result: HighlightedToken[] = []
    highlightTree(parser.parse(code), tokenStyle, (from, to, type) => result.push({ from, to, type }))
    return result
}

/**
 * 查找指定文本起点实际落入的高亮类别.
 * @param code 合成源码.
 * @param language 白名单语言名称.
 * @param needle 需要验证的词元文本.
 * @param occurrence 同名文本的出现序号, 从零开始.
 * @returns 高亮类别; 文本不存在或未着色时返回空值.
 */
function classification(code: string, language: string, needle: string, occurrence = 0): string | undefined {
    let position = -1
    for (let index = 0; index <= occurrence; index++) position = code.indexOf(needle, position + 1)
    return tokens(code, language).find((token) => token.from <= position && position < token.to)?.type
}

describe("代码语言选择", () => {
    it("限定白名单及 M 别名, 未知值使用原始文本", () => {
        expect(["DAX", "m", "pq", "powerquery", "Power Query", "power-query", "TMDL", "html", "", undefined].map(normalizeLanguage)).toEqual([
            "dax",
            "m",
            "m",
            "m",
            "m",
            "m",
            "tmdl",
            "plaintext",
            "plaintext",
            "plaintext",
        ])
        expect(codeLanguage("dax")).toBeInstanceOf(LanguageSupport)
        expect(codeLanguage("pq")).toBeInstanceOf(LanguageSupport)
        expect(codeLanguage("tmdl")).toBeInstanceOf(LanguageSupport)
        expect(codeLanguage("javascript")).toEqual([])
        expect(getCodeLanguage("html")).toBeNull()
    })
})

describe("DAX 词法扩展", () => {
    it("识别查询, 类型, 转义表列, 日期文本和用户函数调用", () => {
        const code = "define FUNCTION 本地.函数 = (x: SCALAR) => x\nVAR 返回值 = 本地.函数(1.25e-2)\nRETURN SUM('销''售'[金]]额]) & DT\"2026-10-13\""
        expect(classification(code, "dax", "define")).toBe("keyword")
        expect(classification(code, "dax", "SCALAR")).toBe("type")
        expect(classification(code, "dax", "本地.函数", 0)).toBe("identifier")
        expect(classification(code, "dax", "本地.函数", 1)).toBe("function")
        expect(classification(code, "dax", "1.25e-2")).toBe("number")
        expect(classification(code, "dax", "'销''售'")).toBe("name")
        expect(classification(code, "dax", "[金]]额]")).toBe("name")
        expect(classification(code, "dax", 'DT"2026-10-13"')).toBe("string")
    })

    it("文本和注释跨行后恢复代码, 字面量中的注释符和 HTML 保持数据", () => {
        const code = '"第一行 // <img>\n第二行 ""引号"""\n/* VAR x =\nRETURN */ RETURN 1 -- SUM(x)\nSUM(2)'
        expect(classification(code, "dax", "<img>")).toBe("string")
        expect(classification(code, "dax", "第二行")).toBe("string")
        expect(classification(code, "dax", "VAR")).toBe("comment")
        expect(classification(code, "dax", "RETURN", 0)).toBe("comment")
        expect(classification(code, "dax", "RETURN", 1)).toBe("keyword")
        expect(classification(code, "dax", "SUM", 0)).toBe("comment")
        expect(classification(code, "dax", "SUM", 1)).toBe("function")
    })

    it("数字不拆分相邻 Unicode 名称, 保留合法数字及名称后缀", () => {
        const code = "VAR 值1 = 1.25 + 1名称 + 名称2 RETURN 值1"
        expect(classification(code, "dax", "1.25")).toBe("number")
        expect(classification(code, "dax", "1名称")).toBe("identifier")
        expect(classification(code, "dax", "名称2")).toBe("identifier")
    })

    it("未完成名称和文本仍可解析, 不会丢失源文范围", () => {
        for (const code of ["SUM('销售", "[金额]]", 'VAR x = "未结束', "/* 注释未结束\nRETURN"]) {
            const tree = getCodeLanguage("dax")!.parser.parse(code)
            expect(tree.length).toBe(code.length)
            expect(tokens(code, "dax").every((token) => token.from >= 0 && token.to <= code.length)).toBe(true)
        }
    })
})

describe("Power Query M 词法扩展", () => {
    it("关键字大小写敏感, 支持 Unicode, #名称, 引号名称和调用位置", () => {
        const code = 'let Let = #date(2026, 10, 13), #"步""骤#(lf)" = Table.AddColumn(数据, "列", each true), 变量́ = 0xFF in #"步""骤#(lf)" + LET + #Date(1)'
        expect(classification(code, "m", "let")).toBe("keyword")
        expect(classification(code, "m", "Let")).toBe("identifier")
        expect(classification(code, "m", "LET")).toBe("identifier")
        expect(classification(code, "m", "#date")).toBe("keyword")
        expect(classification(code, "m", "#Date")).toBe("identifier")
        expect(classification(code, "m", '#"步""骤#(lf)"')).toBe("name")
        expect(classification(code, "m", "Table.AddColumn")).toBe("function")
        expect(classification(code, "m", "变量́")).toBe("identifier")
        expect(classification(code, "m", "0xFF")).toBe("number")
        expect(classification(code, "m", "each")).toBe("keyword")
        expect(classification(code, "m", "true")).toBe("literal")
    })

    it("块注释不嵌套, 双引号字符串和引号名称不处理注释标记", () => {
        const code = '/* outer /* inner */ in\nlet x = "// #(cr,lf) ""quoted""\nend", #"/* step\nname */" = #!"invalid code" in x'
        expect(classification(code, "m", "inner")).toBe("comment")
        expect(classification(code, "m", "in", 1)).toBe("keyword")
        expect(classification(code, "m", "#(cr,lf)")).toBe("string")
        expect(classification(code, "m", "end")).toBe("string")
        expect(classification(code, "m", "name */")).toBe("name")
        expect(classification(code, "m", '#!"invalid code"')).toBe("string")
    })

    it("带数字的 Unicode 标识符不会被拆成数字及名称", () => {
        const code = "let 值1 = 1.5, 数字 = 1字段 in 值1"
        expect(classification(code, "m", "1.5")).toBe("number")
        expect(classification(code, "m", "1字段")).toBe("identifier")
        expect(classification(code, "m", "值1")).toBe("identifier")
    })

    it("跨行注释结束后恢复代码, 不把单行注释内容作为新状态", () => {
        const code = "/* let\nin */ let x = 1 // /* comment\nin x"
        expect(classification(code, "m", "let", 0)).toBe("comment")
        expect(classification(code, "m", "let", 1)).toBe("keyword")
        expect(classification(code, "m", "in", 1)).toBe("keyword")
    })
})

describe("TMDL 结构及嵌入语言", () => {
    it("识别缩进属性及默认 DAX 与 M 表达式, 赋值名称中的等号不干扰", () => {
        const code = [
            "table 'Sales'' report'",
            "    measure 'A = B' =",
            "            VAR x = 1",
            "            RETURN SUM('Sales'[Amount])",
            '        formatString: "#,##0"',
            "    partition 'P' = m",
            "        source =",
            "            let",
            '                #"步骤" = #date(2026, 10, 13)',
            '            in #"步骤"',
            "        mode: import",
            "expression Query = let x = 1 in x",
        ].join("\n")
        expect(classification(code, "tmdl", "table")).toBe("keyword")
        expect(classification(code, "tmdl", "'Sales'' report'")).toBe("name")
        expect(classification(code, "tmdl", "'A = B'")).toBe("name")
        expect(classification(code, "tmdl", "VAR")).toBe("keyword")
        expect(classification(code, "tmdl", "SUM")).toBe("function")
        expect(classification(code, "tmdl", "[Amount]")).toBe("name")
        expect(classification(code, "tmdl", "formatString")).toBe("property")
        expect(classification(code, "tmdl", "source")).toBe("property")
        expect(classification(code, "tmdl", "#date")).toBe("keyword")
        expect(classification(code, "tmdl", '#"步骤"')).toBe("name")
        expect(classification(code, "tmdl", "mode")).toBe("property")
        expect(classification(code, "tmdl", "let", 1)).toBe("keyword")
    })

    it("缩进回退释放未闭合的 DAX 文本和 M 注释, 后续属性不串色", () => {
        const code =
            'table T\n    measure A =\n            "未完成\n        formatString: "0"\n    partition P = m\n        source =\n            /* 未完成\n        mode: import\n    column C\n        dataType: int64'
        expect(classification(code, "tmdl", "未完成", 0)).toBe("string")
        expect(classification(code, "tmdl", "formatString")).toBe("property")
        expect(classification(code, "tmdl", "未完成", 1)).toBe("comment")
        expect(classification(code, "tmdl", "mode")).toBe("property")
        expect(classification(code, "tmdl", "dataType")).toBe("property")
    })

    it("反引号块忽略外部缩进, 结束符先于内嵌未闭合词法状态处理", () => {
        const code =
            'table T\n    measure A = ```\nVAR x = "未完成\n```\n        formatString: 0\n    partition P = m\n        source = ```\nlet x = /* 未完成\n```\n        mode: import'
        expect(classification(code, "tmdl", "VAR")).toBe("keyword")
        expect(classification(code, "tmdl", "formatString")).toBe("property")
        expect(classification(code, "tmdl", "let")).toBe("keyword")
        expect(classification(code, "tmdl", "mode")).toBe("property")
        expect(classification(code, "tmdl", "```", 1)).toBe("punctuation")
        expect(classification(code, "tmdl", "```", 3)).toBe("punctuation")
    })

    it("原始表达式块保留文本类别, 不猜测嵌入代码或吞掉后续结构", () => {
        const code = "annotation Html = ```\n<img src=x> VAR let /*\n```\nTABLE T\n\tisHidden\n\tmeasure A = SUM(1)\n\t\tformatString: 0"
        expect(classification(code, "tmdl", "VAR")).toBe("string")
        expect(classification(code, "tmdl", "TABLE")).toBe("keyword")
        expect(classification(code, "tmdl", "isHidden")).toBe("property")
        expect(classification(code, "tmdl", "SUM")).toBe("function")
        expect(classification(code, "tmdl", "formatString")).toBe("property")
    })

    it("两空格缩进及 calculated 分区使用 DAX, query 分区保持原始内容", () => {
        const code =
            "table T\n  partition P = calculated\n    source =\n      SUM(1)\n    mode: import\n  partition Q = query\n    source = ```\nSELECT let VAR\n```\n    mode: import"
        expect(classification(code, "tmdl", "SUM")).toBe("function")
        expect(classification(code, "tmdl", "mode", 0)).toBe("property")
        expect(classification(code, "tmdl", "SELECT")).toBe("string")
        expect(classification(code, "tmdl", "mode", 1)).toBe("property")
    })

    it("带数字的 Unicode 对象名称和属性 GUID 不被拆成数字", () => {
        const code = "table 1名称\n    lineageTag: 123abc-def\n    compatibilityLevel: 1567"
        expect(classification(code, "tmdl", "1名称")).toBe("name")
        expect(classification(code, "tmdl", "123abc-def")).toBe("name")
        expect(classification(code, "tmdl", "1567")).toBe("number")
    })

    it("含等号和单引号转义的分区名称不干扰 M 或 calculated 源语言", () => {
        const code =
            "table T\n    partition 'P = m' = m\n        source =\n            let x = 1 in x\n    partition 'P'' = calculated' = calculated\n        source =\n            SUM(1)\n        mode: import"
        expect(classification(code, "tmdl", "'P = m'")).toBe("name")
        expect(classification(code, "tmdl", "let")).toBe("keyword")
        expect(classification(code, "tmdl", "SUM")).toBe("function")
        expect(classification(code, "tmdl", "mode")).toBe("property")
    })

    it("对象名称使用名称类别, 与对象同名的引用属性使用属性类别", () => {
        const code = "ref table T\n    hierarchy table\n        level L\n            column: column\n    measure measure = 1"
        expect(classification(code, "tmdl", "table", 0)).toBe("keyword")
        expect(classification(code, "tmdl", "table", 1)).toBe("name")
        expect(classification(code, "tmdl", "column", 0)).toBe("property")
        expect(classification(code, "tmdl", "column", 1)).toBe("name")
        expect(classification(code, "tmdl", "measure", 0)).toBe("keyword")
        expect(classification(code, "tmdl", "measure", 1)).toBe("name")
    })

    it("真实编辑状态插入和删除引号后增量解析仍释放表达式状态", () => {
        const code = 'table T\n    measure A =\n            "未完成\n        formatString: 0\n    measure B = SUM(2)'
        const original = EditorState.create({ doc: code, extensions: codeLanguage("tmdl") })
        syntaxTree(original)
        const position = code.indexOf("未完成") + "未完成".length
        const closed = original.update({ changes: { from: position, insert: '"' } }).state
        const reverted = closed.update({ changes: { from: position, to: position + 1 } }).state
        const ranges: HighlightedToken[] = []
        highlightTree(syntaxTree(reverted), tokenStyle, (from, to, type) => ranges.push({ from, to, type }))
        const property = code.indexOf("formatString")
        const call = code.indexOf("SUM")
        expect(ranges.find((token) => token.from <= property && property < token.to)?.type).toBe("property")
        expect(ranges.find((token) => token.from <= call && call < token.to)?.type).toBe("function")
        expect(reverted.doc.toString()).toBe(code)
    })
})
