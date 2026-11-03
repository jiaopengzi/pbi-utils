/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfBulkImport.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 全来源精确tag函数预览和原子选择, 与本页批量范围区分并保持依赖/许可保护.
 */
import { computed, ref } from "vue"
import type { UdfPackage, UdfSelection } from "@/features/scaffold/contracts"
import type { UdfState } from "./useUdfState"
import type { UdfRequests } from "./useUdfRequests"
import { useI18n } from "@/composables/i18n/useI18n"
import { readUdfCatalogPages } from "@/features/scaffold/udfCatalogPages"
import { latestStableUdfVersion } from "@/features/scaffold/udfVersions"
import { validateUdfSelections, withinUdfSelectionBudget } from "@/features/scaffold/udfSelectionValidation"
import { groupLocalUdfPackages, localUdfPackageKey, preferredLocalUdfEntry } from "@/features/scaffold/udfLocalPackages"
export interface UdfBulkEntry {
    selection: UdfSelection
    package?: UdfPackage
}
/** 全来源行为保留明确预览, 不随搜索或分页缩小; 任一步失败不部分更新草稿. */
export function useUdfBulkImport(state: UdfState, requests: UdfRequests) {
    const { t } = useI18n(),
        entries = ref<UdfBulkEntry[]>([]),
        ready = ref(false),
        cancelled = ref(false)
    const origin = ref<{ source: string; sourceBaseline?: string | null; catalogBaseline?: string | null }>()
    const count = computed(() => entries.value.reduce((total, item) => total + item.selection.functions.length, 0))
    const canConfirm = computed(() => ready.value && entries.value.length > 0 && !state.busy.value && !state.uncertain.value)
    /** 全来源替换仅影响当前来源, 所有其他来源精确版本保持. */
    function nextSelection(own: UdfBulkEntry[] = entries.value): UdfSelection[] {
        return [...state.model.value.filter((item) => item.source !== origin.value?.source), ...own.map((item) => item.selection)]
    }
    /** 显式全来源预览允许有界整库读取, 日常函数分页不走此路径. */
    async function prepare(): Promise<void> {
        if (state.locked.value || state.uncertain.value || !state.currentSource.value) return
        ready.value = false
        cancelled.value = false
        entries.value = []
        origin.value = { ...state.catalogReadOrigin.value }
        state.bulkImport.value = true
        const selectedOrigin = origin.value
        const result = await requests.run(async (request) => {
            const own: UdfBulkEntry[] = []
            if (selectedOrigin.source === "local") {
                const locals = await request("udf.localList", {})
                for (const group of groupLocalUdfPackages(locals.items)) {
                    const selected = state.model.value.find(
                        (selection) => selection.source === "local" && selection.functions.some((name) => localUdfPackageKey(name) === group.key),
                    )
                    // 与列表一致采用显式当前 tag; 新保存版本可以展示而不改旧选择, 全量确认才替换该逻辑包.
                    const current = group.versions.find((item) => item.id === state.localPackageTags.value[group.key])
                    if (!current && selected && !group.versions.some((item) => item.id === selected.packageId)) throw new Error(t("scaffold.bulkInvalid"))
                    const item = preferredLocalUdfEntry(group, current?.id ?? selected?.packageId)
                    if (selected && item.id === selected.packageId && (selected.version !== item.definition.version || selected.baseline !== item.baseline))
                        throw new Error(t("scaffold.bulkInvalid"))
                    own.push({
                        selection: {
                            source: "local",
                            packageId: item.id,
                            version: item.definition.version,
                            functions: [item.definition.name],
                            baseline: item.baseline,
                        },
                    })
                }
            } else {
                const catalog = await readUdfCatalogPages(request, selectedOrigin, false, () => new Error(t("scaffold.bulkInvalid")))
                for (const summary of catalog.packages ?? []) {
                    // oxlint-disable-next-line no-await-in-loop -- 整批共享取消身份, 精确tag必须先于对应包读取并首错停止.
                    const versions = (await request("udf.catalog", { action: "versions", ...selectedOrigin, packageId: summary.id })).versions ?? []
                    const version = state.packageTags.value[summary.id] ?? latestStableUdfVersion(versions)
                    if (!version) throw new Error(t("scaffold.bulkNoStable", { name: summary.id }))
                    // oxlint-disable-next-line no-await-in-loop -- 按已核验tag逐包累计预算, 不并发发布或部分选择.
                    const pkg = (await request("udf.catalog", { action: "package", ...selectedOrigin, packageId: summary.id, version })).package
                    if (!pkg || pkg.version !== version || pkg.id.toLowerCase() !== summary.id.toLowerCase()) throw new Error(t("scaffold.bulkInvalid"))
                    own.push({
                        package: pkg,
                        selection: {
                            ...selectedOrigin,
                            packageId: pkg.id,
                            version: pkg.version,
                            baseline: pkg.hash,
                            functions: pkg.functions.map((fn) => fn.name),
                        },
                    })
                    if (!withinUdfSelectionBudget(nextSelection(own))) throw new Error(t("scaffold.bulkLimit"))
                }
            }
            await validateUdfSelections(
                request,
                nextSelection(own),
                state.statuses.value,
                () => new Error(t("scaffold.dependencyPreviewInvalid")),
                requests.validationOptions,
            )
            return own
        })
        if (state.lifecycle.disposed) return
        if (!result) {
            cancelled.value = !state.error.value
            return
        }
        entries.value = result
        ready.value = withinUdfSelectionBudget(nextSelection())
        if (!ready.value) state.error.value = t("scaffold.bulkLimit")
    }
    /** 确认再次复核基线和精确选择, 精确依赖整批通过后一次替换当前来源. */
    async function confirm(): Promise<void> {
        if (
            !canConfirm.value ||
            !origin.value ||
            state.source.value !== origin.value.source ||
            state.catalogOrigin.value.sourceBaseline !== origin.value.sourceBaseline ||
            state.catalogReadOrigin.value.catalogBaseline !== origin.value.catalogBaseline
        )
            return
        const next = nextSelection()
        const resolved = await requests.run((request) =>
            validateUdfSelections(request, next, state.statuses.value, () => new Error(t("scaffold.dependencyPreviewInvalid")), requests.validationOptions),
        )
        if (!resolved) return
        state.resolved.value = { ...state.resolved.value, ...resolved }
        state.model.value = next
        state.bulkImport.value = false
    }
    return { entries, ready, cancelled, count, canConfirm, prepare, confirm }
}
export type UdfBulkImport = ReturnType<typeof useUdfBulkImport>
