/**
 * FilePath    : pbi-utils\web\src\composables\app\useDesktopInstances.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : Desktop 实例扫描、完整身份连接和取消排空.
 */
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { projectSummary, scannedSummary } from "@/app/modelSummary"
import { computed, onActivated, onMounted, onUnmounted, ref, watch } from "vue"
import { BridgeClient, type ProjectSnapshot, type PowerBiInstance, type PowerBiSnapshot } from "@/bridge/client"
import { hostText } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"

export type DesktopInstancesProps = {
    bridge: BridgeClient
    activeProject?: ProjectSnapshot
    disabled?: boolean
    operationLocked?: boolean
    fileOpening?: boolean
    refreshKey?: string | number
}
export type DesktopInstancesEmit = {
    (event: "busy", ...args: [value: boolean]): void
    (event: "connected", ...args: [value: ProjectSnapshot]): void
    (event: "openFile", ...args: []): void
    (event: "viewModel", ...args: []): void
}

/** 判断扫描是否实际提供唯一模型; 同一判定供摘要、卡片和连接守卫复用. */
function hasReadableModel(instance: PowerBiInstance): boolean {
    return instance.status === "connected" && instance.models.length === 1
}

/** 管理 Desktop 实例扫描、完整身份连接和取消排空; props 为当前响应式输入, emit 转发权威结果, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useDesktopInstances(props: DesktopInstancesProps, emit: DesktopInstancesEmit) {
    const { locale, t } = useI18n()
    const { showFeedback } = useFeedback()

    const snapshot = ref<PowerBiSnapshot>()
    const snapshotFresh = ref(false)
    const snapshotStale = ref(false)
    // 仅计入真实唯一模型, 不把当前模型的“查看”入口或未知/旧连接身份当作可连接事实.
    const connectableCount = computed(() => snapshot.value?.instances.filter(hasReadableModel).length ?? 0)
    const loading = ref(false)
    const operationMessageVisible = ref(false)
    // 临时并发锁沿父级显式传递; 业务禁用和失败后的旧快照仍保留禁用外观.
    const preserveAppearance = computed(() => (loading.value || props.operationLocked) && (!props.disabled || props.operationLocked))
    const operation = ref<"scan" | "connect">("scan")
    const cancellationRequested = ref(false)
    const error = ref("")
    const requestId = ref<string>()
    let mounted = true
    let deferredRefresh = false
    let activatedOnce = false

    useOperationMessage(() =>
        loading.value && operationMessageVisible.value
            ? {
                  message: t(operation.value === "scan" ? "powerbi.loading" : "powerbi.connecting"),
                  cancel: () => void cancel(),
                  cancelLabel: t("powerbi.cancel"),
                  cancelDisabled: cancellationRequested.value || !requestId.value,
              }
            : undefined,
    )

    /** 扫描保留旧卡片和身份锁; showProgress 仅为主动扫描显示消息, 后台重验失败仍展示真实错误. */
    async function refresh(showProgress = true) {
        if (loading.value || props.disabled) return
        operationMessageVisible.value = showProgress
        operation.value = "scan"
        loading.value = true
        cancellationRequested.value = false
        emit("busy", true)
        error.value = ""
        snapshotFresh.value = false
        const request = props.bridge.request("powerbi.instances", {})
        requestId.value = request.id
        try {
            const result = await request.promise
            if (mounted) {
                snapshot.value = result
                snapshotFresh.value = true
                snapshotStale.value = false
            }
        } catch (e) {
            if (mounted) {
                snapshotStale.value = true
                error.value = e instanceof Error ? e.message : t("powerbi.readFailed")
            }
        } finally {
            requestId.value = undefined
            loading.value = false
            cancellationRequested.value = false
            emit("busy", false)
        }
    }

    /** 仅用宿主返回的完整实例身份判断当前连接; 缺少身份或远端来源时保守返回 false. */
    function isCurrentInstance(instance: PowerBiInstance) {
        const connection = props.activeProject?.connection
        return connection?.kind === "desktop" && !!instance.instanceId && connection.instanceId === instance.instanceId
    }

    /** 按宿主状态和稳定错误码给出短标签, 不根据标题或错误正文猜测连接模式. */
    function instanceStatusLabel(instance: PowerBiInstance): string | undefined {
        if (instance.status === "starting") return t("powerbi.starting")
        if (instance.errorCode === "DESKTOP_NO_LOCAL_MODEL" || instance.errorCode === "DESKTOP_REMOTE_REPORT") return t("powerbi.noLocalModel")
        if (instance.status === "unavailable") return t("powerbi.readError")
        if (instance.models.length > 1) return t("powerbi.ambiguousModels")
        if (!instance.models.length) return t("powerbi.noLocalModel")
        return undefined
    }

    /** 优先保留宿主具体原因, 空原因时按已知扫描状态补充说明; 不产生通知或业务请求. */
    function instanceReason(instance: PowerBiInstance): string {
        if (instance.message?.trim()) return hostText(instance.message)
        if (instance.status === "starting") return t("powerbi.waitingReason")
        if (instance.status === "unavailable") return t("powerbi.readFailed")
        if (instance.models.length > 1) return t("powerbi.multipleModels")
        if (!instance.models.length) return t("powerbi.noModels")
        return ""
    }

    /** 用户查看当前快照中某实例的原因, 标明标题与 PID; 过期/在途/卸载时忽略, 不尝试连接. */
    function showInstanceReason(instance: PowerBiInstance): void {
        if (!mounted || loading.value || props.disabled || !snapshotFresh.value || !snapshot.value?.instances.includes(instance)) return
        const reason = instanceReason(instance)
        if (!reason) return
        showFeedback({ message: t("powerbi.instanceReason", { title: instance.title, processId: instance.processId, reason }), type: "info" })
    }

    /** 当前实例只导航到已绑定的模型; 其他实例保持原连接与宿主身份复核流程. */
    function activateInstance(instance: PowerBiInstance) {
        if (loading.value || props.disabled || !snapshotFresh.value) return
        if (isCurrentInstance(instance)) emit("viewModel")
        else void selectInstance(instance)
    }

    /** 仅选择含唯一可读模型的 Desktop 实例; 空模型/多模型不发请求, 后端继续复核身份, 当前连接不重复提交. */
    async function selectInstance(instance: PowerBiInstance) {
        if (loading.value || props.disabled || !snapshotFresh.value || !hasReadableModel(instance) || isCurrentInstance(instance)) return
        operationMessageVisible.value = true
        operation.value = "connect"
        loading.value = true
        cancellationRequested.value = false
        emit("busy", true)
        error.value = ""
        try {
            const request = props.bridge.request("project.connectLocal", {
                processId: String(instance.processId),
                ...(instance.instanceId ? { instanceId: instance.instanceId } : {}),
            })
            requestId.value = request.id
            const result = await request.promise
            if (mounted) emit("connected", result)
        } catch (e) {
            if (mounted) error.value = e instanceof Error ? e.message : t("powerbi.connectFailed")
        } finally {
            loading.value = false
            cancellationRequested.value = false
            requestId.value = undefined
            emit("busy", false)
        }
    }
    /** 对当前扫描或实例连接发送一次取消; 等待原请求终态, 迟到取消失败不覆盖后续请求提示. */
    async function cancel() {
        const activeRequestId = requestId.value
        if (!activeRequestId || cancellationRequested.value) return
        cancellationRequested.value = true
        try {
            await props.bridge.request("request.cancel", { requestId: activeRequestId }).promise
        } catch (e) {
            if (mounted && requestId.value === activeRequestId) {
                cancellationRequested.value = false
                error.value = e instanceof Error ? e.message : t("powerbi.cancelFailed")
            }
        }
    }

    /** 文件连接成功后由父级更新刷新标记; 合并在途变更, 等待原请求排空和导航锁解除后只补一次扫描. */
    function refreshWhenAvailable() {
        if (!mounted || props.disabled || loading.value || !deferredRefresh) return
        deferredRefresh = false
        void refresh(false)
    }

    /** 面板在最近文件连接中挂载时延后首次扫描, 父请求结束后只补一次, 失败不循环重试. */
    onMounted(() => {
        deferredRefresh = true
        refreshWhenAvailable()
    })
    /** 来源切回 Desktop 时复用卡片并重新核验; 首次挂载沿用原扫描, 不重复提交. */
    onActivated(() => {
        if (activatedOnce) {
            deferredRefresh = true
            refreshWhenAvailable()
        }
        activatedOnce = true
    })
    watch(
        () => [props.disabled, props.refreshKey, loading.value] as const,
        ([, refreshKey], [, previousRefreshKey]) => {
            if (refreshKey !== previousRefreshKey) deferredRefresh = true
            refreshWhenAvailable()
        },
    )
    onUnmounted(() => {
        mounted = false
        void cancel()
    })
    return {
        snapshot,
        snapshotFresh,
        snapshotStale,
        connectableCount,
        hasReadableModel,
        instanceStatusLabel,
        instanceReason,
        showInstanceReason,
        loading,
        preserveAppearance,
        operation,
        error,
        refresh,
        isCurrentInstance,
        activateInstance,
        locale,
        t,
        projectSummary,
        scannedSummary,
        hostText,
    }
}
