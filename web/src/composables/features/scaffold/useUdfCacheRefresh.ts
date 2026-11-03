/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfCacheRefresh.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式更新唯一当前缓存与只读核验, 未确认提交仅允许只读核验, 不重放写操作.
 */
import { computed, ref } from "vue"
import { useFeedback } from "@/composables/ui/useFeedback"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfCatalogStatus } from "@/features/scaffold/contracts"
import type { UdfState } from "./useUdfState"
import type { UdfRequests } from "./useUdfRequests"
/** 更新只影响该来源目录, 不写本地函数或模型; 发布后读回失败和提交前失败分别反馈. */
export function useUdfCacheRefresh(state: UdfState, requests: UdfRequests, readPage: () => Promise<boolean>, preserve: () => Promise<boolean>) {
    const { t } = useI18n(),
        { showFeedback } = useFeedback()
    const verifying = ref(false)
    const cleanupFailed = ref(false)
    const failureMessage = computed(() => t(cleanupFailed.value ? "scaffold.catalogCleanupFailed" : "scaffold.catalogUnconfirmed"))
    /** 已确认提交的清理失败独立保留, 后续只读失败附加原因而不改称未知提交. */
    function retainCleanupFailure(verified: boolean): void {
        if (!cleanupFailed.value) return
        const detail = !verified ? state.error.value : ""
        state.error.value = `${t("scaffold.catalogCleanupFailed")}${detail ? ` ${detail}` : ""}`
    }
    /** 接纳权威当前事实后撤销旧页, 防止读回失败仍操作旧行. */
    function accept(status: UdfCatalogStatus): void {
        state.catalogStatus.value = status
        state.statuses.value = { ...state.statuses.value, [state.source.value]: status }
        state.packageItems.value = []
        state.total.value = 0
        state.packageTags.value = {}
        state.tagOptions.value = {}
        state.icons.value = {}
        state.page.value = 1
    }
    /** 仅完整读取当前状态确认真实指针; 不重放未知提交. */
    async function verify(): Promise<boolean> {
        if (state.busy.value || verifying.value || !state.currentSource.value || state.source.value === "local") return false
        verifying.value = true
        const result = await requests.run(async (request) => {
            const current = (await request("udf.catalog", { action: "status", ...state.catalogOrigin.value })).status
            if (!current) throw new Error(t("scaffold.catalogUnconfirmed"))
            return current
        }, true)
        verifying.value = false
        if (!result) {
            retainCleanupFailure(false)
            return false
        }
        accept(result)
        const loaded = !result.initialized || (await readPage())
        if (loaded) state.uncertain.value = false
        if (cleanupFailed.value) retainCleanupFailure(loaded)
        else
            showFeedback({
                message: t(loaded ? "scaffold.catalogVerified" : "scaffold.catalogReadFailed"),
                type: loaded ? "info" : "warning",
            })
        return loaded
    }
    /** 未确认提交撤销可操作事实, 用户可继续只读核验, 禁止再次更新. */
    async function failed(): Promise<void> {
        if (["UDF_CATALOG_UNCONFIRMED", "UDF_CATALOG_CLEANUP_FAILED"].includes(state.lastErrorCode.value)) {
            cleanupFailed.value = state.lastErrorCode.value === "UDF_CATALOG_CLEANUP_FAILED"
            state.uncertain.value = true
            delete state.statuses.value[state.source.value]
            state.catalogStatus.value = undefined
            state.packageItems.value = []
            state.error.value = cleanupFailed.value ? "" : t("scaffold.catalogUnconfirmed")
            await verify()
        } else if (state.lastErrorCode.value === "CANCELLED" && !state.error.value) showFeedback({ message: t("scaffold.catalogCancelled"), type: "info" })
    }
    /** 只有手动点击更新才请求远端, 失败保持原缓存, 成功与后续列表失败分别反馈. */
    async function refresh(): Promise<void> {
        if (state.locked.value || state.uncertain.value || !state.currentSource.value || state.source.value === "local") return
        state.operation.value = "refresh"
        cleanupFailed.value = false
        try {
            // 已提交清理错误由本 owner 在只读核验后统一反馈, 避免原始错误与核验消息相互覆盖.
            const result = await requests.call("udf.catalogRefresh", state.catalogOrigin.value, false, ["UDF_CATALOG_CLEANUP_FAILED"])
            if (!result) {
                await failed()
                return
            }
            accept(result)
            let loaded = await readPage()
            if (loaded && (await preserve())) loaded = await readPage()
            showFeedback({
                message: t(loaded ? "scaffold.catalogUpdated" : "scaffold.catalogReadFailed"),
                type: loaded ? "success" : "warning",
            })
        } finally {
            state.operation.value = "read"
        }
    }
    return { verifying, failureMessage, refresh, verify }
}
