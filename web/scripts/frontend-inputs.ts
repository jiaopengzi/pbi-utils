/**
 * FilePath    : pbi-utils\web\scripts\frontend-inputs.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 对同轮前端静态检查输入生成内容与依赖身份快照, 不保存跨轮缓存.
 */

import { createHash } from "node:crypto"
import { lstatSync, readFileSync, readdirSync, readlinkSync, realpathSync } from "node:fs"
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path"
import { fileURLToPath } from "node:url"

/**
 * 读取仓库前端源码、目录快照与依赖身份, 返回 SHA-256 指纹及是否允许同轮复用.
 * 源码和安装元数据按内容计算, 普通依赖使用路径、长度与纳秒修改时间; 不保证识别人为恢复长度和时间的依赖改写.
 * 源码链接或越出依赖根的链接禁止复用; 不递归链接, 文件缺失、不可读和未知文件类型直接抛错.
 * @param repositoryRoot 仓库根目录, 不依赖当前工作目录.
 * @returns 稳定输入指纹和链接边界允许的复用标记.
 */
export function snapshotFrontendInputs(repositoryRoot: string): { fingerprint: string; reusable: boolean } {
    const root = resolve(repositoryRoot)
    const webRoot = join(root, "web")
    const dependencyRoot = join(webRoot, "node_modules")
    const hash = createHash("sha256")
    let reusable = true

    /** 把有界结构记录写入哈希, 参数不写日志; JSON 数组保留字段边界和特殊路径字符. */
    function record(...fields: string[]): void {
        hash.update(JSON.stringify(fields))
    }

    /** 将输入绝对路径转换为仓库内稳定相对路径, 不受平台分隔符影响. */
    function filename(path: string): string {
        return relative(root, path).split(sep).join("/")
    }

    /** 读取一个普通文件并记录其内容哈希, 读取失败直接向调用方传播. */
    function content(path: string): void {
        record("content", filename(path), createHash("sha256").update(readFileSync(path)).digest("hex"))
    }

    /** 判断真实链接目标是否仍位于依赖根内, 相对路径不得上行或切换卷. */
    function insideDependencies(path: string): boolean {
        const location = relative(dependencyRoot, path)
        return !isAbsolute(location) && location !== ".." && !location.startsWith(`..${sep}`)
    }

    /** 按路径稳定遍历实际文件; 源码用内容、依赖用元数据, 链接只记录身份且不跟随. */
    function visit(path: string, dependency: boolean): void {
        const stat = lstatSync(path, { bigint: true })
        if (stat.isSymbolicLink()) {
            const target = realpathSync(path)
            record("link", filename(path), readlinkSync(path), target)
            if (!dependency || !insideDependencies(target)) reusable = false
            return
        }
        if (stat.isDirectory()) {
            // 不记录目录时间, 生成缓存的创建或清理不代表被检查的文件发生变化.
            record("directory", filename(path))
            for (const name of readdirSync(path).toSorted()) {
                if (!dependency && path === webRoot && ["dist", "coverage", "node_modules"].includes(name)) continue
                if (dependency && path === dependencyRoot && [".vite", ".vite-temp", ".cache"].includes(name)) continue
                visit(join(path, name), dependency)
            }
            return
        }
        if (!stat.isFile()) throw new Error(`不支持的前端输入文件类型: ${filename(path)}`)
        if (!dependency || path === join(dependencyRoot, ".modules.yaml") || path === join(dependencyRoot, ".pnpm/lock.yaml")) content(path)
        else record("dependency", filename(path), stat.size.toString(), stat.mtimeNs.toString())
    }

    visit(webRoot, false)
    visit(dependencyRoot, true)
    // 格式工具可读取祖先目录规则, 规则文件的新增或删除也必须使本轮检查失效.
    for (const name of [".gitignore", ".editorconfig"]) {
        const path = join(root, name)
        if (lstatSync(path, { throwIfNoEntry: false })) visit(path, false)
        else record("missing", filename(path))
    }
    return { fingerprint: hash.digest("hex"), reusable }
}

// 原生入口只输出一个 JSON 文档; 异常写 stderr, 不将不完整指纹视为成功.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..")
        process.stdout.write(`${JSON.stringify(snapshotFrontendInputs(root))}\n`)
    } catch (error) {
        console.error(error instanceof Error ? error.message : String(error))
        process.exitCode = 1
    }
}
