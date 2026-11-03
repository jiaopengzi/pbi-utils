/**
 * FilePath    : pbi-utils\web\src\composables\app\useConnectionWorkspace.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 模型连接来源、账号目录和连接请求排空.
 */
import { computed, onUnmounted, ref, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { projectSummary } from "@/app/modelSummary"
import { useServiceAccount } from "@/composables/app/useServiceAccount"
import { useServiceCatalog } from "@/composables/app/useServiceCatalog"
import { BridgeClient, BridgeError, type Methods, type ProjectSnapshot } from "@/bridge/client"

export type ConnectionWorkspaceProps = {
    bridge: BridgeClient
    activeProject?: ProjectSnapshot
    disabled?: boolean
    operationLocked?: boolean
    desktopRefreshKey?: number
}
export type ConnectionWorkspaceEmit = {
    (event: "connected", ...args: [value: ProjectSnapshot]): void
    (event: "disconnected", ...args: []): void
    (event: "busy", ...args: [value: boolean]): void
    (event: "viewModel", ...args: []): void
}

/** 管理 模型连接来源、账号目录和连接请求排空; props 为当前响应式输入, emit 转发权威结果, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useConnectionWorkspace(props: ConnectionWorkspaceProps, emit: ConnectionWorkspaceEmit) {
    const { t, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const source = ref(
        props.activeProject?.connection?.kind === "powerbi-service" ? "service" : props.activeProject?.connection?.kind === "ssas-xmla" ? "server" : "desktop",
    )
    const requestLoading = ref(false)
    const operationMessageVisible = ref(false)
    const instanceLoading = ref(false)
    const remoteLoading = ref(false)
    const loading = computed(() => requestLoading.value || instanceLoading.value || remoteLoading.value)
    const blocked = computed(() => loading.value || props.disabled === true)
    // 显式区分父级临时锁与待保存等业务禁用, 子请求不能将已有业务禁用伪装成可用外观.
    const preserveAppearance = computed(() => !!((loading.value || props.operationLocked) && (!props.disabled || props.operationLocked)))
    const pendingMethod = ref<keyof Methods>()
    const remoteConnecting = ref(false)
    const desktopConnectionRevision = ref(0)
    const serviceLoginPhase = ref<"discoveringEnvironment" | "authenticating">("discoveringEnvironment")
    /** 根据当前请求和宿主固定阶段生成状态提示; SDK 未提供百分比, 不推算进度. */
    const loadingMessage = computed(() => {
        switch (pendingMethod.value) {
            case "project.connectService":
                return t("connection.connectingService")
            case "powerbi.serviceSignIn":
                return t(serviceLoginPhase.value === "discoveringEnvironment" ? "connection.discoveringEnvironment" : "connection.signingIn")
            case "powerbi.serviceDeleteLogin":
                return t("connection.deletingRecent")
            case "powerbi.serviceModels":
            case "powerbi.serviceRecentModels":
                return t("connection.loadingModels")
            case "powerbi.serviceStatus":
                return t("connection.loadingStatus")
            case "powerbi.serviceSignOut":
                return t("connection.signingOut")
            case "project.open":
                return t(remoteConnecting.value ? "connection.connectingDesktop" : "connection.openingDesktopFile")
            default:
                return t("connection.processing")
        }
    })
    const error = ref("")
    const cancellationRequested = ref(false)
    const currentServer = computed(() => (props.activeProject?.connection?.kind === "ssas-xmla" ? props.activeProject : undefined))
    const requestId = ref<string>()
    let mounted = true
    const {
        status,
        loginForm,
        login,
        environments,
        recentLoginId,
        serviceRecents,
        recentStorageWarning,
        loginRules,
        signIn,
        selectRecentLogin,
        deleteRecentLogin,
        signOut,
    } = useServiceAccount({
        blocked,
        serviceLoginPhase,
        call,
        run,
        loadModels: () => loadModels(),
        resetModels: () => {
            models.value = []
            workspace.value = ""
            modelId.value = ""
        },
        isMounted: () => mounted,
        isServiceConnected: () => props.activeProject?.connection?.kind === "powerbi-service",
        onDisconnected: () => emit("disconnected"),
    })

    const {
        models,
        workspace,
        modelId,
        workspaces,
        visibleModels,
        selectedModel,
        currentService,
        selectedIsCurrent,
        modelLabel,
        loadModels,
        loadServiceRecents,
        recentStorageReady,
        openServiceRecent,
        deleteServiceRecent,
    } = useServiceCatalog({
        source,
        blocked,
        status,
        serviceRecents,
        recentStorageWarning,
        call,
        run,
        getActiveProject: () => props.activeProject,
        isMounted: () => mounted,
        accept,
        onViewModel: () => emit("viewModel"),
    })
    useOperationMessage(() =>
        requestLoading.value && operationMessageVisible.value && pendingMethod.value
            ? {
                  message: loadingMessage.value,
                  cancel: () => void cancel(),
                  cancelLabel: t("connection.cancel"),
                  cancelDisabled: cancellationRequested.value || !requestId.value,
              }
            : undefined,
    )

    /** 在途连接锁定来源和导航, 允许取消并等待后端终态. */
    function setBusy(value: boolean) {
        requestLoading.value = value
        emit("busy", loading.value)
    }
    /** 合并已开实例扫描和文件连接状态, 任一请求未结束时保持导航和来源锁定. */
    function setInstanceBusy(value: boolean) {
        instanceLoading.value = value
        emit("busy", loading.value)
    }
    /** 远端表单空闲编辑不锁定导航; 真实元数据、连接和记录操作才传播 busy. */
    function setRemoteBusy(value: boolean) {
        remoteLoading.value = value
        emit("busy", loading.value)
    }
    /** 顺序追踪桥接请求及提示; 每个新请求恢复独立取消, 迟到响应只影响原请求标识. */
    async function call<M extends keyof Methods>(method: M, params: Methods[M]["params"]) {
        pendingMethod.value = method
        remoteConnecting.value = false
        cancellationRequested.value = false
        const request =
            method === "project.open" || method === "powerbi.serviceSignIn"
                ? props.bridge.request(method, params, undefined, (phase) => {
                      if (!mounted || pendingMethod.value !== method || !requestLoading.value) return
                      if (phase === "connecting") remoteConnecting.value = true
                      if (phase === "discoveringEnvironment" || phase === "authenticating") serviceLoginPhase.value = phase
                  })
                : props.bridge.request(method, params)
        requestId.value = request.id
        return await request.promise
    }
    /** 执行操作并保留原模型; showProgress 仅控制在途消息, 后台核验仍保留操作锁及错误. */
    async function run(action: () => Promise<void>, showProgress = true) {
        if (blocked.value) return
        operationMessageVisible.value = showProgress
        setBusy(true)
        error.value = ""
        cancellationRequested.value = false
        try {
            await action()
        } catch (e) {
            if (mounted) {
                const message = e instanceof Error ? e.message : t("connection.failed")
                if (e instanceof BridgeError && e.code === "CANCELLED") showFeedback({ message, type: "info" })
                else error.value = message
            }
        } finally {
            requestId.value = undefined
            pendingMethod.value = undefined
            remoteConnecting.value = false
            cancellationRequested.value = false
            operationMessageVisible.value = false
            setBusy(false)
        }
    }
    /** 仅发布宿主连接成功快照, 应用外层在整个 busy 请求链排空后统一导航. */
    function accept(snapshot: ProjectSnapshot) {
        if (!mounted) return
        emit("connected", snapshot)
    }

    /** 当前卡片只发出查看事件; 面板被锁定时禁止导航, 不重复请求连接. */
    function viewModel() {
        if (!blocked.value) emit("viewModel")
    }

    /** 所选目录卡片通过精确身份区分查看与连接, 不可用条目保持拒绝连接. */
    function activateServiceModel() {
        if (blocked.value || !selectedModel.value || selectedModel.value.unavailableReason) return
        if (selectedIsCurrent.value) viewModel()
        else void connectSelected()
    }
    /** 文件使用系统选择器; Service 只发送已发布的模型标识, 远端由平铺表单独立提交. */
    async function connectSelected() {
        if (source.value === "server") return
        if (source.value === "service" && (!selectedModel.value || selectedModel.value.unavailableReason || selectedIsCurrent.value)) return
        await run(async () => {
            const snapshot = source.value === "service" ? await call("project.connectService", { modelId: modelId.value }) : await call("project.open", {})
            accept(snapshot)
            if (source.value === "desktop") desktopConnectionRevision.value++
            if (source.value === "service" && mounted) await loadServiceRecents()
        })
    }
    /** 对当前登录或连接发送一次取消; 取消确认后继续等待权威终态, 失败时允许重试. */
    async function cancel() {
        const activeRequestId = requestId.value
        if (!activeRequestId || cancellationRequested.value) return
        cancellationRequested.value = true
        try {
            await props.bridge.request("request.cancel", { requestId: activeRequestId }).promise
        } catch (e) {
            if (mounted && requestId.value === activeRequestId) {
                cancellationRequested.value = false
                error.value = e instanceof Error ? e.message : t("connection.cancelFailed")
            }
        }
    }
    /** 最近文件等外部入口切换工作区后显示实际来源, 原工作区刷新保留用户的目录选择. */
    watch(
        () => [props.activeProject?.connection?.kind, props.activeProject?.projectId],
        ([kind, projectId], [previousKind, previousProjectId]) => {
            if (kind && (kind !== previousKind || projectId !== previousProjectId))
                source.value = kind === "powerbi-service" ? "service" : kind === "ssas-xmla" ? "server" : "desktop"
        },
    )
    watch(
        source,
        async (current) => {
            error.value = ""
            if (current !== "service") return
            serviceRecents.value = []
            recentStorageWarning.value = ""
            await run(async () => {
                const result = await call("powerbi.serviceStatus", {})
                if (!mounted) return
                status.value = result
                serviceRecents.value = []
                recentStorageWarning.value = ""
                if (!result.account && !login.value.email && result.recentLogins.length) {
                    recentLoginId.value = result.recentLogins[0]!.id
                    selectRecentLogin()
                }
                if (result.account) await loadModels()
            }, false)
        },
        { immediate: true },
    )
    onUnmounted(() => {
        mounted = false
        void cancel()
    })
    return {
        source,
        loading,
        blocked,
        preserveAppearance,
        pendingMethod,
        desktopConnectionRevision,
        error,
        status,
        loginForm,
        login,
        environments,
        recentLoginId,
        serviceRecents,
        recentStorageWarning,
        recentStorageReady,
        loginRules,
        models,
        workspace,
        modelId,
        workspaces,
        visibleModels,
        selectedModel,
        currentService,
        currentServer,
        selectedIsCurrent,
        modelLabel,
        setInstanceBusy,
        setRemoteBusy,
        run,
        accept,
        loadModels,
        openServiceRecent,
        deleteServiceRecent,
        viewModel,
        activateServiceModel,
        signIn,
        selectRecentLogin,
        deleteRecentLogin,
        signOut,
        connectSelected,
        t,
        hostText,
        projectSummary,
    }
}
