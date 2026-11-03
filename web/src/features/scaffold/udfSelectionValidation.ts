/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfSelectionValidation.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式选择前校验精确包与同源依赖版本, 整批成功后才允许更新草稿.
 */
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
import type { LocalUdfEntry, UdfCatalogStatus, UdfPackage, UdfSelection } from "./contracts"
/** 复用业务选择预算, 不裁剪超限草稿. */
export function withinUdfSelectionBudget(items: UdfSelection[]): boolean {
    return (
        items.length <= 128 &&
        items.every((item) => item.functions.length > 0 && item.functions.length <= 128) &&
        items.reduce((total, item) => total + item.functions.length, 0) <= 8192 &&
        new TextEncoder().encode(JSON.stringify(items)).length <= 60 * 1024
    )
}
/** 校验选择和递归依赖; source+package 唯一版本, 失败不修改调用方状态, 服务端仍执行权威核验. */
export async function validateUdfSelections(
    request: UdfRequest,
    selections: UdfSelection[],
    statuses: Record<string, UdfCatalogStatus>,
    invalid: () => Error,
    options: { current?: () => boolean; cancelled?: () => Error } = {},
): Promise<Record<string, UdfPackage>> {
    if (!withinUdfSelectionBudget(selections)) throw invalid()
    const packages: Record<string, UdfPackage> = {},
        visiting = new Set<string>(),
        localVisiting = new Set<string>(),
        localDone = new Set<string>(),
        functionOwners = new Map<string, string>(),
        checked = new Set<string>(),
        localEntries = new Map<string, LocalUdfEntry>(),
        sourceOrigins = new Map<string, string | null>()
    /** 纯缓存遍历同样观察所属操作, 取消不能等到下一次宿主读取才生效. */
    function ensureCurrent(): void {
        if (options.current?.() === false) throw options.cancelled?.() ?? invalid()
    }
    /** 精确读取同源依赖, 包标识不区分大小写, 函数内容身份保留原文. */
    async function visit(source: string, id: string, version: string, sourceBaseline: string | null | undefined): Promise<UdfPackage> {
        ensureCurrent()
        const key = `${source}/${id.toLowerCase()}`,
            known = packages[key]
        if (sourceOrigins.has(source) && sourceOrigins.get(source) !== (sourceBaseline ?? null)) throw invalid()
        sourceOrigins.set(source, sourceBaseline ?? null)
        if (visiting.has(key) || (known && known.version !== version) || (Object.keys(packages).length + localDone.size >= 128 && !known)) throw invalid()
        if (known && checked.has(key)) return known
        const status = statuses[source]
        if (!status?.initialized || !status.baseline) throw invalid()
        const result = await request("udf.catalog", { action: "package", source, sourceBaseline, catalogBaseline: status.baseline, packageId: id, version })
        const pkg = result.package
        if (!pkg || pkg.id.toLowerCase() !== id.toLowerCase() || pkg.version !== version) throw invalid()
        packages[key] = pkg
        visiting.add(key)
        for (const fn of pkg.functions) {
            const owner = functionOwners.get(fn.name.toLowerCase())
            if (owner && owner !== key) throw invalid()
            functionOwners.set(fn.name.toLowerCase(), key)
        }
        // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
        for (const dependency of pkg.dependencies) await visit(source, dependency.id, dependency.version, sourceBaseline)
        visiting.delete(key)
        checked.add(key)
        return pkg
    }
    /** 本地定义继续使用宿主稳定ID/hash, 其声明依赖也参与同一次版本冲突检查. */
    async function visitLocal(id: string, baseline: string | null): Promise<LocalUdfEntry> {
        ensureCurrent()
        if (localVisiting.has(id)) throw invalid()
        const item = localEntries.get(id) ?? (await request("udf.localGet", { id }))
        localEntries.set(id, item)
        if (baseline && item.baseline !== baseline) throw invalid()
        if (localDone.has(id)) return item
        if (!localDone.has(id) && Object.keys(packages).length + localDone.size >= 128) throw invalid()
        localVisiting.add(id)
        const owner = functionOwners.get(item.definition.name.toLowerCase())
        if (owner && owner !== `local/${id}`) throw invalid()
        functionOwners.set(item.definition.name.toLowerCase(), `local/${id}`)
        for (const dependency of item.definition.dependencies) {
            if (dependency.source === "local") {
                // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
                const local = await visitLocal(dependency.packageId, dependency.baseline)
                if (local.definition.name !== dependency.function || local.definition.version !== dependency.version) throw invalid()
            } else {
                if (!statuses[dependency.source]) {
                    // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
                    const status = (await request("udf.catalog", { action: "status", source: dependency.source, sourceBaseline: dependency.sourceBaseline }))
                        .status
                    if (!status) throw invalid()
                    statuses = { ...statuses, [dependency.source]: status }
                }
                // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
                const pkg = await visit(dependency.source, dependency.packageId, dependency.version, dependency.sourceBaseline)
                if ((dependency.baseline && dependency.baseline !== pkg.hash) || !pkg.functions.some((fn) => fn.name === dependency.function)) throw invalid()
            }
        }
        localVisiting.delete(id)
        localDone.add(id)
        return item
    }
    for (const selection of selections) {
        ensureCurrent()
        if (selection.source === "local") {
            // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
            const item = await visitLocal(selection.packageId, selection.baseline)
            if (selection.version !== item.definition.version || selection.functions.length !== 1 || selection.functions[0] !== item.definition.name)
                throw invalid()
        } else {
            const status = statuses[selection.source]
            if (!status?.initialized || selection.catalogBaseline !== status.baseline || !selection.version || !selection.baseline) throw invalid()
            // oxlint-disable-next-line no-await-in-loop -- 依赖闭包存在顺序依赖, 复用唯一取消身份并首错停止.
            const pkg = await visit(selection.source, selection.packageId, selection.version, selection.sourceBaseline)
            if (selection.baseline !== pkg.hash || selection.functions.some((name) => !pkg.functions.some((fn) => fn.name === name))) throw invalid()
        }
    }
    return packages
}
