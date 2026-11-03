/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfCache.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地目录状态, 显式刷新与变化后的选择重新核验.
 */
import { computed, ref } from "vue"
import type { UdfPackage, UdfSelection } from "@/features/scaffold/contracts"
import type { UdfState } from "./useUdfState"
import type { UdfRequests } from "./useUdfRequests"
import { readUdfDependencyPreview } from "@/features/scaffold/udfDependencyPreview"
import { latestStableUdfVersion } from "@/features/scaffold/udfVersions"
import { useI18n } from "@/composables/i18n/useI18n"
import { useUdfCacheRefresh } from "./useUdfCacheRefresh"
import { validateUdfSelections } from "@/features/scaffold/udfSelectionValidation"
export interface UdfRevalidationEntry {
    previous: UdfSelection
    previousVersion: string | null
    previousPackage?: UdfPackage
    next: UdfSelection
    package: UdfPackage
    dependencies: UdfPackage[]
    missing: string[]
}
/** 管理本地状态和显式更新; 过期选择保留原草稿直到用户审阅后确认.
 * @param state 当前选择器状态, 含按来源隔离的目录事实.
 * @param requests 串行请求入口, 负责取消和卸载保护.
 * @param search 原子读取本地卡片的动作.
 * @returns 状态读取、显式更新、核验预览与确认; 失败不改变原选择.
 */
