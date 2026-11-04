/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelTableDeletion.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 冻结所选表和完整基线, 复用共享编辑保存通道并等待取消与迟到请求排空.
 */
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue"
import { BridgeError, type BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { hasPendingModelUpdate, isModelUpdateComplete, isReadOnlyProject } from "@/bridge/modelAccess"
import { readModelForOperation, sameModelTarget } from "@/features/model/operationFreshness"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

type Props = { project: ProjectSnapshot; bridge: BridgeClient; locked?: boolean }

/**
 * 删除 getTables 返回的精确表集合; getBaseline 为用户最后选择时的基线, 变化必须重新选择.
 * 返回可写状态、busy 和删除动作; onUpdated 只接收同一目标的权威结果, 失败保留选择.
 */
export function useModelTableDeletion(
    props: Props,
    getTables: () => string[],
    getBaseline: () => string,
    isLocked: () => boolean,
    onUpdated: (project: ProjectSnapshot) => void,
) {
    const { t, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const busy = ref(false),
        cancelling = ref(false),
        committing = ref(false)
    const active = shallowRef<{ id: string; bridge: BridgeClient }>()
    const writable = computed(() => !isReadOnlyProject(props.project))
    let disposed = false,
        generation = 0,
        stopRequested = false
    const identity = () =>
        JSON.stringify([
            props.project.projectId,
            props.project.projectPath,
            props.project.connection?.kind,
            props.project.connection?.instanceId,
            props.project.connection?.modelId,
        ])

    /** 取消只请求停止提交前准备, 接受取消仍由原请求终态解除 busy. */
    async function cancel(): Promise<void> {
        if (!active.value || cancelling.value || committing.value) return
        stopRequested = true
        cancelling.value = true
        const request = active.value
        try {
            const result = await request.bridge.request("request.cancel", { requestId: request.id }).promise
            if (active.value === request && !result.accepted) cancelling.value = false
        } catch {
            if (active.value === request) cancelling.value = false
        }
    }
    useOperationMessage(() =>
        busy.value
            ? {
                  message: t(cancelling.value ? "model.tableDeleteCancelling" : "model.tableDeleteProcessing"),
                  cancel: active.value ? cancel : undefined,
                  cancelLabel: t("model.cancel"),
                  cancelDisabled: cancelling.value || committing.value,
              }
            : undefined,
    )
    watch(identity, () => {
        generation++
        void cancel()
    })

    /** 整批只发一次 model.edit, 不把破坏性选择自动改绑到刷新后的模型基线或重放失败提交. */
    async function deleteSelected(): Promise<void> {
        if (disposed || busy.value || isLocked() || !writable.value || hasPendingModelUpdate(props.project)) return
        const tables = [...getTables()]
        if (!tables.length) return
        const original = props.project,
            bridge = props.bridge,
            target = identity(),
            epoch = ++generation
        let writing = false
        stopRequested = false
        const current = () => !disposed && epoch === generation && target === identity()
        try {
            const value = JSON.stringify({ projectId: original.projectId, baselineId: getBaseline(), tables })
            if (tables.length > 2000 || value.length > 30000) throw new Error(t("model.tableDeleteBounds"))
            if (getBaseline() !== original.baselineId) throw new Error(t("model.tableDeleteStale"))
            busy.value = true
            cancelling.value = false
            committing.value = false
            const fresh = await readModelForOperation(bridge, original, (id) => (active.value = { id, bridge }))
            if (!current() || stopRequested || !sameModelTarget(original, props.project)) return
            if (fresh.baselineId !== original.baselineId) {
                onUpdated(fresh)
                throw new Error(t("model.tableDeleteStale"))
            }
            writing = true
            const request = bridge.request(
                "model.edit",
                { operation: "table.deleteBatch", objectName: "tables", property: "items", value, table: "" },
                undefined,
                (phase) => {
                    if (current() && phase === "committing") committing.value = true
                },
            )
            active.value = { id: request.id, bridge }
            const snapshot = await request.promise
            if (!current()) return
            onUpdated(snapshot)
            if (isModelUpdateComplete(snapshot))
                showFeedback({
                    message: snapshot.saveResult?.message ? hostText(snapshot.saveResult.message) : t("model.tableDeleteSimulation"),
                    type: snapshot.saveResult ? "success" : "info",
                })
        } catch (error) {
            if (!current()) return
            const cancelled = error instanceof BridgeError && error.code === "CANCELLED"
            showFeedback({ message: error instanceof Error ? hostText(error.message) : t("model.saveFailed"), type: cancelled ? "warning" : "error" })
            if (writing) {
                try {
                    const snapshot = await bridge.request("project.inspect", {}).promise
                    if (current() && sameModelTarget(original, snapshot)) onUpdated(snapshot)
                } catch {
                    /* 权威回读失败时保留原选择和首个错误, 不推断提交结果. */
                }
            }
        } finally {
            active.value = undefined
            busy.value = false
            cancelling.value = false
            committing.value = false
        }
    }
    onBeforeUnmount(() => {
        disposed = true
        generation++
        void cancel()
    })
    return { busy, writable, deleteSelected }
}
