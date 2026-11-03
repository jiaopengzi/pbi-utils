/**
 * FilePath    : pbi-utils\web\tests\features\code\measureExpression.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证度量值编辑正文与保存包装转换, 保留跨行字面量、注释与未完成草稿.
 */
import { describe, expect, it } from "vitest"
import { measureExpressionForEditor, measureExpressionForSave } from "@/features/code/measureExpression"

describe("度量值正文显示与保存", () => {
    it.each([
        ["SUM ( Sales[Amount] )", "\nSUM ( Sales[Amount] )"],
        [
            "VAR Result =\n    DIVIDE (\n        [Profit],\n        [Sales]\n    )\nRETURN\n    Result",
            "\nVAR Result =\n    DIVIDE (\n        [Profit],\n        [Sales]\n    )\nRETURN\n    Result",
        ],
        ["// leading comment\nVAR Result = 1\nRETURN Result", "\n// leading comment\nVAR Result = 1\nRETURN Result"],
    ])("保存只增加定义头换行且编辑回填保留语法缩进: %s", (editor, stored) => {
        expect(measureExpressionForEditor(stored)).toBe(editor)
        expect(measureExpressionForSave(editor)).toBe(stored)
        expect(measureExpressionForSave(stored)).toBe(stored)
        expect(measureExpressionForEditor(editor)).toBe(editor)
    })

    it.each([
        'VAR Text = "first\n    second ""quoted""\nlast"\nRETURN Text',
        "SUM ( 'first\n    table''name'[Amount] )",
        "SUM ( 'Table'[first\n    column]]name] )",
        "/* first\n    inside\nlast */\nVAR Result = 1\nRETURN Result",
        'VAR Text = "// not comment\n/* still text */" // "not a string\nRETURN Text',
        "-- 'not a quote\nVAR Result = 1 /* \" not text */\nRETURN Result",
        'VAR Text = "unfinished\n    literal',
        "VAR Result = 1 /* unfinished\n    comment",
        "SUM ( 'unfinished\n    table",
        "SUM ( Table[unfinished\n    column",
        "    VAR Result = 1\n        RETURN Result",
        "  VAR Result = 1\n\tRETURN Result",
    ])("跨行词元、未闭合草稿和用户已有缩进均逐字符保留: %s", (editor) => {
        const stored = "\n" + editor
        expect(measureExpressionForEditor(stored)).toBe(editor)
        expect(measureExpressionForSave(editor)).toBe(stored)
        expect(measureExpressionForSave(stored)).toBe(stored)
    })

    it("只去除一层首换行, 不裁剪用户额外空行和首行缩进", () => {
        expect(measureExpressionForEditor("\n\n    SUM ( T[A] )")).toBe("\n    SUM ( T[A] )")
        expect(measureExpressionForSave("\n\n    SUM ( T[A] )")).toBe("\n\n    SUM ( T[A] )")
    })

    it("转换保留空行、尾随空白及字面量内 CRLF, 保存包装首部采用 LF", () => {
        const editor = 'VAR Text = "first\r\n    second"  \r\n\r\nRETURN Text\r\n'
        const stored = '\nVAR Text = "first\r\n    second"  \r\n\r\nRETURN Text\r\n'
        expect(measureExpressionForSave(editor)).toBe(stored)
        expect(measureExpressionForEditor(stored)).toBe(editor)
        expect(measureExpressionForEditor("\r\n" + stored.slice(1))).toBe(editor)
        expect(measureExpressionForSave("\r\n" + stored.slice(1))).toBe(stored)
    })

    it.each(["", " ", "\n\t\r\n"])("空白草稿不被转换成看似有值的保存正文: %s", (source) => {
        expect(measureExpressionForEditor(source)).toBe(source)
        expect(measureExpressionForSave(source)).toBe(source)
    })
})
