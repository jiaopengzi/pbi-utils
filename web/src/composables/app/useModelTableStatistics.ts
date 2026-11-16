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
 * 按当前目标和基线自动读取统计, 与导出共用操作锁, 未提供的值显示为-.
 * onUpdated 发布同目标权威快照, operationBlocked 核验相邻操作; 返回统计和刷新入口.
 * 过期基线仅自动核验一次, 失败时显示宿主反馈.
 */
export function useModelTableStatistics(props: ModelTableExportProps, onUpdated: (project: ProjectSnapshot) => void, operationBlocked: () => boolean) {
    const { t, hostText, locale } = useI18n(),
        { showFeedback } = useFeedback()
    const cache = modelReadCache(props.bridge)
    const cached = cache.statistics?.identity === modelReadIdentity(props.project) ? cache.statistics.result : undefined
    const result = shallowRef<ModelTableStatisticsResult | undefined>(cached),
        busy = ref(false),
        cancelRequested = ref(false),
        expired = ref(false),
        cancelling = computed(() => cancelRequested.value || expired.value)
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
    /** 仅展示当前目标的失败, 用户主动取消不显示错误, 到期取消仍报告超时. */
    function notify(error: unknown, epoch: number): void {
        if (!disposed && epoch === generation && !cancelRequested.value)
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
    /** 由用户请求取消本通道, 保持锁定直至原统计请求终结. */
    async function cancel(): Promise<void> {
        if (active && !cancelling.value) {
            const request = active
            cancelRequested.value = true
            try {
                const response = await request.bridge.request("request.cancel", { requestId: request.id }).promise
                if (active === request && !response.accepted) cancelRequested.value = false
            } catch {
                if (active === request) cancelRequested.value = false
                /* 原请求仍负责唯一终态和解锁. */
            }
        }
    }
    useOperationMessage(() =>
        busy.value
            ? {
                  message: t(cancelling.value ? "model.statisticsCancelling" : "model.statisticsLoading"),
                  cancel,
                  cancelDisabled: cancelling.value,
                  cancelLabel: t("model.cancel"),
              }
            : undefined,
    )
    /** 在当前身份下读取一次统计; 刷新只替换成功结果, 失败不将未知数伪装为0. */
    async function read(): Promise<void> {
        if (disposed || busy.value || blocked()) return
        const epoch = ++generation,
            target = identity(),
            project = props.project,
            bridge = props.bridge
        attempted = target
        cache.statistics = undefined
        recoveryTarget = ""
        busy.value = true
        cancelRequested.value = false
        expired.value = false
        try {
            const identityParams = { projectId: project.projectId, baselineId: project.baselineId }
            const phase = (value: string) => {
                if (value === "canceling" && epoch === generation) expired.value = true
            }
            const request = bridge.request("model.tableStatistics", identityParams, undefined, phase)
            active = { id: request.id, bridge }
            const response = await request.promise
            // 自动到期只请求取消; 若宿主已先完成, 仍接纳原请求的权威成功结果.
            if (disposed || epoch !== generation || cancelRequested.value || target !== identity()) return
            if (response.projectId !== props.project.projectId || response.baselineId !== props.project.baselineId) return
            result.value = response
            cache.statistics = { identity: modelReadIdentity(project), result: response }
        } catch (error) {
            if (
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
            else if (expired.value && error instanceof BridgeError && error.code === "CANCELLED") notify(new BridgeError("TIMEOUT", "请求超时"), epoch)
            else notify(error, epoch)
        } finally {
            active = undefined
            // 待处理状态中断自动读取后, 显式恢复即使不改变基线也应继续读取, 不能沿用失败的已尝试标记.
            if (hasPendingModelUpdate(props.project)) {
                attempted = ""
                recoveryAttempted = false
            }
            busy.value = false
            cancelRequested.value = false
            expired.value = false
        }
    }
    /** 重读当前模型统计, 成功后替换已有统计, 失败时保留现有结果. */
    function refresh(): Promise<void> {
        if (disposed || busy.value || blocked()) return Promise.resolve()
        recoveryAttempted = false
        return read()
    }
    /** 查询计数受当前账号可见范围约束; 明确不可计数与尚未读取分开标注. */
    function countKind(table: string): "query" | "storage" | "unknown" | "unavailable" {
        const value = byTable.value.get(table)
        if (value?.rowCountUnavailableReason === "no_queryable_columns") return "unavailable"
        if (value?.rowCount === undefined || value.rowCount === null) return "unknown"
        return value.isQueryCount ? "query" : "storage"
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
    /** 返回指定表的本地化行数, 无可查询列显示不适用, 未读取显示为-. */
    function rowCount(table: string): string {
        const value = byTable.value.get(table)
        if (value?.rowCountUnavailableReason === "no_queryable_columns") return t("model.rowCountNotApplicable")
        return value?.rowCount === undefined || value.rowCount === null ? "-" : new Intl.NumberFormat(locale.value).format(value.rowCount)
    }
    /** 根据指定表的原始统计判断是否至少有一行, 未知或不适用均不满足. */
    function hasRows(table: string): boolean {
        const value = byTable.value.get(table)
        return value?.rowCountUnavailableReason !== "no_queryable_columns" && (value?.rowCount ?? 0) > 0
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
    return { busy, countKind, refresh, rowCount, hasRows, size }
}
