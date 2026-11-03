/**
 * FilePath    : pbi-utils\web\tests\tooling\editor-checks.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证样式语言服务、严格模板类型和独立入口能拒绝未被组件测试导入的错误.
 */

import { expect, it } from "vitest"
import { mkdirSync, mkdtempSync, writeFileSync, rmSync, readFileSync } from "node:fs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { spawnSync } from "node:child_process"
import { checkStyle, checkStyles, checkVueStyles } from "../../scripts/check-styles"
import { stringSelection } from "@/ui/forms"

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..")

it.each(["css", "scss"] as const)("%s 拒绝未知属性、错误属性值和缺少括号", (language) => {
    expect(checkStyle(".card { colro: red; }", "Typo.scss", language).join("\n")).toContain("unknownProperties")
    expect(checkStyle(".card { color: #ggg; }", "Color.scss", language).length).toBeGreaterThan(0)
    expect(checkStyle(".card { color: red;", "Syntax.scss", language).length).toBeGreaterThan(0)
})

it("内嵌样式的多块诊断保留原文件行列", () => {
    const errors = checkVueStyles(
        '<template><div/></template>\n<style scoped>.card { colro: red; }</style>\n<style lang="scss">.other { colro: blue; }</style>',
        "Styles.vue",
    )
    expect(errors.some((error) => error.startsWith("Styles.vue:2:23:"))).toBe(true)
    expect(errors.some((error) => error.startsWith("Styles.vue:3:29:"))).toBe(true)
})

it("保留合法 CSS 自定义属性、SCSS 模块与 Vue 样式语法", () => {
    expect(checkStyle(".card { --brand: #123; color: var(--brand); display: grid; }", "Valid.css", "css")).toEqual([])
    expect(
        checkVueStyles(
            '<template><div/></template><style lang="scss">@use "sass:map"; $brand: #123; @mixin frame { color: $brand; } .card { @include frame; &:hover { color: v-bind(color); } :deep(.child) { display: grid; } }</style>',
            "Valid.vue",
        ),
    ).toEqual([])
})

it("扫描真实源码样式, 合法多选标识保持原类型", () => {
    expect(checkStyles(join(webRoot, "src")).errors).toEqual([])
    expect(stringSelection(["measure", 123, "description"])).toEqual(["measure", "description"])
})

it("继承真实 tsconfig 的反例项目拒绝未知组件、指令、属性、事件及类型错误", () => {
    // 反例项目与实际源码共用依赖解析层级, 自带独立配置并在 finally 中删除.
    const root = mkdtempSync(join(webRoot, ".editor-check-"))
    const child =
        '<script setup lang="ts">\ndefineProps<{ message: string }>()\ndefineEmits<{ save: [value: number] }>()\n</script><template><button>{{ message }}</button></template>'
    const cases = {
        UnknownComponent: "<UnregisteredWidget/>",
        UnknownDirective: "<div v-unregistered/>",
        UnknownProp: '<Child message="ok" typo="bad"/>',
        UnknownEvent: '<Child message="ok" @savve="() => {}"/>',
        UnknownAria: '<Child message="ok" aria-labl="bad"/>',
        WrongPropType: '<Child :message="42"/>',
        WrongEventType: '<Child message="ok" @save="(value: string) => value.toUpperCase()"/>',
        MissingRequired: "<Child/>",
        WrongModel: '<NumericInput v-model="text"/>',
        UnknownTemplateValue: "<div>{{ missingValue }}</div>",
        UnknownDataValue: '<Child message="ok" :data-extra="missingValue"/>',
        SideEffectImport: "<div/>",
    }
    try {
        mkdirSync(join(root, "scripts"))
        writeFileSync(
            join(root, "tsconfig.json"),
            JSON.stringify({ extends: join(webRoot, "tsconfig.json"), include: ["*.vue", "scripts/*.ts", join(webRoot, "src/ui/attributes.d.ts")] }),
        )
        writeFileSync(join(root, "Child.vue"), child)
        writeFileSync(join(root, "NumericInput.vue"), '<script setup lang="ts">defineModel<number>({ required: true })</script><template><input/></template>')
        for (const [name, template] of Object.entries(cases)) {
            const imports = [
                template.includes("<Child") ? 'import Child from "./Child.vue";' : "",
                template.includes("<NumericInput")
                    ? 'import NumericInput from "./NumericInput.vue"; import { ref } from "vue"; const text = ref("hello");'
                    : "",
            ].join(" ")
            const extra = name === "SideEffectImport" ? 'import "./missing-module";' : ""
            writeFileSync(join(root, `${name}.vue`), `<script setup lang="ts">${imports} ${extra}</script><template>${template}</template>`)
        }
        writeFileSync(join(root, "scripts", "Unimported.ts"), 'export const value: number = "bad"')
        const checker = join(webRoot, "node_modules/vue-tsc/bin/vue-tsc.js")
        const failed = spawnSync(process.execPath, [checker, "--noEmit", "--project", join(root, "tsconfig.json")], { encoding: "utf8", timeout: 30000 })
        expect(failed.error).toBeUndefined()
        expect(failed.status).toBe(2)
        for (const name of Object.keys(cases)) expect(failed.stdout).toContain(`${name}.vue`)
        expect(failed.stdout).toContain("Unimported.ts")
        for (const name of Object.keys(cases)) {
            writeFileSync(
                join(root, `${name}.vue`),
                '<script setup lang="ts">import Child from "./Child.vue";</script><template><Child message="ok" aria-label="child" title="tip" data-test="ok" role="status" @save="(value: number) => value.toFixed()"/></template>',
            )
        }
        writeFileSync(join(root, "scripts", "Unimported.ts"), "export const value: number = 1")
        const passed = spawnSync(process.execPath, [checker, "--noEmit", "--project", join(root, "tsconfig.json")], { encoding: "utf8", timeout: 30000 })
        expect(passed.error).toBeUndefined()
        expect(passed.stdout).toBe("")
        expect(passed.status).toBe(0)
    } finally {
        rmSync(root, { recursive: true, force: true })
    }
}, 60000)

it("lint、测试与构建共享静态门禁, 脚本与独立测试目录显式参与类型检查", () => {
    const { scripts } = JSON.parse(readFileSync(join(webRoot, "package.json"), "utf8"))
    expect(scripts.lint).toContain("pnpm lint:templates")
    expect(scripts.lint).toContain("pnpm format:check")
    expect(scripts.lint).toContain("pnpm lint:styles")
    expect(scripts.lint).toContain("pnpm typecheck")
    expect(scripts.test).toBe("pnpm lint && vitest run")
    expect(scripts.build).toBe("pnpm lint && vite build")
    const config = JSON.parse(readFileSync(join(webRoot, "tsconfig.json"), "utf8"))
    expect(config.include).toContain("scripts/**/*.ts")
    expect(config.include).toContain("tests/**/*.ts")
})
