/**
 * FilePath    : pbi-utils\web\src\composables\app\useUdfLint.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前模型 UDF 分析、精确预览确认、取消与目标切换保护.
 */
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue"
import type { BridgeClient, BridgePhase, ProjectSnapshot, UdfLintResult } from "@/bridge/client"
import { hasPendingModelUpdate, isConnectedDesktopProject, isModelUpdateComplete, isReadOnlyProject } from "@/bridge/modelAccess"
import { udfLintDeleteAllowed, udfLintDeleteRequest } from "@/bridge/udfLintBudget"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { readModelForOperation } from "@/features/model/operationFreshness"

export type UdfLintProps = { project: ProjectSnapshot; bridge: BridgeClient; locked?: boolean }
export type UdfLintEmit = { (event: "busy", value: boolean): void; (event: "updated", value: ProjectSnapshot): void }

/** 只读分析后展示完整删除集合, 确认时绑定原目标与基线; 失败回读待保存事实, 迟到结果不更新新目标. */
export function useUdfLint(props: UdfLintProps, emit: UdfLintEmit) {
    const { t, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const result = shallowRef<UdfLintResult>()
    const busy = ref(false),
        dialog = ref(false),
        requestId = ref(""),
        cancelling = ref(false),
        committed = ref(false),
        error = ref("")
    let generation = 0,
        disposed = false,
        publishing = "",
        stopRequested = false
    /** 完整界面请求身份包含目标与基线; project 缺省为当前 props, 供迟到结果与自身刷新区分. */
    const identity = (project = props.project) =>
        JSON.stringify([project.projectId, project.projectPath, project.connection?.kind, project.connection?.instanceId, project.baselineId])
    const writable = computed(() => !isReadOnlyProject(props.project) && !hasPendingModelUpdate(props.project))
    const canAnalyze = computed(() => writable.value && !props.locked && !busy.value && props.project.model.functions.length > 0)
    const temporarilyLocked = computed(() => writable.value && props.project.model.functions.length > 0 && (props.locked || busy.value))
    const withinBudget = computed(() => !result.value || udfLintDeleteAllowed(result.value))
    const canDelete = computed(
        () =>
            writable.value &&
            !props.locked &&
            !busy.value &&
            result.value?.complete === true &&
            result.value.unused.length > 0 &&
            withinBudget.value &&
            result.value.projectId === props.project.projectId &&
            result.value.baselineId === props.project.baselineId,
    )
    watch(
        () => busy.value || dialog.value,
        (value) => emit("busy", value),
        { flush: "sync" },
    )
    watch(identity, () => {
        // 本次只读刷新发布的新基线属于同一次分析, 外部切换仍撤销旧响应.
        if (identity() === publishing) return
        generation++
        result.value = undefined
        dialog.value = false
        error.value = ""
        void cancel()
    })
    useOperationMessage(() =>
        busy.value
            ? {
                  message: t(committed.value ? "model.udfLintSaving" : "model.udfLintProcessing"),
                  cancel: requestId.value ? cancel : undefined,
                  cancelLabel: t("model.cancel"),
                  cancelDisabled: committed.value || cancelling.value,
              }
            : undefined,
    )
    /** 只向当前请求发送取消, 等待原请求终态后再解除忙状态. */
    async function cancel(): Promise<void> {
        if (!requestId.value || committed.value || cancelling.value) return
        // 读取请求可能已经完成而拒绝取消; 用户的停止意图仍阻止后续分析或删除请求.
        stopRequested = true
        cancelling.value = true
        try {
            if (!(await props.bridge.request("request.cancel", { requestId: requestId.value }).promise).accepted) cancelling.value = false
        } catch {
            cancelling.value = false
        }
    }
    /** 在指定只读快照上分析并发布准确候选; epoch/target 绑定原请求, 迟到、取消或不匹配结果不进入删除预览, 读取错误交回调用方. */
    async function analyzeSnapshot(snapshot: ProjectSnapshot, epoch: number, target: string): Promise<void> {
        const request = props.bridge.request("model.udfLint", { projectId: snapshot.projectId, baselineId: snapshot.baselineId })
        requestId.value = request.id
        const next = await request.promise
        if (disposed || epoch !== generation || stopRequested || cancelling.value || identity() !== target) return
        if (next.projectId !== snapshot.projectId || next.baselineId !== snapshot.baselineId) return
        if (isConnectedDesktopProject(snapshot)) {
            publishing = identity(snapshot)
            try {
                emit("updated", snapshot)
                await nextTick()
            } finally {
                publishing = ""
            }
        }
        if (disposed || epoch !== generation || stopRequested || cancelling.value || (identity() !== target && identity() !== identity(snapshot))) return
        result.value = next
        dialog.value = next.complete && next.unused.length > 0
    }
    /** 读取完整模型分析, 只在原目标仍有效时发布预览; 取消和未知表达式不产生候选删除. */
    async function analyze(): Promise<void> {
        if (!canAnalyze.value) return
        const epoch = ++generation,
            target = identity()
        busy.value = true
        result.value = undefined
        dialog.value = false
        error.value = ""
        cancelling.value = false
        stopRequested = false
        try {
            const snapshot = await readModelForOperation(props.bridge, props.project, (id) => (requestId.value = id))
            if (disposed || epoch !== generation || stopRequested || cancelling.value || identity() !== target) return
            await analyzeSnapshot(snapshot, epoch, target)
        } catch (cause) {
            if (!disposed && epoch === generation) error.value = cause instanceof Error ? cause.message : t("model.editFailed")
        } finally {
            requestId.value = ""
            busy.value = false
            cancelling.value = false
        }
    }
    /** 只有明确预览确认才提交删除集合; 目标核验与直接提交由既有宿主编辑通道执行, 失败保留权威恢复入口. */
    async function remove(): Promise<void> {
        if (!dialog.value || !canDelete.value || !result.value) return
        const epoch = ++generation,
            target = identity(),
            preview = result.value
        busy.value = true
        cancelling.value = false
        committed.value = false
        error.value = ""
        stopRequested = false
        let attemptedEdit = false
        const phase = (value: BridgePhase) => {
            if (!disposed && value === "committing") committed.value = true
        }
        try {
            const fresh = await readModelForOperation(props.bridge, props.project, (id) => (requestId.value = id))
            if (disposed || epoch !== generation || stopRequested || cancelling.value || identity() !== target) return
            if (fresh.baselineId !== preview.baselineId) {
                // 旧确认只授权原分析集合; 模型变化后重新展示候选, 等待用户再次确认.
                result.value = undefined
                dialog.value = false
                await analyzeSnapshot(fresh, epoch, target)
                return
            }
            attemptedEdit = true
            const request = props.bridge.request("model.edit", udfLintDeleteRequest(preview), undefined, phase)
            requestId.value = request.id
            const snapshot = await request.promise
            if (disposed || epoch !== generation || identity() !== target) return
            dialog.value = false
            result.value = undefined
            emit("updated", snapshot)
            if (isModelUpdateComplete(snapshot))
                showFeedback({
                    type: snapshot.saveResult ? "success" : "info",
                    message:
                        snapshot.saveResult && isConnectedDesktopProject(snapshot)
                            ? t("model.desktopUpdated")
                            : snapshot.saveResult?.message
                              ? hostText(snapshot.saveResult.message)
                              : t("model.simulationSaved"),
                })
        } catch (cause) {
            if (!disposed && epoch === generation && identity() === target) {
                error.value = cause instanceof Error ? cause.message : t("model.editFailed")
                dialog.value = false
                result.value = undefined
                if (attemptedEdit) {
                    try {
                        const snapshot = await props.bridge.request("project.inspect", {}).promise
                        if (!disposed && epoch === generation && identity() === target && snapshot.projectId === props.project.projectId)
                            emit("updated", snapshot)
                    } catch {
                        /* 回读失败保留原错误, 不将保存事实推定为失败或成功. */
                    }
                }
            }
        } finally {
            requestId.value = ""
            busy.value = false
            cancelling.value = false
            committed.value = false
        }
    }
    onBeforeUnmount(() => {
        disposed = true
        generation++
        void cancel()
        emit("busy", false)
    })
    return { t, hostText, result, busy, dialog, error, canAnalyze, canDelete, temporarilyLocked, withinBudget, analyze, remove }
}
