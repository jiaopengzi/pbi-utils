/**
 * FilePath    : pbi-utils\web\scripts\check-styles.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用 VS Code 样式语言服务检查独立及 Vue 内嵌 CSS/SCSS, 将编辑器诊断接入门禁.
 */

import { readFileSync, readdirSync } from "node:fs"
import { join, resolve } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"
import { parse } from "vue/compiler-sfc"
import { getCSSLanguageService, getSCSSLanguageService } from "vscode-css-languageservice"
import { TextDocument } from "vscode-languageserver-textdocument"

const services = { css: getCSSLanguageService(), scss: getSCSSLanguageService() }

/** 校验样式文本的语法和属性; 错误和警告均失败, 返回原文件位置, 不联网或生成 CSS. */
export function checkStyle(source: string, filename: string, language: "css" | "scss"): string[] {
    const service = services[language]
    const document = TextDocument.create(pathToFileURL(resolve(filename)).href, language, 0, source)
    return service
        .doValidation(document, service.parseStylesheet(document))
        .filter((diagnostic) => diagnostic.severity === 1 || diagnostic.severity === 2)
        .map(
            (diagnostic) =>
                `${filename}:${diagnostic.range.start.line + 1}:${diagnostic.range.start.character + 1}: [${language}/${diagnostic.code}] ${diagnostic.message}`,
        )
}

/** 检查所有内嵌样式块, 用空白保留 SFC 原始行列; 不支持的外部或预处理样式明确失败. */
export function checkVueStyles(source: string, filename: string): string[] {
    const { descriptor, errors } = parse(source, { filename })
    if (errors.length) return errors.map((error) => `${filename}: ${typeof error === "string" ? error : error.message}`)
    return descriptor.styles.flatMap((style) => {
        const language = style.lang ?? "css"
        if (style.src || (language !== "css" && language !== "scss")) return [`${filename}:${style.loc.start.line}:1: 样式检查仅支持内联 CSS/SCSS.`]
        const prefix = source.slice(0, style.loc.start.offset).replace(/[^\r\n]/g, " ")
        return checkStyle(prefix + style.content, filename, language)
    })
}

/** 递归扫描全部源码样式和 Vue 内嵌样式; 空扫描和读取失败不得判为通过. */
export function checkStyles(root: string): { files: number; errors: string[] } {
    const files = readdirSync(root, { recursive: true, encoding: "utf8" })
        .filter((file) => /\.(?:vue|css|scss)$/.test(file))
        .toSorted()
    if (!files.length) return { files: 0, errors: [`${root}: 未找到样式源码, 请检查扫描目录.`] }
    return {
        files: files.length,
        errors: files.flatMap((file) => {
            const filename = join(root, file)
            const source = readFileSync(filename, "utf8")
            return file.endsWith(".vue") ? checkVueStyles(source, filename) : checkStyle(source, filename, file.endsWith(".scss") ? "scss" : "css")
        }),
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const { files, errors } = checkStyles(resolve(process.argv[2] ?? fileURLToPath(new URL("../src", import.meta.url))))
    if (errors.length) {
        console.error(errors.join("\n"))
        process.exitCode = 1
    } else console.log(`Style check: ${files} files passed.`)
}
