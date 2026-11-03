/**
 * FilePath    : pbi-utils\web\vite.config.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 配置 Vue 构建, 环境专用 CSP, 显式 mock 开关与前端测试.
 */

import { defineConfig } from "vitest/config"
import vue from "@vitejs/plugin-vue"
import { fileURLToPath, URL } from "node:url"
import { randomBytes } from "node:crypto"
import { availableParallelism } from "node:os"
import AutoImport from "unplugin-auto-import/vite"
import Components from "unplugin-vue-components/vite"
import { ElementPlusResolver } from "unplugin-vue-components/resolvers"
import { createBuildLogger } from "./scripts/build-logger.ts"
import { createTestResultsReporter } from "./scripts/test-results.ts"
import { createChunkSizeGuard, productionChunkGroups } from "./scripts/chunking.ts"

// 每次构建只生成一次样式授权标记, HTML 与编辑器使用同一值; 不放宽脚本或网络策略.
const editorStyleNonce = randomBytes(16).toString("base64")

/** 配置生产 CSP 与仅开发使用的 HMR 例外, mock 仅由显式模式开启. */
export default defineConfig(({ command, mode }) => {
    // 正式构建是独立Vite CLI; 诊断直接标记进程失败, 不让通用横幅覆盖原始异常.
    const buildLogger = command === "build" ? createBuildLogger() : undefined
    return {
        customLogger: buildLogger,
        plugins: [
            createChunkSizeGuard(),
            vue({ template: { compilerOptions: { comments: false } } }),
            // 参考 blog-client v1.0.0, 按需加载组件和 SCSS; 显式保留 Vue API 导入.
            // 程序式消息和确认框与模板组件统一使用品牌 SCSS, 避免混入默认 CSS.
            AutoImport({ resolvers: [ElementPlusResolver({ importStyle: "sass" })], dts: false }),
            Components({ dirs: [], resolvers: [ElementPlusResolver({ importStyle: "sass" })], dts: false }),
            {
                name: "local-csp",
                /** 根据构建模式注入 CSP; 生产禁止远程连接与内联脚本. */
                transformIndexHtml: (html: string) =>
                    html.replace(
                        "__CSP__",
                        command === "serve"
                            ? "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:5173; img-src 'self' data: blob:; object-src 'none'; base-uri 'none'; frame-src 'none'"
                            : `default-src 'self'; script-src 'self'; style-src 'self' 'nonce-${editorStyleNonce}'; style-src-attr 'unsafe-inline'; connect-src 'none'; img-src 'self' data: blob:; object-src 'none'; base-uri 'none'; frame-src 'none'`,
                    ),
            },
        ],
        resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
        // 按需组件共用 ES 入口, 预构建避免首次导航触发依赖重优化和过期模块.
        optimizeDeps: {
            include: [
                "element-plus/es",
                // 按需组件的样式入口在模板转换后出现, 预先列入避免冷启动第一次导航重优化.
                ...[
                    "alert",
                    "autocomplete",
                    "base",
                    "breadcrumb-item",
                    "breadcrumb",
                    "button",
                    "card",
                    "checkbox-group",
                    "checkbox",
                    "collapse-item",
                    "collapse",
                    "config-provider",
                    "descriptions-item",
                    "descriptions",
                    "dialog",
                    "divider",
                    "dropdown",
                    "dropdown-item",
                    "dropdown-menu",
                    "empty",
                    "form-item",
                    "form",
                    "input-number",
                    "input",
                    "message-box",
                    "message",
                    "option",
                    "option-group",
                    "select",
                    "skeleton",
                    "statistic",
                    "step",
                    "steps",
                    "switch",
                    "tab-pane",
                    "table-column",
                    "table",
                    "tabs",
                    "tag",
                    "tooltip",
                ].map((component) => `element-plus/es/components/${component}/style/index`),
                "element-plus/es/components/date-picker/style/index",
                "element-plus/es/components/progress/style/index",
                "element-plus/es/locale/lang/en",
                "element-plus/es/locale/lang/zh-cn",
            ],
        },
        server: { host: "127.0.0.1", port: 5173, strictPort: true },
        css: {
            preprocessorOptions: {
                scss: {
                    api: "modern-compiler",
                    /** 仅按需 Element Plus 入口加载其品牌变量; 本地样式只需断点, 避免重复求值完整组件变量表. */
                    additionalData: (source: string, filename: string) =>
                        filename.replaceAll("\\", "/").includes("/node_modules/element-plus/")
                            ? `@use "@/ui/theme" as *;\n${source}`
                            : `@use "@/app/responsive" as *;\n${source}`,
                },
            },
        },
        build: {
            minify: "oxc",
            chunkSizeWarningLimit: 500,
            // 沿用 SPA 按资源类型输出的组织方式, 本地宿主不需要 HTTP gzip 或 API 代理.
            rolldownOptions: {
                output: {
                    // 依赖层及纯字典各自缓存, 不改变原页面/编辑器的挂载与执行入口.
                    codeSplitting: { groups: productionChunkGroups },
                    entryFileNames: "static/js/[name]-[hash].js",
                    chunkFileNames: "static/js/[name]-[hash].js",
                    assetFileNames: "static/[ext]/[name]-[hash].[ext]",
                },
            },
        },
        define: {
            __MOCK_BRIDGE__: JSON.stringify(command === "serve" && mode === "mock"),
            __EDITOR_STYLE_NONCE__: JSON.stringify(editorStyleNonce),
        },
        // 限制重型 Vue/Element Plus 用例的并发资源争用, 保留逐文件隔离和默认超时; 低核机器不超出可用并发量.
        test: {
            maxWorkers: Math.min(4, availableParallelism()),
            environment: "jsdom",
            include: ["tests/**/*.test.ts"],
            setupFiles: ["tests/setup.ts"],
            silent: false,
            allowOnly: false,
            passWithNoTests: false,
            dangerouslyIgnoreUnhandledErrors: false,
            reporters: ["default", createTestResultsReporter()],
            server: { deps: { inline: ["element-plus"] } },
        },
    }
})
