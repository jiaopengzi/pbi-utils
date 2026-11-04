/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelTableExport.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 管理模型表选择、统一格式、批量导出及取消排空, 普通结果统一浮动提示.
 */
import { computed, onBeforeUnmount, ref, watch } from "vue"
import { BridgeError, type BridgeClient, type BridgePhase, type ModelTableExportProgress, type ProjectSnapshot } from "@/bridge/client"
import { hasPendingModelUpdate, isReadOnlyProject } from "@/bridge/modelAccess"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

export type ModelTableExportProps = { project: ProjectSnapshot; bridge: BridgeClient; locked?: boolean }
type ModelTable = ProjectSnapshot["model"]["tables"][number]

/** 只管理已核验模型的选择和意图, 不读取业务值或构造文件路径; 取消始终等待原请求唯一终态. */
export function useModelTableExport(props: ModelTableExportProps, onExported?: () => Promise<void>) {
    const { t, hostText, locale } = useI18n()
    const { showFeedback } = useFeedback()
    const selected = ref<string[]>([]),
        selectionBaselineId = ref(props.project.baselineId),
        format = ref<"xlsx" | "csv">("xlsx"),
        includeLog = ref(true),
        scope = ref<"sample" | "all">("sample"),
        rowLimit = ref<number | undefined>(100)
    const busy = ref(false),
        phase = ref<BridgePhase>(),
        progress = ref<ModelTableExportProgress>(),
        cancelling = ref(false)
    let disposed = false,
        generation = 0,
        active: { id: string; bridge: BridgeClient } | undefined
    const identity = () =>
        JSON.stringify([props.project.projectId, props.project.baselineId, props.project.connection?.instanceId, props.project.connection?.modelId])
    const locked = computed(() => busy.value || !!props.locked)
    const validLimit = computed(() => scope.value === "all" || (Number.isInteger(rowLimit.value) && rowLimit.value! >= 1 && rowLimit.value! <= 2147483647))
    /** 返回不可用原因, 受限Service依实际游标判列, 其它来源保留真实能力守卫. */
    function unavailable(table: ModelTable): string {
        if (hasPendingModelUpdate(props.project)) return "model.exportPending"
        const connection = props.project.connection
        if (!connection || connection.kind === "pbip") return "model.exportOffline"
        const limitedService = connection.kind === "powerbi-service" && connection.isLimited
        if (connection.isLimited && !limitedService) return "model.exportLimited"
        if (!limitedService && !table.columns.some((column) => column.kind !== "RowNumber")) return "model.exportNoColumns"
        return ""
    }
    /** 可写本地模型允许选择仅含度量值或离线表供删除; 只读来源仍按真实导出能力选择. */
    function selectionUnavailable(table: ModelTable): string {
        return !isReadOnlyProject(props.project) && !hasPendingModelUpdate(props.project) ? "" : unavailable(table)
    }
    const eligible = computed(() => props.project.model.tables.filter((table) => !selectionUnavailable(table)))
    const selectedTables = computed(() => eligible.value.filter((table) => selected.value.includes(table.name)))
    const exportAvailable = computed(() => selectedTables.value.length > 0 && selectedTables.value.every((table) => !unavailable(table)))
    /** 全选或取消指定可见表, 未指定范围时作用于全部; 核验真实可选身份并保留范围外选择, 不允许过期基线选择混入新操作. */
    function selectAll(value: boolean, names?: readonly string[]): void {
        if (!locked.value) {
            const available = eligible.value.filter((table) => !names || names.includes(table.name)).map((table) => table.name)
            const previous = selectionBaselineId.value === props.project.baselineId ? selected.value : []
            selectionBaselineId.value = props.project.baselineId
            selected.value = value ? [...new Set([...previous, ...available])] : names ? previous.filter((name) => !available.includes(name)) : []
        }
    }
    /** 只切换当前模型内的真实表, 不接受未知名称或绕过disabled的点击. */
    function selectTable(table: ModelTable, value: boolean): void {
        if (locked.value || selectionUnavailable(table) || !props.project.model.tables.includes(table)) return
        if (selectionBaselineId.value !== props.project.baselineId) selected.value = []
        selectionBaselineId.value = props.project.baselineId
        selected.value = value ? [...new Set([...selected.value, table.name])] : selected.value.filter((name) => name !== table.name)
    }
    /** 取消当前批次, 不因取消确认提前解除锁或向新模型发旧请求. */
    async function cancel(): Promise<void> {
        if (!active || phase.value === "committing" || cancelling.value) return
        const request = active
        cancelling.value = true
        try {
            const result = await request.bridge.request("request.cancel", { requestId: request.id }).promise
            if (active === request && !result.accepted) cancelling.value = false
        } catch {
            if (active === request) cancelling.value = false
        }
    }
    /** 按宿主实际复核表数表示进度, 读完仍需完成文件及发布, 成功前不显示100%. */
    const progressValue = computed(() =>
        progress.value
            ? Math.min(99, Math.floor((progress.value.completedTables / progress.value.totalTables) * 100))
            : phase.value === "exporting"
              ? 0
              : undefined,
    )
    /** 固定阶段与实际读取行数原位更新共享消息, 不请求全量COUNTROWS或推算均匀速度. */
    function progressMessage(): string {
        const item = progress.value
        if (phase.value === "committing") return item ? t("model.exportProgressPublishing", { total: item.totalTables }) : t("model.exportPublishing")
        if (cancelling.value) return t("model.exportCancelling")
        if (!item) return t("model.exportProcessing", { table: selectedTables.value.length })
        const key = {
            reading: "model.exportProgressReading",
            verifying: "model.exportProgressVerifying",
            finalizing: "model.exportProgressFinalizing",
            publishing: "model.exportProgressPublishing",
        } as const
        return t(key[item.stage], {
            index: item.tableIndex,
            total: item.totalTables,
            completed: item.completedTables,
            rows: item.rowsRead.toLocaleString(locale.value),
        })
    }
    useOperationMessage(() =>
        busy.value
            ? {
                  message: progressMessage(),
                  progress: progressValue.value,
                  cancel: active ? cancel : undefined,
                  cancelLabel: t("model.cancel"),
                  cancelDisabled: cancelling.value || phase.value === "committing",
              }
            : undefined,
    )
    watch(identity, (next, previous) => {
        generation++
        if (next !== previous) void cancel()
        selected.value = props.project.model.tables
            .filter((table) => selected.value.includes(table.name) && !selectionUnavailable(table))
            .map((table) => table.name)
        if (JSON.parse(next)[0] !== JSON.parse(previous)[0]) selected.value = []
    })
    /** 冻结选定表及统一格式, 一次选择目标并等待全批发布; 失败保留选择, 迟到结果不通知新工作区. */
    async function exportTables(): Promise<void> {
        if (disposed || locked.value || !exportAvailable.value) return
        if (!validLimit.value) {
            showFeedback({ message: t("model.exportInvalidLimit"), type: "warning" })
            return
        }
        const epoch = ++generation,
            target = identity(),
            tables = selectedTables.value.map((table) => table.name),
            bridge = props.bridge
        busy.value = true
        phase.value = undefined
        progress.value = undefined
        cancelling.value = false
        try {
            const request = bridge.request(
                "model.exportTables",
                {
                    projectId: props.project.projectId,
                    baselineId: props.project.baselineId,
                    tables,
                    rowLimit: scope.value === "sample" ? rowLimit.value! : null,
                    format: format.value,
                    includeLog: format.value === "xlsx" && includeLog.value,
                    locale: locale.value,
                },
                undefined,
                (value) => {
                    if (!disposed && epoch === generation) {
                        phase.value = value
                        if (value === "canceling") cancelling.value = true
                    }
                },
                (value) => {
                    if (!disposed && epoch === generation && !cancelling.value) progress.value = value
                },
            )
            active = { id: request.id, bridge }
            const result = await request.promise
            if (disposed || epoch !== generation || target !== identity()) return
            if (onExported && !result.isSimulation) await onExported()
            if (disposed || epoch !== generation || target !== identity()) return
            showFeedback({
                message: result.isSimulation
                    ? t("model.exportSimulation")
                    : t("model.exportComplete", { tables: tables.length, count: result.rowCount, file: result.fileName }),
                type: result.isSimulation ? "info" : "success",
            })
            if (result.locationWarning) showFeedback({ message: hostText(result.locationWarning), type: "warning" })
        } catch (error) {
            if (disposed || epoch !== generation || target !== identity()) return
            const cancelled = error instanceof BridgeError && error.code === "CANCELLED"
            const detail = error instanceof Error ? hostText(error.message) : t("model.exportFailed")
            showFeedback({
                message: cancelled && error instanceof Error && error.message === "操作已取消" ? t("model.exportCancelled") : detail,
                type: cancelled ? "warning" : "error",
            })
        } finally {
            active = undefined
            busy.value = false
            phase.value = undefined
            progress.value = undefined
            cancelling.value = false
        }
    }
    onBeforeUnmount(() => {
        disposed = true
        generation++
        void cancel()
    })
    return {
        t,
        hostText,
        selected,
        selectionBaselineId,
        format,
        includeLog,
        scope,
        rowLimit,
        validLimit,
        busy,
        locked,
        eligible,
        selectedTables,
        exportAvailable,
        selectAll,
        selectTable,
        unavailable,
        selectionUnavailable,
        exportTables,
    }
}
