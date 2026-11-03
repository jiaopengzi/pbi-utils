/**
 * FilePath    : pbi-utils\web\oxlint.config.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 配置 TypeScript 和 Vue 脚本的正确性, 可疑模式与性能检查.
 */

import { defineConfig } from "oxlint"

// 参考 blog-client 的原生插件与分类, 仅启用当前脚手架使用的规则.
export default defineConfig({
    plugins: ["unicorn", "typescript", "oxc", "vue", "eslint"],
    categories: {
        correctness: "error",
        suspicious: "warn",
        perf: "warn",
        pedantic: "off",
    },
    rules: {
        "eslint/no-unused-vars": "error",
        "no-debugger": "error",
        eqeqeq: "error",
        "no-var": "error",
        "typescript/no-explicit-any": "error",
        // Vite 在编译阶段替换的固定常量名称, 与业务私有变量命名区分.
        "no-underscore-dangle": ["warn", { allow: ["__MOCK_BRIDGE__", "__EDITOR_STYLE_NONCE__"] }],
    },
    ignorePatterns: ["dist/**", "coverage/**", "node_modules/**"],
})
