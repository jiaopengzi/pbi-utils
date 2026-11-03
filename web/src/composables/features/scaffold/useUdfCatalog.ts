/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfCatalog.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按页读取轻量包摘要, 按需详情和逐库精确tag选择, 保留跨页草稿.
 */
import { computed, onBeforeUnmount, watch } from "vue"
import type { UdfRequests } from "./useUdfRequests"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "./useUdfState"
import { readUdfPackagePage } from "@/features/scaffold/udfPackagePages"
import { useUdfPackageSelection } from "./useUdfPackageSelection"
/** 缓存图标只生成一个本地对象URL, 切代/来源或卸载立即回收, 行内不重复base64正文. */
function releaseIcons(values: Record<string, string | null>, keep: Record<string, string | null> = {}): void {
    for (const url of Object.values(values)) if (url?.startsWith("blob:") && !Object.values(keep).includes(url)) URL.revokeObjectURL(url)
}
/** 已选条目的所有身份/版本错误均关联其自身行. */
function selectionFields(_row: number): string[] {
    return ["source", "sourceBaseline", "catalogBaseline", "functions", "baseline", "version"]
}
/** 函数分页和选择均绑定宿主来源/目录基线, 失败只保留原草稿, 不产生部分选择. */
export function useUdfCatalog(state: UdfState, requests: UdfRequests) {
    const { t } = useI18n()
    const totalSelected = computed(() => state.model.value.reduce((total, item) => total + item.functions.length, 0))
    let queryTimer: ReturnType<typeof setTimeout> | undefined
    let composing = false,
        searchPending = false,
        loadingQuery = ""
    watch(state.icons, (next, previous) => releaseIcons(previous, next), { flush: "sync" })
    /** 获取当前显示页, 只补足受传输字节预算限制的短页, 不枚举其余包或正文. */
    async function load(): Promise<boolean> {
        // 未确认提交允许只读核验当前缓存; 搜索和选择动作仍由各自 guard 拒绝.
        if (!state.currentSource.value || state.source.value === "local") return false
        const source = state.source.value,
            origin = { ...state.catalogReadOrigin.value },
            requestedPage = state.page.value,
            requestedQuery = state.query.value,
            requestedPrerelease = state.prerelease.value
        state.searchQuery.value = requestedQuery
        const packageVersions = Object.fromEntries(
            Object.entries(state.packageTags.value).filter(([, version]) => requestedPrerelease || !version.split("+")[0]!.includes("-")),
        )
        for (const selection of state.model.value)
            if (selection.source === source) {
                if (
                    selection.catalogBaseline === state.catalogStatus.value?.baseline &&
                    selection.version &&
                    (requestedPrerelease || !selection.version.split("+")[0]!.includes("-")) &&
                    state.tagOptions.value[selection.packageId]?.includes(selection.version) &&
                    !packageVersions[selection.packageId]
                )
                    packageVersions[selection.packageId] = selection.version
            }
        const result = await requests.run(
            async (request) => {
                let page = await readUdfPackagePage(
                    request,
                    origin,
                    {
                        query: requestedQuery,
                        includePrerelease: requestedPrerelease,
                        offset: (requestedPage - 1) * state.pageSize.value,
                        limit: state.pageSize.value,
                        packageVersions,
                    },
                    () => new Error(t("scaffold.functionPageInvalid")),
                )
                const restoredTags: Record<string, string[]> = {}
                let restored = false
                // 先用包页见证同一目录代, 再恢复已选历史tag; 跨代旧tag不进入查询覆盖.
                for (const selection of state.model.value) {
                    if (
                        selection.source !== source ||
                        selection.catalogBaseline !== page.status.baseline ||
                        !selection.version ||
                        (!requestedPrerelease && selection.version.split("+")[0]!.includes("-")) ||
                        packageVersions[selection.packageId]
                    )
                        continue
                    let tags = state.tagOptions.value[selection.packageId]
                    if (!tags) {
                        // oxlint-disable-next-line no-await-in-loop -- 仅对已有显式选择逐库核标签, 共用本次取消身份和同代见证.
                        const response = await request("udf.catalog", {
                            action: "versions",
                            ...origin,
                            catalogBaseline: page.status.baseline,
                            packageId: selection.packageId,
                        })
                        tags = response.versions ?? []
                    }
                    restoredTags[selection.packageId] = tags
                    if (tags.includes(selection.version)) {
                        packageVersions[selection.packageId] = selection.version
                        restored = true
                    }
                }
                if (restored)
                    page = await readUdfPackagePage(
                        request,
                        { ...origin, catalogBaseline: page.status.baseline },
                        {
                            query: requestedQuery,
                            includePrerelease: requestedPrerelease,
                            offset: (requestedPage - 1) * state.pageSize.value,
                            limit: state.pageSize.value,
                            packageVersions,
                        },
                        () => new Error(t("scaffold.functionPageInvalid")),
                    )
                const icons: Record<string, string | null> = {}
                for (const library of page.items) {
                    if (!library.iconKey || Object.hasOwn(state.icons.value, library.iconKey) || Object.hasOwn(icons, library.iconKey)) continue
                    // oxlint-disable-next-line no-await-in-loop -- 同一逻辑读操作只持有一个取消身份, 同页去重后逐个读取.
                    const response = await request("udf.catalog", {
                        action: "icon",
                        ...origin,
                        catalogBaseline: page.status.baseline,
                        packageId: library.id,
                        version: library.version,
                        iconKey: library.iconKey,
                    })
                    if (!response.icon || response.icon.key !== library.iconKey) throw new Error(t("scaffold.functionPageInvalid"))
                    const data = response.icon.dataUrl
                    if (data && (!data.startsWith("data:image/png;base64,") || data.length > 175000)) throw new Error(t("scaffold.functionPageInvalid"))
                    icons[library.iconKey] = data
                }
                return { page, icons, restoredTags }
            },
            true,
            ["UDF_CATALOG_UNINITIALIZED", "UDF_CATALOG_UPDATE_REQUIRED"],
        )
        if (state.lifecycle.disposed || source !== state.source.value || requestedQuery !== state.query.value || requestedPrerelease !== state.prerelease.value)
            return false
        if (!result && ["UDF_CATALOG_UNINITIALIZED", "UDF_CATALOG_UPDATE_REQUIRED"].includes(state.lastErrorCode.value)) {
            const empty = { initialized: false, baseline: null, retrievedAt: null, packageCount: 0, versionCount: 0, functionCount: 0 }
            state.catalogStatus.value = empty
            state.statuses.value = { ...state.statuses.value, [source]: empty }
            state.packageItems.value = []
            state.total.value = 0
            return true
        }
        if (
            !result ||
            source !== state.source.value ||
            origin.sourceBaseline !== state.catalogReadOrigin.value.sourceBaseline ||
            origin.catalogBaseline !== state.catalogStatus.value?.baseline
        )
            return false
        state.packageItems.value = result.page.items
        state.catalogStatus.value = result.page.status
        state.statuses.value = { ...state.statuses.value, [source]: result.page.status }
        state.packageTags.value = packageVersions
        state.tagOptions.value = { ...state.tagOptions.value, ...result.restoredTags }
        state.total.value = result.page.total
        const iconUrls: Record<string, string | null> = {}
        for (const [key, data] of Object.entries(result.icons)) {
            if (!data) {
                iconUrls[key] = null
                continue
            }
            const bytes = Uint8Array.from(atob(data.slice("data:image/png;base64,".length)), (character) => character.charCodeAt(0))
            if (bytes.length > 131072) continue
            iconUrls[key] = URL.createObjectURL(new Blob([bytes], { type: "image/png" }))
        }
        state.icons.value = { ...state.icons.value, ...iconUrls }
        state.catalogCache.value = true
        state.catalogRetrievedAt.value = result.page.status.retrievedAt
        state.searched.value = true
        return true
    }
    /** 搜索排空前继续接收输入, 仅最新查询可更新列表; 其他业务禁用仍拒绝事件. */
    async function search(): Promise<void> {
        if (composing || state.lifecycle.disposed || state.uncertain.value) return
        if (state.busy.value) {
            searchPending = !state.searching.value || state.query.value !== loadingQuery
            return
        }
        if (state.disabled()) return
        if (queryTimer) clearTimeout(queryTimer)
        queryTimer = undefined
        searchPending = false
        state.page.value = 1
        state.searchQuery.value = state.query.value
        if (state.source.value === "local") return
        loadingQuery = state.query.value
        state.searching.value = true
        try {
            await load()
        } finally {
            state.searching.value = false
            if (searchPending && !queryTimer && !state.lifecycle.disposed) scheduleSearch()
        }
    }
    /** 页码和大小仅接受产品选项; 翻页不会清除选择. */
    async function changePage(page: number): Promise<void> {
        if (state.locked.value || !Number.isInteger(page) || page < 1 || page > Math.max(1, Math.ceil(state.total.value / state.pageSize.value))) return
        state.page.value = page
        await load()
    }
    async function changePageSize(size: number): Promise<void> {
        if (state.locked.value || ![50, 100, 200, 500].includes(size)) return
        state.pageSize.value = size
        state.page.value = 1
        await load()
    }
    /** 输入结束后的防抖任务绑定当前来源, 切源/卸载不会重放旧查询. */
    function scheduleSearch(): void {
        if (queryTimer) clearTimeout(queryTimer)
        if (composing) return
        const source = state.source.value
        queryTimer = setTimeout(() => {
            queryTimer = undefined
            if (!state.lifecycle.disposed && source === state.source.value) void search()
        }, 180)
    }
    /** 组合输入期间保留草稿和焦点, 结束后才启动一次名称查询. */
    function compositionStart(): void {
        composing = true
        if (queryTimer) clearTimeout(queryTimer)
        queryTimer = undefined
    }
    function compositionEnd(): void {
        composing = false
        scheduleSearch()
    }
    watch(state.query, scheduleSearch)
    // 防抖到时可能遇到选择/详情等已有串行操作, 排空后继续最新查询, 不取消用户正在确认的选择.
    watch(state.busy, (busy) => {
        if (!busy && searchPending && !queryTimer && !state.lifecycle.disposed) scheduleSearch()
    })
    watch(state.source, () => {
        if (queryTimer) clearTimeout(queryTimer)
        queryTimer = undefined
        searchPending = false
        composing = false
        state.searchQuery.value = ""
    })
    onBeforeUnmount(() => {
        if (queryTimer) clearTimeout(queryTimer)
        releaseIcons(state.icons.value)
    })
    /** 仅展开版本列表时读取一次精确标签, 不在初始列表逐库加载. */
    async function loadTags(id: string): Promise<void> {
        if (state.locked.value) return
        state.tagPickerId.value = id
        state.tagPickerValue.value =
            state.packageTags.value[id] ??
            (state.packageItems.value
                .find((item) => item.id === id)
                ?.version.split("+")[0]!
                .includes("-")
                ? ""
                : "latest")
        state.tagPickerOpen.value = true
        if (state.tagOptions.value[id]) return
        const result = await requests.call("udf.catalog", { action: "versions", ...state.catalogReadOrigin.value, packageId: id })
        if (result?.versions) state.tagOptions.value = { ...state.tagOptions.value, [id]: result.versions }
    }
    const selection = useUdfPackageSelection(state, requests, load)
    return {
        load,
        search,
        compositionStart,
        compositionEnd,
        changePage,
        changePageSize,
        loadTags,
        ...selection,
        totalSelected,
        selectionFields,
    }
}
export type UdfCatalog = ReturnType<typeof useUdfCatalog>
