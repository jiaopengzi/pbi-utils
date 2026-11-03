/**
 * FilePath    : pbi-utils\web\scripts\chunking.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按稳定依赖层和纯语言文案拆分生产资源, 保持共享运行时单份及原页面生命周期.
 */
import { Buffer } from "node:buffer"
import type { Plugin } from "vite"

/** 从最终node_modules段读取完整npm包名, 同时支持pnpm实体路径和Windows分隔符; 应用/虚拟模块返回null. */
function packageName(moduleId: string): string | null {
    const normalized = moduleId.replaceAll("\\", "/")
    const index = normalized.lastIndexOf("/node_modules/")
    if (index < 0) return null
    const parts = normalized.slice(index + "/node_modules/".length).split("/")
    return parts[0]?.startsWith("@") ? parts.slice(0, 2).join("/") : (parts[0] ?? null)
}

/** 归并Vue自身及其内部包, 避免组件库递归捕获后拆出第二份运行时. */
function isVue(moduleId: string): boolean {
    const name = packageName(moduleId)
    return name === "vue" || name?.startsWith("@vue/") === true
}

/** Markdown及其HTML嵌入语法独立分组, 共享状态/视图/解析运行时仍由编辑器基础组持有. */
function isMarkdownGrammar(moduleId: string): boolean {
    const name = packageName(moduleId)
    return name?.startsWith("@codemirror/lang-") === true || ["@lezer/markdown", "@lezer/html", "@lezer/css", "@lezer/javascript"].includes(name ?? "")
}

/** 编辑器的状态/视图/解析运行时及DOM辅助依赖保持同组, 不更改挂载、样式nonce或历史. */
function isEditor(moduleId: string): boolean {
    const name = packageName(moduleId)
    return (
        !isMarkdownGrammar(moduleId) &&
        (name?.startsWith("@codemirror/") === true ||
            name?.startsWith("@lezer/") === true ||
            ["style-mod", "w3c-keyname", "crelt", "@marijn/find-cluster-break"].includes(name ?? ""))
    )
}

/** Element Plus与其图标共享组件组, 通用工具由更高优先级的独立组提供. */
function isElementPlus(moduleId: string): boolean {
    return ["element-plus", "@element-plus/icons-vue"].includes(packageName(moduleId) ?? "")
}

// 参考blog-client v1.0.0的vendor隔离原则, 按本项目实测依赖层合并小包.
// 基础依赖先归属, 保留Rolldown默认递归捕获, 避免UI组吸走Vue/共享工具并形成循环.
export const productionChunkGroups = [
    { name: "vue-runtime", test: isVue, priority: 30 },
    { name: "code-editor", test: isEditor, priority: 20 },
    // 语法层依赖基础组, 优先级低于共享运行时, 防止递归捕获形成循环或重复状态实例.
    { name: "markdown-grammar", test: isMarkdownGrammar, priority: 18 },
    {
        name: "vendor-support",
        test: (moduleId: string): boolean =>
            packageName(moduleId) !== null && !isVue(moduleId) && !isEditor(moduleId) && !isMarkdownGrammar(moduleId) && !isElementPlus(moduleId),
        priority: 15,
    },
    { name: "element-plus", test: isElementPlus, priority: 10 },
    // 字典均为纯数据; 偏好状态和翻译入口仍沿用原模块与静态导入, 不引入异步切换.
    { name: "locales", test: /[\\/]src[\\/]i18n[\\/][^\\/]+Messages\.ts$/, priority: 5 },
]

/** 按所有压缩后chunk的UTF-8字节核验实际阈值, 返回build专用post插件; 超限在写盘前以具体文件/字节原因失败. */
export function createChunkSizeGuard(): Plugin {
    let limit = 0
    return {
        name: "production-chunk-size-guard",
        apply: "build",
        enforce: "post",
        configResolved(config) {
            limit = config.build.chunkSizeWarningLimit * 1000
            if (!Number.isFinite(limit) || limit <= 0) throw new Error("分块体积阈值必须为正数")
        },
        generateBundle: {
            order: "post",
            handler(_options, bundle) {
                const oversized = Object.values(bundle)
                    .filter((item) => item.type === "chunk")
                    .map((chunk) => ({ file: chunk.fileName, bytes: Buffer.byteLength(chunk.code, "utf8") }))
                    .filter((chunk) => chunk.bytes > limit)
                if (oversized.length) this.error(`资源分块超过${limit / 1000} kB: ${oversized.map((chunk) => `${chunk.file} (${chunk.bytes} B)`).join(", ")}`)
            },
        },
    }
}
