/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelTableStatistics.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 绑定权威模型读取附属统计, 自动核验过期基线并保护目标变化和迟到结果.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue"
import { BridgeError, type ModelTableStatisticsResult, type ProjectSnapshot } from "@/bridge/client"
import { hasPendingModelUpdate, isConnectedDesktopProject } from "@/bridge/modelAccess"
import { sameModelTarget } from "@/features/model/operationFreshness"
import { modelReadCache, modelReadIdentity } from "@/features/model/modelReadCache"
import type { ModelTableExportProps } from "./useModelTableExport"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

/**
 * 统计与导出共用操作锁, 未提供显示-; 架构 cardinality 明确标估计, 不伪造精确数据计数.
 * onUpdated 发布同目标权威快照, operationBlocked 核验相邻导出/目录操作; 返回统计和显式读取入口.
 * 过期元数据仅自动核验一次, 错误保持真实提示, 不重放精确计数或模型写入.
 */
export function useModelTableStatistics(props: ModelTableExportProps, onUpdated: (project: ProjectSnapshot) => void, operationBlocked: () => boolean) {
    const { t, hostText, locale } = useI18n(),
        { showFeedback } = useFeedback()
    const cache = modelReadCache(props.bridge)
    const cached = cache.statistics?.identity === modelReadIdentity(props.project) ? cache.statistics.result : undefined
    const result = shallowRef<ModelTableStatisticsResult | undefined>(cached),
        busy = ref(false),
        counting = ref(false),
        cancelling = ref(false)
    let disposed = false,
        generation = 0,
        attempted = cached ? identity() : "",
        recoveryAttempted = false,
        recoveryTarget = "",
        active: { id: string; bridge: typeof props.bridge } | undefined
    /** 完整目标与基线共同绑定统计结果, 同名表和可复用模型 ID 不能替代宿主身份. */
    function identity(project = props.project): string {
        return JSON.stringify([modelReadIdentity(project), cache.revision.value])
    }
    const blocked = () => !!props.locked || operationBlocked() || hasPendingModelUpdate(props.project)
    const byTable = computed(() => new Map(result.value?.tables.map((table) => [table.table, table])))
    /** 仅向当前仍在场的请求展示失败, 目标变化、取消和卸载不污染新页面. */
    function notify(error: unknown, epoch: number): void {
        if (!disposed && epoch === generation && !cancelling.value)
            showFeedback({ message: error instanceof Error ? hostText(error.message) : t("model.statisticsFailed"), type: "warning" })
    }
    /** 统计过期后只读核验一次; 成功发布权威快照, 待父级与相邻配置读取排空后再读取新基线. */
    async function recover(project: ProjectSnapshot, epoch: number, target: string): Promise<void> {
        recoveryAttempted = true
        result.value = undefined
        try {
            const request = props.bridge.request("project.inspect", {})
            active = { id: request.id, bridge: props.bridge }
            const fresh = await request.promise
            if (disposed || epoch !== generation || cancelling.value || target !== identity() || hasPendingModelUpdate(props.project)) return
            if (!sameModelTarget(project, fresh) || project.connection?.modelId !== fresh.connection?.modelId)
                throw new BridgeError("STALE_CONFIGURATION_TARGET", "配置目标已失效, 请重新读取")
            if (hasPendingModelUpdate(fresh)) throw new BridgeError("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存或核验")
            recoveryTarget = identity(fresh)
            attempted = ""
            active = undefined
            onUpdated(fresh)
        } catch (error) {
            notify(error, epoch)
        }
    }
    /** 仅取消本通道, 统计请求排空前不释放busy. */
    async function cancel(): Promise<void> {
        if (active && !cancelling.value) {
            const request = active
            cancelling.value = true
            try {
                const response = await request.bridge.request("request.cancel", { requestId: request.id }).promise
                if (active === request && !response.accepted) cancelling.value = false
            } catch {
                if (active === request) cancelling.value = false
                /* 原请求仍负责唯一终态和解锁. */
            }
        }
    }
    useOperationMessage(() =>
        busy.value
            ? {
                  message: t(cancelling.value ? "model.statisticsCancelling" : counting.value ? "model.countingRows" : "model.statisticsLoading"),
                  cancel,
                  cancelDisabled: cancelling.value,
                  cancelLabel: t("model.cancel"),
              }
            : undefined,
    )
    /** 在当前身份下读取一次统计; 刷新只替换成功结果, 失败不将未知数伪装为0. */
    async function read(names?: string[]): Promise<void> {
        if (disposed || busy.value || blocked()) return
        const epoch = ++generation,
            target = identity(),
            project = props.project,
            bridge = props.bridge
        attempted = target
        cache.statistics = undefined
        recoveryTarget = ""
        busy.value = true
        counting.value = !!names
        cancelling.value = false
        try {
            const identityParams = { projectId: props.project.projectId, baselineId: props.project.baselineId }
            const phase = (value: string) => {
                if (value === "canceling" && epoch === generation) cancelling.value = true
            }
            const request = names
                ? bridge.request("model.tableRowCounts", { ...identityParams, tables: names }, undefined, phase)
                : bridge.request("model.tableStatistics", identityParams, undefined, phase)
            active = { id: request.id, bridge }
            const response = await request.promise
            if (disposed || epoch !== generation || cancelling.value || target !== identity()) return
            if (response.projectId !== props.project.projectId || response.baselineId !== props.project.baselineId) return
            if (names) {
                const combined = new Map(result.value?.tables.map((table) => [table.table, table]))
                for (const table of response.tables) {
                    if (names.includes(table.table)) combined.set(table.table, { ...table, sizeBytes: combined.get(table.table)?.sizeBytes ?? null })
                }
                result.value = { ...response, tables: [...combined.values()] }
            } else result.value = response
            cache.statistics = { identity: modelReadIdentity(props.project), result: result.value }
        } catch (error) {
            if (
                !names &&
                error instanceof BridgeError &&
                error.code === "STALE_BASELINE" &&
                !disposed &&
                epoch === generation &&
                target === identity() &&
                !cancelling.value &&
                !recoveryAttempted &&
                !hasPendingModelUpdate(props.project)
            )
                await recover(project, epoch, target)
            else notify(error, epoch)
        } finally {
            active = undefined
            // 待处理状态中断自动读取后, 显式恢复即使不改变基线也应继续读取, 不能沿用失败的已尝试标记.
            if (hasPendingModelUpdate(props.project)) {
                attempted = ""
                recoveryAttempted = false
            }
            busy.value = false
            counting.value = false
            cancelling.value = false
        }
    }
    /** 显式重读元数据, 成功后替换旧统计, 不保留未经重新查询的旧精确计数. */
    function refresh(): Promise<void> {
        if (disposed || busy.value || blocked()) return Promise.resolve()
        recoveryAttempted = false
        return read()
    }
    /** 只统计当前所选真实表, 不在挂载时对全部模型隐式执行COUNTROWS. */
    function countExact(names: string[]): Promise<void> {
        if (!names.length || names.some((name) => !props.project.model.tables.some((table) => table.name === name))) return Promise.resolve()
        return read([...new Set(names)])
    }
    const hasEstimates = computed(() => result.value?.tables.some((table) => table.isEstimate) ?? false)
    /** 查询计数受当前账号可见范围约束; 明确不可计数与尚未读取分开标注. */
    function countKind(table: string): "query" | "estimate" | "storage" | "unknown" | "unavailable" {
        const value = byTable.value.get(table)
        if (value?.rowCountUnavailableReason === "no_queryable_columns") return "unavailable"
        if (value?.rowCount === undefined || value.rowCount === null) return "unknown"
        return value.isQueryCount ? "query" : value.isEstimate ? "estimate" : "storage"
    }
    watch(
        () => identity(),
        (target) => {
            generation++
            result.value = undefined
            cache.statistics = undefined
            attempted = ""
            if (target !== recoveryTarget) {
                recoveryAttempted = false
                recoveryTarget = ""
            }
            void cancel()
        },
    )
    /** 仅在当前快照已被父级接纳且相邻读链排空后自动统计, 同一恢复目标不重置次数. */
    function readWhenReady(): void {
        const target = identity()
        if (!disposed && !blocked() && !busy.value && target !== attempted && (!recoveryTarget || recoveryTarget === target)) void read()
    }
    watch(
        [() => identity(), blocked, busy],
        readWhenReady,
        // 父级新快照会同时触发隐藏配置读取, 必须等其 busy 传回 props 后再进入同一宿主门闩.
        { flush: "post" },
    )
    /** 返回当前 Desktop 表格页时自动更新只读统计, 隐藏、挂起保存和相邻操作期间不插入请求. */
    function resume(): void {
        if (document.visibilityState !== "hidden" && isConnectedDesktopProject(props.project)) void refresh()
    }
    onMounted(() => {
        window.addEventListener("focus", resume)
        document.addEventListener("visibilitychange", resume)
        // immediate 的首次回调仍同步执行, 首次挂载也需等父级配置 busy 与目录初始化传播完成.
        void nextTick(readWhenReady)
    })
    /** 只格式化真实非负值; 无可查询列显示不适用, schema估计以≈区分, 未读取始终为-. */
    function rowCount(table: string): string {
        const value = byTable.value.get(table)
        if (value?.rowCountUnavailableReason === "no_queryable_columns") return t("model.rowCountNotApplicable")
        return value?.rowCount === undefined || value.rowCount === null
            ? "-"
            : (value.isEstimate ? "≈ " : "") + new Intl.NumberFormat(locale.value).format(value.rowCount)
    }
    /** 大小采用二进制单位换算, 不用导出文件字节数代替模型驻留结构占用. */
    function size(table: string): string {
        let value = byTable.value.get(table)?.sizeBytes
        if (value === undefined || value === null) return "-"
        const units = ["B", "KB", "MB", "GB", "TB"]
        let index = 0
        while (value >= 1024 && index < units.length - 1) {
            value /= 1024
            index++
        }
        return new Intl.NumberFormat(locale.value, { maximumFractionDigits: 2 }).format(value) + " " + units[index]
    }
    onBeforeUnmount(() => {
        disposed = true
        generation++
        window.removeEventListener("focus", resume)
        document.removeEventListener("visibilitychange", resume)
        void cancel()
    })
    return { busy, counting, hasEstimates, countKind, refresh, countExact, rowCount, size }
}
