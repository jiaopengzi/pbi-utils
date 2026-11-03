/**
 * FilePath    : pbi-utils\web\tests\tooling\frontend-inputs.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用独立文件树验证同轮前端检查输入快照的失效、链接与命令行边界.
 */

import { afterEach, expect, it } from "vitest"
import { copyFileSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, symlinkSync, utimesSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { fileURLToPath } from "node:url"
import { snapshotFrontendInputs } from "../../scripts/frontend-inputs"

const fixtures: string[] = []
const scriptPath = resolve(dirname(fileURLToPath(import.meta.url)), "../../scripts/frontend-inputs.ts")

/** 创建独立仓库文件树并返回根目录; 文件创建失败直接抛出, 测试结束后统一删除. */
function repository(): string {
    const root = mkdtempSync(join(tmpdir(), "pbi-frontend-inputs-"))
    fixtures.push(root)
    for (const path of ["web/src", "web/scripts", "web/node_modules/.pnpm/pkg/node_modules/pkg"]) {
        mkdirSync(join(root, path), { recursive: true })
    }
    for (const [path, content] of Object.entries({
        "web/src/App.vue": "<template><div>A</div></template>",
        "web/src/env.d.ts": "declare const initial: string",
        "web/.oxfmtrc.json": "{}",
        "web/pnpm-lock.yaml": "lockfileVersion: '9.0'",
        "web/node_modules/.modules.yaml": "layoutVersion: 5",
        "web/node_modules/.pnpm/lock.yaml": "lockfileVersion: '9.0'",
        "web/node_modules/.pnpm/pkg/node_modules/pkg/index.js": "export const a = 1",
    })) {
        writeFileSync(join(root, path), content)
    }
    return root
}

afterEach(() => {
    for (const root of fixtures.splice(0)) rmSync(root, { recursive: true, force: true })
})

it("未变化输入快照稳定并允许同轮复用", () => {
    const root = repository()
    const initial = snapshotFrontendInputs(root)
    expect(initial.reusable).toBe(true)
    expect(initial.fingerprint).toMatch(/^[a-f0-9]{64}$/)
    expect(snapshotFrontendInputs(root)).toEqual(initial)
})

it.each(["web/src/App.vue", "web/src/env.d.ts", "web/.oxfmtrc.json", "web/pnpm-lock.yaml"])("%s 同长度并恢复时间戳的内容修改仍使快照失效", (path) => {
    const root = repository()
    const filename = join(root, path)
    const before = snapshotFrontendInputs(root)
    const stat = lstatSync(filename)
    const content = readFileSync(filename)
    content[0] = content[0] === 65 ? 66 : 65
    writeFileSync(filename, content)
    utimesSync(filename, stat.atime, stat.mtime)
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(before.fingerprint)
})

it("源文件新增、删除和改名分别改变快照, 删除临时反例后恢复原输入", () => {
    const root = repository()
    const before = snapshotFrontendInputs(root)
    const first = join(root, "web/scripts/first.ts")
    const second = join(root, "web/scripts/second.ts")
    writeFileSync(first, "export const value = 1")
    const added = snapshotFrontendInputs(root)
    expect(added.fingerprint).not.toBe(before.fingerprint)
    renameSync(first, second)
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(added.fingerprint)
    rmSync(second)
    expect(snapshotFrontendInputs(root)).toEqual(before)
    const temporary = join(root, "web/.editor-check-example")
    mkdirSync(temporary)
    writeFileSync(join(temporary, "Invalid.vue"), "<broken>")
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(before.fingerprint)
    rmSync(temporary, { recursive: true })
    expect(snapshotFrontendInputs(root)).toEqual(before)
})

it.each([".gitignore", ".editorconfig"])("根目录 %s 的新增、修改和删除分别参与快照", (name) => {
    const root = repository()
    const before = snapshotFrontendInputs(root)
    const path = join(root, name)
    writeFileSync(path, "initial")
    const added = snapshotFrontendInputs(root)
    expect(added.fingerprint).not.toBe(before.fingerprint)
    writeFileSync(path, "changed")
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(added.fingerprint)
    rmSync(path)
    expect(snapshotFrontendInputs(root)).toEqual(before)
})

it("依赖文件的新增、删除、改名和写入时间变化均使快照失效", () => {
    const root = repository()
    const before = snapshotFrontendInputs(root)
    const dependency = join(root, "web/node_modules/.pnpm/pkg/node_modules/pkg/index.js")
    const extra = join(dirname(dependency), "extra.d.ts")
    writeFileSync(extra, "export const value: number")
    const added = snapshotFrontendInputs(root)
    expect(added.fingerprint).not.toBe(before.fingerprint)
    renameSync(extra, join(dirname(extra), "renamed.d.ts"))
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(added.fingerprint)
    rmSync(join(dirname(extra), "renamed.d.ts"))
    expect(snapshotFrontendInputs(root)).toEqual(before)
    writeFileSync(dependency, "export const b = 2")
    utimesSync(dependency, new Date(), new Date("2030-01-01T00:00:00Z"))
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(before.fingerprint)
})

it.each([".modules.yaml", ".pnpm/lock.yaml"])("依赖元数据 %s 同长度并恢复时间戳的修改仍失效", (path) => {
    const root = repository()
    const filename = join(root, "web/node_modules", path)
    const before = snapshotFrontendInputs(root)
    const stat = lstatSync(filename)
    const content = readFileSync(filename)
    content[0] = 65
    writeFileSync(filename, content)
    utimesSync(filename, stat.atime, stat.mtime)
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(before.fingerprint)
})

it("仅约定的生成输出与依赖缓存不影响输入快照", () => {
    const root = repository()
    const before = snapshotFrontendInputs(root)
    for (const path of ["web/dist", "web/coverage", "web/node_modules/.vite", "web/node_modules/.vite-temp", "web/node_modules/.cache"]) {
        const directory = join(root, path)
        mkdirSync(directory, { recursive: true })
        writeFileSync(join(directory, "generated.js"), "generated")
    }
    expect(snapshotFrontendInputs(root)).toEqual(before)
    mkdirSync(join(root, "web/node_modules/.other"))
    writeFileSync(join(root, "web/node_modules/.other/dependency.js"), "must participate")
    expect(snapshotFrontendInputs(root).fingerprint).not.toBe(before.fingerprint)
})

it("依赖内部目录链接只记录身份并避免环, 外部链接与源内链接禁止复用", () => {
    const root = repository()
    const dependencies = join(root, "web/node_modules")
    const packageDirectory = join(dependencies, ".pnpm/pkg/node_modules/pkg")
    const dependencyLink = join(dependencies, "pkg")
    const before = snapshotFrontendInputs(root)
    symlinkSync(packageDirectory, dependencyLink, "junction")
    symlinkSync(packageDirectory, join(packageDirectory, "cycle"), "junction")
    const internal = snapshotFrontendInputs(root)
    expect(internal.reusable).toBe(true)
    expect(internal.fingerprint).not.toBe(before.fingerprint)
    rmSync(dependencyLink)
    const outside = join(root, "outside")
    mkdirSync(outside)
    symlinkSync(outside, dependencyLink, "junction")
    const external = snapshotFrontendInputs(root)
    expect(external.reusable).toBe(false)
    expect(external.fingerprint).not.toBe(internal.fingerprint)
    rmSync(dependencyLink)
    symlinkSync(packageDirectory, join(root, "web/src/linked"), "junction")
    expect(snapshotFrontendInputs(root).reusable).toBe(false)
})

it("必要依赖目录缺失时抛错, 不以不完整快照宣布可复用", () => {
    const root = repository()
    renameSync(join(root, "web/node_modules"), join(root, "web/dependencies-missing"))
    expect(() => snapshotFrontendInputs(root)).toThrow()
})

it("命令行按自身路径定位仓库, stdout 恰好为一个完整 JSON, 错误使用 stderr", () => {
    const root = repository()
    const localScript = join(root, "web/scripts/frontend-inputs.ts")
    copyFileSync(scriptPath, localScript)
    const result = spawnSync(process.execPath, [localScript], { cwd: tmpdir(), encoding: "utf8", timeout: 10000 })
    expect(result.error).toBeUndefined()
    expect(result.status).toBe(0)
    expect(result.stderr).toBe("")
    expect(JSON.parse(result.stdout)).toEqual(snapshotFrontendInputs(root))
    expect(result.stdout.trim().split("\n")).toHaveLength(1)
    renameSync(join(root, "web/node_modules"), join(root, "web/dependencies-missing"))
    const failure = spawnSync(process.execPath, [localScript], { encoding: "utf8", timeout: 10000 })
    expect(failure.error).toBeUndefined()
    expect(failure.status).toBe(1)
    expect(failure.stdout).toBe("")
    expect(failure.stderr.length).toBeGreaterThan(0)
})