export function useUdfCache(state: UdfState, requests: UdfRequests, search: () => Promise<boolean>) {
    const { t } = useI18n()
    const reviewing = state.revalidation,
        entries = ref<UdfRevalidationEntry[]>([])
    const stale = computed(() =>
        state.model.value.filter(
            (item) => item.source !== "local" && (!item.catalogBaseline || item.catalogBaseline !== state.statuses.value[item.source]?.baseline),
        ),
    )
    const valid = computed(() => !state.uncertain.value && !stale.value.length)
    const canConfirm = computed(
        () =>
            !state.busy.value &&
            entries.value.length > 0 &&
            entries.value.every((item) => !item.missing.length && item.next.catalogBaseline === state.statuses.value[item.next.source]?.baseline),
    )
    /** 本地状态读取允许在未启用步骤时执行, 不触发更新或网络. */
    async function load(): Promise<void> {
        if (!state.currentSource.value || state.source.value === "local") return
        delete state.statuses.value[state.source.value]
        state.catalogStatus.value = undefined
        await search()
    }
    /** 已知精确事实完全不变时只前移目录基线, 新版本或未知旧事实仍待明确审阅. */
    async function preserveUnchanged(): Promise<boolean> {
        const affected = stale.value.filter((item) => item.source === state.source.value)
        if (!affected.length) return false
        const result = await requests.run(async (request) => {
            const unchanged: UdfSelection[] = []
            await affected.reduce(async (pending, item) => {
                await pending
                const old = state.resolved.value[`${item.source}/${item.packageId}`]
                if ((!item.version && !old) || old?.dependencies.length) return
                const tags = await request("udf.catalog", { action: "versions", ...state.catalogReadOrigin.value, packageId: item.packageId })
                const tag = item.version ?? latestStableUdfVersion(tags.versions ?? [])
                if (!tag || !tags.versions?.includes(tag) || (!item.version && tag !== old?.version)) return
                const pkg = (await request("udf.catalog", { action: "package", ...state.catalogReadOrigin.value, packageId: item.packageId, version: tag }))
                    .package
                // 根包不变不能证明依赖闭包不变; 未持有旧闭包事实时必须明确重新审阅.
                if (
                    pkg &&
                    !pkg.dependencies.length &&
                    pkg.hash === (item.baseline ?? old?.hash) &&
                    item.functions.every((name) => pkg.functions.some((fn) => fn.name === name))
                )
                    unchanged.push(item)
            }, Promise.resolve())
            return unchanged
        })
        if (!result) return false
        state.model.value = state.model.value.map((item) => (result.includes(item) ? { ...item, catalogBaseline: state.catalogStatus.value?.baseline } : item))
        return result.length > 0
    }
    /** 读取全部已选远端来源状态, 不把当前来源的基线套到其他来源. */
    async function initialize(): Promise<void> {
        await load()
        await [...new Set(state.model.value.filter((item) => item.source !== "local" && item.source !== state.source.value).map((item) => item.source))].reduce(
            async (pending, id) => {
                await pending
                const origin = state.sources.value.find((item) => item.id === id)
                if (!origin) return
                delete state.statuses.value[id]
                const result = await requests.call(
                    "udf.catalog",
                    {
                        action: "packages",
                        query: "",
                        includePrerelease: false,
                        offset: 0,
                        limit: 1,
                        source: id,
                        ...(origin.kind === "custom" ? { sourceBaseline: origin.baseline } : {}),
                    },
                    true,
                    ["UDF_CATALOG_UNINITIALIZED"],
                )
                if (result?.packagePage) state.statuses.value = { ...state.statuses.value, [id]: result.packagePage.status }
            },
            Promise.resolve(),
        )
    }
    /** 从本地快照建立新旧包预览; 函数缺失必须显式移除后才能继续. */
    async function prepare(): Promise<void> {
        if (state.locked.value || !stale.value.length) return
        entries.value = []
        const original = [...stale.value]
        const result = await requests.run(async (request) => {
            const next: UdfRevalidationEntry[] = []
            const previewPackages = new Set<string>()
            await original.reduce(async (pending, item) => {
                await pending
                const origin = { source: item.source, ...(item.sourceBaseline ? { sourceBaseline: item.sourceBaseline } : {}) }
                const status = (await request("udf.catalog", { action: "status", ...origin })).status
                if (!status?.initialized) throw new Error(t("scaffold.catalogFirstUse"))
                const readOrigin = { ...origin, catalogBaseline: status.baseline }
                const tags = await request("udf.catalog", { action: "versions", ...readOrigin, packageId: item.packageId })
                const tag = item.version ?? latestStableUdfVersion(tags.versions ?? [])
                if (!tag) throw new Error(t("scaffold.functionUnavailable"))
                const pkg = (await request("udf.catalog", { action: "package", ...readOrigin, packageId: item.packageId, version: tag })).package
                if (!pkg) throw new Error(t("scaffold.functionUnavailable"))
                const dependencies = await readUdfDependencyPreview(request, readOrigin, pkg, () => new Error(t("scaffold.dependencyPreviewInvalid")))
                for (const entry of [pkg, ...dependencies]) previewPackages.add(`${item.source}/${entry.id.toLowerCase()}@${entry.version}`)
                if (previewPackages.size > 128) throw new Error(t("scaffold.dependencyPreviewInvalid"))
                state.statuses.value = { ...state.statuses.value, [item.source]: status }
                next.push({
                    previous: item,
                    previousPackage: state.resolved.value[`${item.source}/${item.packageId}`],
                    previousVersion: item.version ?? state.resolved.value[`${item.source}/${item.packageId}`]?.version ?? null,
                    next: { ...item, version: pkg.version, baseline: pkg.hash, catalogBaseline: status.baseline },
                    package: pkg,
                    dependencies,
                    missing: item.functions.filter((name) => !pkg.functions.some((fn) => fn.name === name)),
                })
            }, Promise.resolve())
            if (next.every((entry) => !entry.missing.length)) {
                const combined = state.model.value.map((item) => next.find((entry) => entry.previous === item)?.next ?? item)
                await validateUdfSelections(
                    request,
                    combined,
                    state.statuses.value,
                    () => new Error(t("scaffold.dependencyPreviewInvalid")),
                    requests.validationOptions,
                )
            }
            return next
        })
        if (!result) return
        entries.value = result
        reviewing.value = true
    }
    /** 确认前复核原选择与目录身份, 取消和失败均保留旧版本展示与基线. */
    async function confirm(): Promise<void> {
        if (!canConfirm.value || entries.value.some((entry) => !state.model.value.includes(entry.previous))) return
        state.model.value = state.model.value.map((item) => entries.value.find((entry) => entry.previous === item)?.next ?? item)
        for (const entry of entries.value) state.resolved.value[`${entry.next.source}/${entry.next.packageId}`] = entry.package
        reviewing.value = false
        await search()
    }
    /** 返回已核验的展示版本, 未知旧版本明确显示待核验而不猜测当前版本. */
    function versionLabel(item: UdfSelection): string {
        return item.version ?? state.resolved.value[`${item.source}/${item.packageId}`]?.version ?? t("scaffold.pendingVerification")
    }
    const refresh = useUdfCacheRefresh(state, requests, search, preserveUnchanged)
    return { load, initialize, ...refresh, stale, valid, reviewing, entries, canConfirm, prepare, confirm, versionLabel }
}
export type UdfCache = ReturnType<typeof useUdfCache>
