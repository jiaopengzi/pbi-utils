/**
 * FilePath    : pbi-utils\web\tests\tooling\chunking.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 通过当前生产配置与真实Vite CLI核验体积及写盘阶段诊断, 不写dist或降低500kB门限.
 */
import { expect, it } from "vitest"
import { spawnSync } from "node:child_process"
import { mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..")
type ChunkFact = { file: string; bytes: number; characters: number; isEntry: boolean }
type ProbeResult = { code: number; output: string; chunks: ChunkFact[] }

/** 校验真实目录必须仍是本次临时根的直接子目录, 然后删除自有夹具; 身份异常直接失败. */
async function cleanup(root: string, tempRoot: string): Promise<void> {
    const absolute = await realpath(root)
    if (path.dirname(absolute).toLowerCase() !== tempRoot.toLowerCase() || !path.basename(absolute).startsWith("pbi-utils-chunking-"))
        throw new Error("分包夹具清理身份越界")
    await rm(absolute, { recursive: true })
}

/**
 * 将现有配置factory接入私有虚拟入口, 返回真实进程退出码与原始诊断.
 * @param characters 必须保留在最终JS中的字符串字符数; 实际UTF8体积由OutputChunk独立记录.
 * @param late 是否在原生写盘回调产生独立告警, 不以超限块代替该边界.
 * @param swallowed 是否受控吞掉logger抛错, 验证原生吞异常后进程仍失败.
 * @param dynamic 是否使用实际动态导入生成非entry块.
 * @param character 固定负载字符, 中文用于验证UTF8字节与字符数的区别.
 * @param failure 独立写盘或解析主错误, 验证原始原因不会被通用日志覆盖.
 * @returns 已结束CLI的状态和原始输出; 超时、启动或清理身份异常直接失败.
 */
async function buildProbe(
    characters: number,
    late = false,
    swallowed = false,
    dynamic = false,
    character = "x",
    failure?: "write" | "parse",
): Promise<ProbeResult> {
    const tempRoot = await realpath(tmpdir())
    const root = await mkdtemp(path.join(tempRoot, "pbi-utils-chunking-"))
    const config = path.join(root, "vite.config.mts")
    const actual = pathToFileURL(path.join(web, "vite.config.ts")).href
    try {
        // 导入实际factory并保留plugins/logger/groups/500阈值, 只替换隔离输入和输出.
        await writeFile(
            config,
            `import factory from ${JSON.stringify(actual)};
import { writeFileSync } from 'node:fs';
export default env => {
    const config = factory(env);
    return {
        ...config, root: ${JSON.stringify(root)}, publicDir: false,
        plugins: [...config.plugins, {
            name: 'controlled-chunking-boundary',
            resolveId(id) { if (id.startsWith('virtual:chunk-')) return '\\0' + id.slice(8); },
            load(id) {
                if (id === '\\0chunk-boundary' && ${failure === "parse"}) return 'export const broken = ;';
                if (id === '\\0chunk-boundary' && ${dynamic}) return 'import("virtual:chunk-payload").then(m=>globalThis.__pbiChunkBoundary=m.default)';
                if (id === '\\0chunk-boundary' || id === '\\0chunk-payload') return (id.endsWith('payload') ? 'export default ' : 'globalThis.__pbiChunkBoundary=') + JSON.stringify(${JSON.stringify(character)}.repeat(${characters}));
            },
            generateBundle(_options, bundle) {
                writeFileSync(${JSON.stringify(path.join(root, "chunks.json"))}, JSON.stringify(Object.values(bundle).filter(chunk => chunk.type === 'chunk').map(chunk => ({ file: chunk.fileName, bytes: Buffer.byteLength(chunk.code, 'utf8'), characters: chunk.code.length, isEntry: chunk.isEntry }))));
            },
            writeBundle() {
                if (${failure === "write"}) throw new Error('controlled primary write error');
                if (${late}) this.warn('controlled late diagnostic');
                if (${swallowed}) { try { config.customLogger.warn('controlled swallowed late diagnostic'); } catch {} }
            }
        }],
        build: {
            ...config.build, outDir: ${JSON.stringify(path.join(root, "out"))}, emptyOutDir: true,
            rolldownOptions: { ...config.build.rolldownOptions, input: 'virtual:chunk-boundary' }
        }
    };
};
`,
            "utf8",
        )
        const result = spawnSync(process.execPath, [path.join(web, "node_modules/vite/bin/vite.js"), "build", "--config", config], {
            cwd: root,
            encoding: "utf8",
            timeout: 30000,
            windowsHide: true,
            maxBuffer: 4 * 1024 * 1024,
            env: { ...process.env, NODE_ENV: "production" },
        })
        if (result.error) throw result.error
        if (result.status === null) throw new Error(`Vite未正常结束: ${result.signal}`)
        let chunks: ChunkFact[] = []
        try {
            chunks = JSON.parse(await readFile(path.join(root, "chunks.json"), "utf8")) as ChunkFact[]
        } catch (error) {
            // 解析错误先于generateBundle, 仅缺失观测文件是预期; 不吞损坏JSON或其它I/O异常.
            if (!(error instanceof Error) || !("code" in error) || error.code !== "ENOENT") throw error
        }
        return {
            code: result.status,
            output: result.stdout + result.stderr,
            chunks,
        }
    } finally {
        await cleanup(root, tempRoot)
    }
}

/** 按fixture观测到的真实最终块核对失败原因, 不让其它非零错误冒充正确的体积门禁. */
function expectSizeFailure(result: ProbeResult, select: (chunk: ChunkFact) => boolean = () => true): void {
    const chunk = result.chunks.find((item) => item.bytes > 500000 && select(item))
    expect(chunk, JSON.stringify(result.chunks)).toBeDefined()
    expect(result.code, result.output).not.toBe(0)
    expect(result.output).toContain("资源分块超过500 kB")
    expect(result.output).toContain(`${chunk!.file} (${chunk!.bytes} B)`)
}

it("真实Vite CLI允许正常小块, 不把所有构建都无条件拒绝", async () => {
    const result = await buildProbe(100)
    expect(result.code, result.output).toBe(0)
}, 35000)

it("真实Vite CLI必须拒绝超过500000B的最终JS块", async () => {
    const result = await buildProbe(510000)
    expectSizeFailure(result)
}, 35000)

it("真实Vite CLI对动态import的非entry超限块也必须拒绝", async () => {
    expectSizeFailure(await buildProbe(510000, false, false, true), (chunk) => !chunk.isEntry)
}, 35000)

it("真实Vite CLI按最终UTF8字节拒绝中文超限块, 不使用字符数或gzip", async () => {
    expectSizeFailure(await buildProbe(180000, false, false, false, "中"), (chunk) => chunk.characters < 500000)
}, 35000)

it("真实Vite CLI必须保留并拒绝独立的小块writeBundle告警", async () => {
    const result = await buildProbe(100, true)
    expect(result.output).toContain("controlled late diagnostic")
    expect(result.code, result.output).not.toBe(0)
}, 35000)

it("真实Vite CLI原生吞掉logger异常后仍必须进程失败", async () => {
    const result = await buildProbe(100, false, true)
    expect(result.output).toContain("controlled swallowed late diagnostic")
    expect(result.code, result.output).not.toBe(0)
}, 35000)

it("真实Vite CLI写盘主异常必须非零且保留原始原因", async () => {
    const result = await buildProbe(100, false, false, false, "x", "write")
    expect(result.code, result.output).not.toBe(0)
    expect(result.output).toContain("controlled primary write error")
}, 35000)

it("真实Vite CLI语法错误必须非零且保留具体解析原因", async () => {
    const result = await buildProbe(100, false, false, false, "x", "parse")
    expect(result.code, result.output).not.toBe(0)
    expect(result.output).toMatch(/PARSE_ERROR|Unexpected token/)
}, 35000)
