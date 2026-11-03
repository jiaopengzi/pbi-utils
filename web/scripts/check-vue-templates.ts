/**
 * FilePath    : pbi-utils\web\scripts\check-vue-templates.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用现有 Vue 编译器只读检查全部组件模板, 补足脚本 lint 的覆盖边界.
 */

import { readFileSync, readdirSync } from "node:fs"
import { join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { compileScript, compileTemplate, parse, type SFCTemplateBlock } from "vue/compiler-sfc"

/** 检查原始模板中的每个原生或 Element Plus 表单, 要求持续阻止浏览器默认提交导航; 返回原 SFC 位置. */
function checkFormSubmissions(template: SFCTemplateBlock, filename: string): string[] {
    const diagnostics: string[] = []
    /** 遍历元素和嵌套模板, 不将注释、属性文字或动态事件名误认为提交保护. */
    function visit(nodes: NonNullable<SFCTemplateBlock["ast"]>["children"]): void {
        for (const node of nodes) {
            if (!("tag" in node)) continue
            if (["form", "el-form", "ElForm"].includes(node.tag)) {
                const prevented = node.props.some(
                    (prop) =>
                        "arg" in prop &&
                        prop.name === "on" &&
                        prop.arg &&
                        "isStatic" in prop.arg &&
                        prop.arg.isStatic &&
                        prop.arg.content === "submit" &&
                        prop.modifiers.some((modifier) => modifier.content === "prevent") &&
                        prop.modifiers.every((modifier) => ["prevent", "stop", "capture"].includes(modifier.content)),
                )
                if (!prevented) {
                    const { line, column } = node.loc.start
                    diagnostics.push(
                        `${filename}:${line}:${column}: [form-submit] 表单必须使用 @submit.prevent 持续阻止默认页面导航, 修饰符仅允许 prevent、stop 和 capture.`,
                    )
                }
            }
            visit(node.children)
        }
    }
    if (template.ast) visit(template.ast.children)
    return diagnostics
}

/** 检查单个 SFC 的解析和模板诊断, 返回带源文件位置的错误; 不执行组件代码. */
export function checkVueTemplate(source: string, filename: string): string[] {
    const { descriptor, errors } = parse(source, { filename })
    const diagnostics = errors.map((error) => formatDiagnostic(error, filename))
    if (errors.length) return diagnostics
    let bindings
    if (descriptor.script || descriptor.scriptSetup) {
        try {
            bindings = compileScript(descriptor, { id: filename }).bindings
        } catch (error) {
            // 宏约束与脚本解析错误可能直接抛出, 必须作为失败收集, 不中断其他文件的扫描.
            diagnostics.push(`${filename}: [vue-script] ${error instanceof Error ? error.message : String(error)}`)
        }
    }
    if (!descriptor.template) return diagnostics
    const template = descriptor.template
    if (template.src || (template.lang && template.lang !== "html")) {
        return [...diagnostics, `${filename}:${template.loc.start.line}:1: 模板检查仅支持内联 HTML 模板.`]
    }
    diagnostics.push(...checkFormSubmissions(template, filename))
    for (const mode of ["function", "module"] as const) {
        try {
            const result = compileTemplate({
                source: template.content,
                filename,
                id: filename,
                compilerOptions: {
                    // 函数模式保留原始 key 以检查重复; 模块模式解析表达式及绑定约束.
                    // 两种模式都只收集诊断, 不运行生成代码, 不能用其中一种替代另一种.
                    mode,
                    prefixIdentifiers: mode === "module",
                    cacheHandlers: false,
                    bindingMetadata: mode === "module" ? bindings : undefined,
                    expressionPlugins: descriptor.script?.lang === "ts" || descriptor.scriptSetup?.lang === "ts" ? ["typescript"] : [],
                },
            })
            diagnostics.push(...result.errors.map((error) => formatDiagnostic(error, filename, template.loc.start.line - 1, template.loc.start.column - 1)))
        } catch (error) {
            diagnostics.push(`${filename}:${template.loc.start.line}:1: [vue-template] ${error instanceof Error ? error.message : String(error)}`)
        }
    }
    return [...new Set(diagnostics)]
}

/** 将 Vue 诊断定位到原 SFC; 首行还需叠加模板标签后的列偏移, 解析错误已是文件位置. */
function formatDiagnostic(error: string | SyntaxError, filename: string, lineOffset = 0, columnOffset = 0): string {
    if (typeof error === "string") return `${filename}: ${error}`
    const diagnostic = error as SyntaxError & { code?: number; loc?: { start: { line: number; column: number } } }
    const start = diagnostic.loc?.start ?? { line: 1, column: 1 }
    return `${filename}:${start.line + lineOffset}:${start.column + (start.line === 1 ? columnOffset : 0)}: [vue${diagnostic.code ?? ""}] ${diagnostic.message}`
}

/** 扫描指定源码目录的全部 Vue 文件; 读取失败直接抛出, 不将漏扫判为通过. */
export function checkVueTemplates(root: string): { files: number; errors: string[] } {
    const files = readdirSync(root, { recursive: true, encoding: "utf8" })
        .filter((file) => file.endsWith(".vue"))
        .toSorted()
    if (!files.length) return { files: 0, errors: [`${root}: 未找到 Vue 组件, 请检查扫描目录.`] }
    return {
        files: files.length,
        errors: files.flatMap((file) => {
            const filename = join(root, file)
            return checkVueTemplate(readFileSync(filename, "utf8"), filename)
        }),
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const { files, errors } = checkVueTemplates(resolve(process.argv[2] ?? fileURLToPath(new URL("../src", import.meta.url))))
    if (errors.length) {
        console.error(errors.join("\n"))
        process.exitCode = 1
    } else console.log(`Vue template check: ${files} files passed.`)
}
