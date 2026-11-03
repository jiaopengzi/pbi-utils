/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfRequests.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF请求串行、宿主提交阶段、取消排空及模态导航锁.
 */
import { ref, watch, onBeforeUnmount } from "vue"
import { BridgeError, type Methods } from "@/bridge/client"
import { useOperationMessage } from "@/composables/ui/useFeedback"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
/** 同一逻辑操作中的有类型请求, 失败或取消通过异常中止整批. */
export type UdfRequest = <M extends keyof Methods>(method: M, params: Methods[M]["params"]) => Promise<Methods[M]["result"]>
/** 同一选择器仅允许一条请求; 取消和卸载保持原请求终态边界.
 * @param state 当前挂载的请求、取消和模态状态.
 * @param emitBusy 向外层发布请求和模态占用.
 * @returns 类型化串行请求入口; 请求失败显示错误并返回 undefined.
 */
export function useUdfRequests(state: UdfState, emitBusy: (value: boolean) => void) {
    const {
        bridge,
        disabled,
        lifecycle,
        busy,
        requestId,
        cancelling,
        committed,
        error,
        editor,
        manager,
        deleteTarget,
        sourceManager,
        sourceDeleteTarget,
        bulkImport,
        revalidation,
    } = state
    const progress = ref<number>()
    const { t, hostText } = useI18n()
    // 应用模态期间锁定外层导航, 空闲主表单仍不持有业务请求.
    watch(
        () =>
            busy.value ||
            manager.value ||
            editor.value ||
            !!deleteTarget.value ||
            sourceManager.value ||
            !!sourceDeleteTarget.value ||
            bulkImport.value ||
            state.detailOpen.value ||
            state.localDetailOpen.value ||
            state.tagPickerOpen.value ||
            revalidation.value,
        (value) => emitBusy(value),
        { flush: "sync" },
    )
    useOperationMessage(() =>
        busy.value
            ? {
                  progress: progress.value,
                  message: committed.value
                      ? t("scaffold.committing")
                      : t(state.operation.value === "refresh" ? "scaffold.pullingLibraries" : "scaffold.working"),
                  cancel: cancel,
                  cancelLabel: t("scaffold.cancel"),
                  cancelDisabled: cancelling.value || committed.value,
              }
            : undefined,
    )
    /** 取消仅发出请求, 始终等原业务响应排空后解除 busy. */
    async function cancel(): Promise<void> {
        if (!requestId.value || cancelling.value || committed.value) return
        const pendingId = requestId.value,
            generation = lifecycle.callGeneration
        cancelling.value = true
        try {
            await bridge.request("request.cancel", { requestId: pendingId }).promise
        } catch (cause) {
            if (!lifecycle.disposed && busy.value && requestId.value === pendingId && lifecycle.callGeneration === generation)
                error.value =
                    cause instanceof BridgeError && cause.code === "UDF_CATALOG_UNCONFIRMED"
                        ? t("scaffold.catalogUnconfirmed")
                        : hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        }
    }
    /** 逻辑操作独占忙碌状态; 多个目录读取共享取消、排空与生命周期边界. */
    async function run<T>(work: (request: UdfRequest) => Promise<T>, allowDisabled = false, quietCodes: readonly string[] = []): Promise<T | undefined> {
        if (
            busy.value ||
            (disabled() &&
                !allowDisabled &&
                !manager.value &&
                !editor.value &&
                !deleteTarget.value &&
                !sourceManager.value &&
                !sourceDeleteTarget.value &&
                !state.detailOpen.value &&
                !state.tagPickerOpen.value &&
                !bulkImport.value)
        )
            return
        lifecycle.callGeneration++
        busy.value = true
        cancelling.value = false
        committed.value = false
        progress.value = undefined
        state.lastErrorCode.value = ""
        error.value = ""
        try {
            const request: UdfRequest = async (method, params) => {
                if (lifecycle.disposed || cancelling.value) throw new BridgeError("CANCELLED", t("scaffold.cancel"))
                const pending = bridge.request(
                    method,
                    params,
                    (event) => {
                        if (lifecycle.disposed || method !== "udf.catalogRefresh" || event.type !== "scaffoldProgress") return
                        const data = event.payload as { percent?: unknown; canCancel?: unknown }
                        if (typeof data?.percent === "number" && Number.isFinite(data.percent))
                            progress.value = Math.max(progress.value ?? 0, Math.max(0, Math.min(99, Math.floor(data.percent))))
                        if (data?.canCancel === false) committed.value = true
                    },
                    (phase) => {
                        if (!lifecycle.disposed && phase === "committing") committed.value = true
                    },
                )
                requestId.value = pending.id
                const result = await pending.promise
                requestId.value = ""
                // 已提交写入的成功回执必须保留, 取消只中止尚未提交的只读或后续阶段.
                if (lifecycle.disposed || (cancelling.value && !committed.value)) throw new BridgeError("CANCELLED", t("scaffold.cancel"))
                return result
            }
            const result = await work(request)
            return lifecycle.disposed ? undefined : result
        } catch (cause) {
            if (!lifecycle.disposed) state.lastErrorCode.value = cause instanceof BridgeError ? cause.code : "EXECUTION_FAILED"
            if (
                !lifecycle.disposed &&
                !(cause instanceof BridgeError && quietCodes.includes(cause.code)) &&
                !(cause instanceof BridgeError && cause.code === "CANCELLED" && !cause.message.endsWith("本轮临时目录清理失败, 可能仍有临时文件."))
            )
                error.value =
                    cause instanceof BridgeError && cause.code === "UDF_CATALOG_UNCONFIRMED"
                        ? t("scaffold.catalogUnconfirmed")
                        : hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        } finally {
            requestId.value = ""
            busy.value = false
        }
    }
    /** 单个请求复用整批入口, 失败仍返回 undefined 并保留原显示结果. */
    async function call<M extends keyof Methods>(
        method: M,
        params: Methods[M]["params"],
        allowDisabled = false,
        quietCodes: readonly string[] = [],
    ): Promise<Methods[M]["result"] | undefined> {
        return await run((request) => request(method, params), allowDisabled, quietCodes)
    }
    onBeforeUnmount(() => {
        lifecycle.disposed = true
        if (requestId.value && !committed.value) void bridge.request("request.cancel", { requestId: requestId.value }).promise.catch(() => undefined)
    })
    const validationOptions = { current: () => !lifecycle.disposed && !cancelling.value, cancelled: () => new BridgeError("CANCELLED", t("scaffold.cancel")) }
    return { call, run, cancel, validationOptions }
}
export type UdfRequests = ReturnType<typeof useUdfRequests>
