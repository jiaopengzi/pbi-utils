/**
 * FilePath    : pbi-utils\web\tests\tooling\vue-templates.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 Vue 模板门禁能拒绝动态重复 key 和未被组件测试导入的错误模板.
 */

import { expect, it } from "vitest"
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { fileURLToPath } from "node:url"
import { checkVueTemplate, checkVueTemplates } from "../../scripts/check-vue-templates"

it.each([':key="project.projectId"', 'key="same"', ':key="`model:${project.projectId}`"'])("拒绝同一条件链的重复 %s, 即使分支使用不同组件", (key) => {
    const source = `<script setup lang="ts">const project = { projectId: "p" }</script>\n<template>\n<ModelPanel v-if="project" ${key} />\n<!-- 相邻分支之间允许注释 -->\n<ConnectionPanel v-else-if="project" ${key} />\n<ReportPanel v-else ${key} />\n</template>`
    const errors = checkVueTemplate(source, "Example.vue")
    expect(errors.some((error) => error.startsWith("Example.vue:5:") && error.includes("[vue29]"))).toBe(true)
    expect(errors.some((error) => error.startsWith("Example.vue:6:") && error.includes("[vue29]"))).toBe(true)
})

it.each([
    '<div v-if="ok" :key="`model:${id}`"/><div v-else :key="`ai:${id}`"/>',
    '<div v-if="ok"/><div v-else/>',
    '<section><div v-if="ok" :key="id"/></section><section><div v-if="ok" :key="id"/></section>',
    '<div v-if="ok" :key="(id as string)"/><div v-else :key="`other:${id}`"/>',
])("允许不同分支身份、自动 key、独立条件链和 TS 表达式: %s", (template) => {
    expect(checkVueTemplate(`<script setup lang="ts">const id = "p"; const ok = true</script><template>${template}</template>`, "Valid.vue")).toEqual([])
})

it("报告孤立 v-else 和 SFC 解析错误", () => {
    expect(checkVueTemplate("<template><div v-else/></template>", "Orphan.vue").join("\n")).toContain("[vue30]")
    expect(checkVueTemplate("<template><div></template>", "Malformed.vue").join("\n")).toContain("Malformed.vue:")
})

it("模板与内容同行时仍报告原 SFC 的准确列号", () => {
    const source = '<template><div v-if="ok" :key="id"/><div v-else :key="id"/></template>'
    expect(checkVueTemplate(source, "Inline.vue").join("\n")).toContain(`Inline.vue:1:${source.lastIndexOf(':key="id"') + 1}: [vue29]`)
})

it.each([
    "<el-form><input /></el-form>",
    '<ElForm @submit="save" />',
    '<form v-on:submit.stop="save" />',
    '<el-form data-hint="@submit.prevent" />',
    '<el-form @[event].prevent="save" />',
    '<el-form @submit.prevent.once="save" />',
    '<el-form @submit.passive.prevent="save" />',
    '<el-form @submit.ctrl.prevent="save" />',
    '<el-form @submit.prevent.ctrl="save" />',
])("拒绝未持续阻止默认页面导航的表单: %s", (template) => {
    expect(checkVueTemplate(`<template>${template}</template>`, "Submission.vue").join("\n")).toContain("[form-submit]")
})

it.each(["<el-form @submit.prevent />", '<ElForm v-on:submit.stop.prevent="save" />', '<form @submit.capture.prevent="save" />'])(
    "允许显式阻止默认导航的展示表单和业务提交: %s",
    (template) => {
        expect(checkVueTemplate(`<template>${template}</template>`, "Protected.vue")).toEqual([])
    },
)

it("检查条件分支和插槽内的每个表单, 忽略注释并定位原 SFC", () => {
    const source =
        '<script setup>const ok = true</script>\n<template>\n<!-- <el-form /> -->\n<section><el-form v-if="ok" @submit.prevent /><template v-else><ElForm /></template></section>\n</template>'
    const errors = checkVueTemplate(source, "Nested.vue").filter((error) => error.includes("[form-submit]"))
    expect(errors).toEqual([
        `Nested.vue:4:${source.split("\n")[3]!.indexOf("<ElForm") + 1}: [form-submit] 表单必须使用 @submit.prevent 持续阻止默认页面导航, 修饰符仅允许 prevent、stop 和 capture.`,
    ])
})

it.each(['<div :title="foo("/>', '<button @click="let ="/>', "<div>{{ foo( }}</div>", '<div v-for="value in foo("/>'])(
    "模块模式补足函数模式遗漏的模板表达式语法: %s",
    (template) => {
        expect(checkVueTemplate(`<template>${template}</template>`, "Expression.vue").join("\n")).toContain("[vue46]")
    },
)

it.each(['<input v-model="foo()"/>', '<div v-for="item in items"/> <div v-else/>', '<div id="a" id="b"/>', '<div><template #default="{ value }"/></div>'])(
    "拒绝非法模型绑定、条件链、重复属性和插槽: %s",
    (template) => {
        expect(checkVueTemplate(`<template>${template}</template>`, "Invalid.vue").length).toBeGreaterThan(0)
    },
)

it.each(["defineProps({ first: String }); defineProps({ second: Number })", "const value: = 1", "export const value = 1"])(
    "独立检查脚本与 SFC 宏约束, 无需等待组件被测试导入: %s",
    (script) => {
        expect(checkVueTemplate(`<script setup lang="ts">${script}</script>`, "Script.vue").join("\n")).toContain("[vue-script]")
    },
)

it("扫描整个真实源码树, 防止只验证被组件测试导入的模板", () => {
    const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../src")
    expect(checkVueTemplates(root).errors).toEqual([])
})

it("命令扫描未导入的嵌套组件, 错误返回非零并定位原文件, 修复后通过", () => {
    const script = resolve(dirname(fileURLToPath(import.meta.url)), "../../scripts/check-vue-templates.ts")
    const root = mkdtempSync(join(tmpdir(), "pbi-utils-vue-templates-"))
    try {
        mkdirSync(join(root, "nested"))
        const filename = join(root, "nested", "Unused.vue")
        writeFileSync(filename, '<template><div v-if="ok" :key="id"/><div v-else :key="id"/></template>')
        const failed = spawnSync(process.execPath, [script, root], { encoding: "utf8" })
        expect(failed.status).toBe(1)
        expect(failed.stderr).toContain(`${filename}:1:`)
        expect(failed.stderr).toContain("[vue29]")
        writeFileSync(filename, '<template><div v-if="ok"/><div v-else/></template>')
        const passed = spawnSync(process.execPath, [script, root], { encoding: "utf8" })
        expect(passed.status).toBe(0)
        expect(passed.stdout).toContain("1 files passed")
    } finally {
        rmSync(root, { recursive: true, force: true })
    }
})
