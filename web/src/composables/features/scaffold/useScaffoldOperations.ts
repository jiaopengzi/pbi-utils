/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useScaffoldOperations.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建多请求操作的互斥、进度、取消与过期计划排空.
 */
import { watch, onBeforeUnmount } from "vue"
import { BridgeError, type OperationEvent, type BridgePhase, type Methods } from "@/bridge/client"
import { useOperationMessage } from "@/composables/ui/useFeedback"
import { useI18n } from "@/composables/i18n/useI18n"
import type { ScaffoldState } from "@/composables/features/scaffold/useScaffoldState"
/** 串行运行完整业务链并排空取消; 进度和提交边界以宿主事件为准.
 * @param state 当前挂载的页面状态.
 * @param emitBusy 向外层发布请求和模态导航锁.
 * @returns 受控请求、互斥运行及旧计划失效入口; 业务异常保留在错误状态.
 */
export function useScaffoldOperations(state: ScaffoldState, emitBusy: (value: boolean) => void) {
    const {
        bridge,
        lifecycle,
        plan,
        busy,
        requestId,
        error,
        percent,
        committed,
        cancelling,
        action,
        stage,
        formLocked,
        locked,
        measureDialog,
        projectName,
        templateId,
        templateSelectionInvalid,
        git,
        output,
        measuresEnabled,
        sampleEnabled,
        calendarEnabled,
        periodsEnabled,
        udfsEnabled,
        measures,
        sample,
        calendar,
        periods,
        udfs,
        selectedTemplateQueries,
        templateQueryGroups,
    } = state
    const { t, locale, hostText } = useI18n()
    watch(locked, (value) => emitBusy(value), { flush: "sync" })
    // 任何业务值和全局语言变化立刻解除旧摘要; 原值仍留在可选步骤中供恢复.
    watch(
        [
            projectName,
            templateId,
            templateSelectionInvalid,
            git,
            output,
            locale,
            measuresEnabled,
            () => state.draft.measuresMode,
            sampleEnabled,
            calendarEnabled,
            periodsEnabled,
            udfsEnabled,
            measures,
            sample,
            calendar,
            periods,
            udfs,
            selectedTemplateQueries,
            templateQueryGroups,
        ],
        invalidate,
        { deep: true, flush: "sync" },
    )
    useOperationMessage(() =>
        busy.value
            ? {
                  message:
                      action.value === "scaffold.openDesktop"
                          ? t("connection.connectingDesktop")
                          : action.value === "scaffold.openDirectory"
                            ? t("scaffold.working")
                            : committed.value
                              ? t("scaffold.committing")
                              : stage.value
                                ? t(stage.value)
                                : t("scaffold.working"),
                  progress: action.value === "create" ? Math.min(100, Math.round(percent.value)) : undefined,
                  cancel: requestId.value ? cancel : undefined,
                  cancelLabel: t("scaffold.cancel"),
                  cancelDisabled: cancelling.value || committed.value,
              }
            : undefined,
    )
    /** 改动后排队丢弃原计划, 下次准备先等丢弃终态, 避免宿主门闩竞态. */
    function invalidate(): void {
        const previous = plan.value
        plan.value = undefined
        if (previous && previous.digest !== lifecycle.publishedDigest)
            lifecycle.discardQueue = lifecycle.discardQueue
                .then(async () => {
                    await bridge.request("scaffold.discard", { digest: previous.digest }).promise
                })
                .catch(() => undefined)
    }
    /** 受控请求统一关联事件和取消 ID, 不接受文本推断提交阶段. */
    function tracked<M extends keyof Methods>(method: M, params: Methods[M]["params"]) {
        if (cancelling.value && !committed.value) throw new BridgeError("CANCELLED", t("scaffold.cancel"))
        const pending = bridge.request(method, params, progress, phase)
        requestId.value = pending.id
        return pending
    }
    /** 只接受宿主脚手架进度, 发布和页面渲染前最高 99%. */
    function progress(event: OperationEvent): void {
        if (lifecycle.disposed || event.type !== "scaffoldProgress" || !event.payload || typeof event.payload !== "object") return
        const data = event.payload as { percent?: unknown; canCancel?: unknown; stage?: unknown }
        if (typeof data.stage === "string" && ["scaffold.preparing", "scaffold.previewReady", "scaffold.publishing"].includes(data.stage))
            stage.value = data.stage === "scaffold.previewReady" ? "scaffold.preparing" : data.stage
        if (typeof data.percent === "number" && Number.isFinite(data.percent)) percent.value = Math.max(percent.value, Math.min(99, data.percent))
        if (data.canCancel === false) committed.value = true
    }
    /** 提交后等待不可取消业务终态; Desktop 连接不把阶段事件视作模型写入提交. */
    function phase(value: BridgePhase): void {
        if (value === "committing" && action.value !== "scaffold.openDesktop") committed.value = true
    }
    /** 操作互斥覆盖整个多请求链, 取消不提前解除导航锁. */
    async function run(name: string, work: () => Promise<void>): Promise<void> {
        if (formLocked.value || measureDialog.value) return
        lifecycle.operationGeneration++
        busy.value = true
        action.value = name
        error.value = ""
        committed.value = false
        cancelling.value = false
        percent.value = 0
        stage.value = ""
        try {
            await work()
        } catch (cause) {
            if (!lifecycle.disposed && !(cause instanceof BridgeError && cause.code === "CANCELLED"))
                error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        } finally {
            requestId.value = ""
            busy.value = false
            action.value = ""
        }
    }
    /** 发起取消后仍等待原请求终态, 不撤销已发布结果. */
    async function cancel(): Promise<void> {
        if (!requestId.value || committed.value || cancelling.value) return
        const generation = lifecycle.operationGeneration,
            pendingId = requestId.value
        cancelling.value = true
        try {
            await bridge.request("request.cancel", { requestId: pendingId }).promise
        } catch (cause) {
            if (!lifecycle.disposed && busy.value && lifecycle.operationGeneration === generation && requestId.value === pendingId)
                error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        }
    }
    onBeforeUnmount(() => {
        lifecycle.disposed = true
        if (requestId.value && !committed.value) void bridge.request("request.cancel", { requestId: requestId.value }).promise.catch(() => undefined)
        invalidate()
    })
    return { invalidate, tracked, run, cancel }
}
export type ScaffoldOperations = ReturnType<typeof useScaffoldOperations>
