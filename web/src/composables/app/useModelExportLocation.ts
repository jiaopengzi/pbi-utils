/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelExportLocation.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 读取宿主最近成功导出位置并只按记录身份打开, 保护取消和卸载后的迟到结果.
 */
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue"
import { BridgeError, type BridgeClient, type ModelExportLocation } from "@/bridge/client"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { useI18n } from "@/composables/i18n/useI18n"
import { modelReadCache } from "@/features/model/modelReadCache"

/** 路径只供展示; 读取失败不改变已成功发布文件的事实, 打开不提交任何网页路径. */
export function useModelExportLocation(props: { bridge: BridgeClient; locked?: boolean }) {
    const { t, hostText } = useI18n(),
        { showFeedback } = useFeedback()
    const cache = modelReadCache(props.bridge)
    const location = shallowRef<ModelExportLocation | undefined>(cache.exportLocation),
        busy = ref(false),
        opening = ref(false),
        committing = ref(false),
        cancelling = ref(false)
    let disposed = false,
        generation = 0,
        attemptedRevision = -1,
        active: { id: string; bridge: BridgeClient } | undefined
    /** 尝试取消当前目录动作, 原请求仍负责最终排空. */
    async function cancel(): Promise<void> {
        if (!active || committing.value || cancelling.value) return
        const request = active
        cancelling.value = true
        try {
            const response = await request.bridge.request("request.cancel", { requestId: request.id }).promise
            if (active === request && !response.accepted) cancelling.value = false
        } catch {
            if (active === request) cancelling.value = false
        }
    }
    useOperationMessage(() =>
        opening.value
            ? { message: t("model.exportDirectoryOpening"), cancel, cancelLabel: t("model.cancel"), cancelDisabled: committing.value || cancelling.value }
            : undefined,
    )
    /** 从宿主读取最新记录; 自动挂载及导出后读取只更新位置, 不重放历史警告. */
    async function refresh(notify = true): Promise<void> {
        if (disposed || busy.value) return
        const epoch = ++generation,
            revision = cache.revision.value
        attemptedRevision = revision
        // 旧显示可留在当前页, 但失败后重挂必须重新核验, 不能把先前位置当作本次读取成功.
        cache.exportLocation = undefined
        busy.value = true
        try {
            const request = props.bridge.request("model.exportLocation", {})
            active = { id: request.id, bridge: props.bridge }
            const response = await request.promise
            if (!disposed && epoch === generation && revision === cache.revision.value) {
                location.value = response
                cache.exportLocation = response
                if (notify && response.warning) showFeedback({ message: hostText(response.warning), type: "warning" })
            }
        } catch (error) {
            if (!disposed && epoch === generation && revision === cache.revision.value)
                showFeedback({ message: error instanceof Error ? hostText(error.message) : t("model.exportLocationFailed"), type: "warning" })
        } finally {
            active = undefined
            busy.value = false
        }
    }
    /** 以当前展示记录id发起受限系统打开; 外部记录变更后更新展示, 不自动重试打开新目标. */
    async function open(): Promise<void> {
        if (disposed || busy.value || props.locked || !location.value?.available || !location.value.id) return
        const epoch = ++generation
        busy.value = true
        opening.value = true
        committing.value = false
        cancelling.value = false
        try {
            const request = props.bridge.request("model.openExportDirectory", { id: location.value.id }, undefined, (value) => {
                if (!disposed && epoch === generation) {
                    if (value === "committing") committing.value = true
                    if (value === "canceling") cancelling.value = true
                }
            })
            active = { id: request.id, bridge: props.bridge }
            const result = await request.promise
            if (!disposed && epoch === generation && result.isSimulation) showFeedback({ message: t("model.exportSimulation"), type: "info" })
        } catch (error) {
            if (!disposed && epoch === generation) {
                showFeedback({ message: error instanceof Error ? hostText(error.message) : t("model.exportLocationFailed"), type: "warning" })
                if (error instanceof BridgeError && ["EXPORT_LOCATION_CHANGED", "EXPORT_LOCATION_UNAVAILABLE"].includes(error.code)) {
                    // 原打开已终结后只刷新记录, 不将另一个目录当成已获准目标自动打开.
                    busy.value = false
                    await refresh(false)
                }
            }
        } finally {
            active = undefined
            busy.value = false
            opening.value = false
            committing.value = false
            cancelling.value = false
        }
    }
    /** 刷新代际清除旧显示; 等待已有请求终态再读, 失败不循环重试, 重挂或显式操作可重试. */
    function readWhenReady(): void {
        if (!disposed && !busy.value && !location.value && attemptedRevision !== cache.revision.value) void refresh()
    }
    watch(cache.revision, () => (location.value = undefined), { flush: "sync" })
    watch([cache.revision, busy], readWhenReady, { flush: "post" })
    onMounted(readWhenReady)
    onBeforeUnmount(() => {
        disposed = true
        generation++
        void cancel()
    })
    return { location, busy, opening, refresh, open }
}
