/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfMock.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟的来源登记、目录与本地函数状态机.
 */
import type { UdfCatalogStatus, UdfPackage, LocalUdfDefinition, LocalUdfEntry, UdfSourceEntry } from "./contracts"
import { copy } from "./mockValue"
import { matchUdfName } from "./udfSearch"
import { normalizeUdfRepositoryUrl, validateUdfRepositoryReference, validateUdfSourceName } from "./udfSourceRepository"
/** 显式模拟的精确 tag 提交者时间, 不读取网络或伪称真实 Git 日期. */
const mockCommittedAt: Record<string, string> = {
    "0.9.0": "2026-08-20T03:28:40+08:00",
    "1.0.0": "2026-09-20T03:28:40+08:00",
    "1.1.0-beta": "2026-10-05T03:28:40+08:00",
    "1.2.0": "2026-10-01T03:28:40+08:00",
    "1.10.0": "2026-10-20T03:28:40+08:00",
}
/** 模拟UDF身份和基线, 不执行真实来源请求或持久化. */
export class UdfMock {
    private catalogs: Record<string, UdfCatalogStatus> = {}
    private locals: LocalUdfEntry[] = []
    private sources: UdfSourceEntry[] = [
        { id: "daxlib", name: "DAXLib", baseUrl: "https://github.com/daxlib/daxlib", baseline: null, kind: "builtin", protocol: "git" },
        { id: "local", name: "Local", baseUrl: null, baseline: null, kind: "local", protocol: "git" },
    ]
    /** 只返回内存模拟结果, 失效基线和未知方法保持原错误. */
    handle(method: string, params: Record<string, unknown>): unknown {
        if (method === "udf.sources") return { items: copy(this.sources) }
        if (method === "udf.sourceSave") {
            if ((params.id === undefined || params.id === null) !== (params.baseline === undefined || params.baseline === null))
                throw new Error("UDF_SOURCE_BASELINE_CHANGED")
            const previous = this.sources.find((entry) => entry.id === params.id)
            if (params.id && (!previous || previous.kind !== "custom" || previous.baseline !== params.baseline)) throw new Error("UDF_SOURCE_BASELINE_CHANGED")
            if (params.protocol !== undefined && params.protocol !== "git") throw new Error("UDF_SOURCE_PROTOCOL_INVALID")
            const baseUrl = normalizeUdfRepositoryUrl(String(params.baseUrl))
            const gitRef = validateUdfRepositoryReference(params.gitRef === undefined || params.gitRef === null ? null : String(params.gitRef))
            const entry: UdfSourceEntry = {
                id: previous?.id ?? `source:${crypto.randomUUID().replaceAll("-", "")}`,
                name: validateUdfSourceName(String(params.name).trim()),
                baseUrl,
                baseline: `${crypto.randomUUID().replaceAll("-", "")}${crypto.randomUUID().replaceAll("-", "")}`,
                kind: "custom",
                protocol: "git",
                gitRef,
            }
            if (
                this.sources.some(
                    (item) =>
                        item.id !== entry.id &&
                        (item.name.toLowerCase() === entry.name.toLowerCase() || (item.baseUrl === entry.baseUrl && (item.gitRef ?? null) === gitRef)),
                )
            )
                throw new Error("UDF_SOURCE_DUPLICATE")
            this.sources = [...this.sources.filter((item) => item.id !== entry.id), entry]
            return copy(entry)
        }
        if (method === "udf.sourceDelete") {
            const item = this.sources.find((entry) => entry.id === params.id)
            if (!item || item.kind !== "custom" || item.baseline !== params.baseline || params.confirm !== true) throw new Error("UDF_SOURCE_BASELINE_CHANGED")
            this.sources = this.sources.filter((entry) => entry.id !== item.id)
            return { deleted: true }
        }
        if (method === "udf.catalog" || method === "udf.catalogRefresh") {
            const source = this.sources.find((entry) => entry.id === (params.source ?? "daxlib"))
            if (!source || source.kind === "local") throw new Error("UDF_SOURCE_NOT_FOUND")
            if (source.kind === "custom" && source.baseline !== params.sourceBaseline) throw new Error("UDF_SOURCE_BASELINE_CHANGED")
            const status = this.catalogs[source.id] ?? {
                initialized: false,
                baseline: null,
                retrievedAt: null,
                packageCount: 0,
                versionCount: 0,
                functionCount: 0,
            }
            if (method === "udf.catalogRefresh") {
                this.catalogs[source.id] = {
                    initialized: true,
                    baseline: crypto.randomUUID().replaceAll("-", "").repeat(2),
                    retrievedAt: new Date().toISOString(),
                    packageCount: 1,
                    versionCount: 5,
                    functionCount: 2,
                }
                return copy(this.catalogs[source.id])
            }
            if (params.action === "status") return { status: copy(status) }
            if (!status.initialized) throw new Error("UDF_CATALOG_UNINITIALIZED")
            if (params.catalogBaseline && params.catalogBaseline !== status.baseline) throw new Error("UDF_CATALOG_CHANGED")
            if (params.action === "search")
                return {
                    packages: [{ id: "mock.sample", version: "1.0.0", description: "Browser simulation" }],
                    fromCache: true,
                    total: 1,
                    nextOffset: null,
                    status: copy(status),
                    retrievedAt: status.retrievedAt,
                }
            if (params.action === "versions") return { versions: ["1.0.0", "0.9.0", "1.1.0-beta", "1.10.0", "1.2.0"] }
            const pkg: UdfPackage = {
                id: String(params.packageId),
                version: String(params.version),
                hash: "c".repeat(64),
                description: "Browser simulation",
                readme: "# Browser simulation\n\nThis package contains all sample functions.",
                metadata: { authors: "Mock author", tags: ["Simulation"], releaseNotes: "", projectUrl: null, repositoryUrl: null },
                licenseFiles: { LICENSE: "Mock license" },
                dependencies: params.packageId === "mock.dependency" ? [] : [{ id: "mock.dependency", version: "1.0.0" }],
                functions: (params.packageId === "mock.dependency"
                    ? [source.kind === "custom" ? "Team.Dependency" : "Mock.Dependency"]
                    : [`${source.kind === "custom" ? "Team" : "Mock"}.Sample.First`, `${source.kind === "custom" ? "Team" : "Mock"}.Sample.Second`]
                ).map((name) => ({ name, expression: "() => 1", description: "", annotations: {} })),
                retrievedAt: new Date().toISOString(),
                fromCache: true,
                committedAt: mockCommittedAt[String(params.version)] ?? null,
            }
            if (params.action === "packages") {
                const id = "mock.sample",
                    version = (params.packageVersions as Record<string, string> | undefined)?.[id] ?? "1.10.0"
                const item = {
                    id,
                    version,
                    hash: "c".repeat(64),
                    description: "Browser simulation",
                    authors: "Mock author",
                    tags: ["Simulation"],
                    committedAt: mockCommittedAt[version] ?? null,
                    iconKey: null,
                    functionCount: 2,
                }
                const searchTexts = [
                    item.id,
                    item.description,
                    item.authors,
                    ...item.tags,
                    `${source.kind === "custom" ? "Team" : "Mock"}.Sample.First`,
                    `${source.kind === "custom" ? "Team" : "Mock"}.Sample.Second`,
                ]
                const items = searchTexts.some((text) => matchUdfName(text, String(params.query ?? "")) !== null) ? [item] : []
                const offset = Number(params.offset ?? 0),
                    limit = Number(params.limit ?? 50),
                    visible = items.slice(offset, offset + limit)
                return {
                    packagePage: {
                        items: visible,
                        total: items.length,
                        nextOffset: offset + visible.length < items.length ? offset + visible.length : null,
                        status: copy(status),
                    },
                }
            }
            if (params.action === "icon") return { icon: { key: String(params.iconKey), dataUrl: null } }
            return { package: pkg }
        }
        if (method === "udf.localList") return { items: copy(this.locals) }
        if (method === "udf.localGet") {
            const item = this.locals.find((entry) => entry.id === params.id)
            if (!item) throw new Error("UDF_NOT_FOUND")
            return copy(item)
        }
        if (method === "udf.localSave") {
            const previous = this.locals.find((entry) => entry.id === params.id)
            if (params.id && (!previous || previous.baseline !== params.baseline)) throw new Error("UDF_BASELINE_CHANGED")
            const definition = params.definition as LocalUdfDefinition
            if (
                this.locals.some(
                    (entry) =>
                        entry.id !== previous?.id &&
                        entry.definition.name.toLowerCase() === definition.name.toLowerCase() &&
                        entry.definition.version === definition.version,
                )
            )
                throw new Error("UDF_LOCAL_VERSION_EXISTS")
            const item = {
                id: previous?.id ?? crypto.randomUUID().replaceAll("-", ""),
                baseline: `${crypto.randomUUID().replaceAll("-", "")}${crypto.randomUUID().replaceAll("-", "")}`,
                definition: copy(params.definition as LocalUdfDefinition),
            }
            this.locals = [...this.locals.filter((entry) => entry.id !== item.id), item]
            return copy(item)
        }
        if (method === "udf.localDelete") {
            const previous = this.locals.find((entry) => entry.id === params.id)
            if (!previous || previous.baseline !== params.baseline || params.confirm !== true) throw new Error("UDF_BASELINE_CHANGED")
            this.locals = this.locals.filter((entry) => entry.id !== previous.id)
            return { deleted: true }
        }
        throw new Error("UNKNOWN_METHOD")
    }
}
