/**
 * FilePath    : pbi-utils\web\src\composables\app\useApplicationWorkspace.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 应用桥接启动、权威工作区与全局导航锁生命周期.
 */
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { ElMessageBox } from "element-plus"
import { hasPendingModelUpdate, isConnectedDesktopProject, isModelUpdateComplete, isReadOnlyProject, requiresModelVerification } from "@/bridge/modelAccess"
import { useRecentProjects } from "@/composables/app/useRecentProjects"
import { reconcileProjectSnapshot } from "@/app/reconcileProjectSnapshot"
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue"
import { elementLocale, setUiSettings } from "@/i18n"
import { BridgeClient, BridgeError, hostTransport, type ProjectSnapshot } from "@/bridge/client"
import { modelEditTargetIdentity, type ModelEditorState, type ModelEditSession } from "@/features/model/modelEditSession"
import { invalidateModelReads, modelReadCache, modelReadIdentity } from "@/features/model/modelReadCache"
/** 管理桥接启动、权威工作区与全局导航锁; 返回视图绑定及宿主操作; 请求失败保留错误和输入. */
export function useApplicationWorkspace() {
    const { showRecentFiles, t, hostText } = useI18n()

    const modules = computed(() => [
        ["connections", t("app.connections"), "plug"],
        ["model", t("app.model"), "database"],
        ["new", t("app.new"), "folder"],
        ["sample", t("app.sample"), "flask-conical"],
        ["learning", t("learning.title"), "book-open"],
        ["system", t("app.settings"), "settings"],
    ])
    const page = ref("connections")
    const collapsed = ref(false)
    const targetTab = ref("")
    const navigationId = ref(0)
    const pageOperationBusy = ref(false)
    const modelPanelBusy = ref(false)
    const modelEditorBusy = ref(false)
    // 路由用例、模型面板和持久属性编辑器各自拥有忙状态, 任一通道结束都不能解除其他通道的锁.
    const operationBusy = computed({
        get: () => pageOperationBusy.value || modelPanelBusy.value || modelEditorBusy.value,
        set: (value: boolean) => (pageOperationBusy.value = value),
    })
    const recentConfirming = ref(false)
    const hostDialogActive = ref(false)
    const projectLoading = ref(false)
    const projectInspecting = ref(false)
    const activeProjectRequestId = ref("")
    const projectCancellationRequested = ref(false)
    const requestBusy = computed(() => operationBusy.value || preferenceSaving.value || projectLoading.value || recentDeleting.value)
    const busy = computed(() => requestBusy.value || recentConfirming.value || hostDialogActive.value)
    const { showFeedback } = useFeedback()
    const initializing = ref(true)
    let mounted = true
    let startingBridge: BridgeClient | undefined
    const project = shallowRef<ProjectSnapshot>()
    const modelEditSession = shallowRef<ModelEditSession>()
    const modelEditorIdentity = computed(() => (project.value && !isReadOnlyProject(project.value) ? modelEditTargetIdentity(project.value) : ""))
    const modelEditorCreatedFor = ref("")
    const modelEditorState = shallowRef<ModelEditorState>()
    const modelEditorCreated = computed(() => !!modelEditorIdentity.value && modelEditorCreatedFor.value === modelEditorIdentity.value)
    const modelEditorActive = computed(() => modelEditorCreated.value && page.value === "model" && modelEditorState.value?.active === true)
    const modelEditorLocked = computed(() => modelEditorState.value?.locked ?? true)
    // 只保留当前属性编辑器的 DOM 生命周期; 离开模型路由即隐藏, 不保活模型配置、统计或其他子面板.
    watch(page, () => (modelEditorState.value = undefined), { flush: "sync" })
    watch(
        modelEditorIdentity,
        () => {
            modelEditorCreatedFor.value = ""
            modelEditorState.value = undefined
        },
        { flush: "sync" },
    )
    /** 只接纳当前可写目标的模型面板状态; 首次 Tables 访问才创建编辑器, 旧目标或隐藏路由事件无效. */
    function acceptModelEditorState(state: ModelEditorState): void {
        if (page.value !== "model" || !modelEditorIdentity.value || state.targetIdentity !== modelEditorIdentity.value) return
        modelEditorState.value = state
        if (state.active) modelEditorCreatedFor.value = state.targetIdentity
    }
    // 会话只属于当前应用和当前宿主目标; 断开或换绑立即清除, 不积累多个模型的草稿.
    watch(
        () => project.value && modelEditTargetIdentity(project.value),
        (identity) => {
            if (modelEditSession.value?.targetIdentity !== identity) modelEditSession.value = undefined
        },
        { flush: "sync" },
    )
    /** 接收当前模型表说明的精确会话; 迟到的旧目标事件、模板及其它编辑分支不进入持久工作区. */
    function acceptModelEditSession(session: ModelEditSession): void {
        if (!project.value || session.targetIdentity !== modelEditTargetIdentity(project.value) || session.operation !== "table.set") return
        modelEditSession.value = session
    }
    const connectionNavigationPending = ref(false)
    const desktopRefreshKey = ref(0)
    const bridge = shallowRef<BridgeClient>()
    // 隐藏路由也丢弃旧目标/基线统计, 防止之后回到相同标识时复用更早的一份结果.
    watch(
        () => project.value && modelReadIdentity(project.value),
        () => {
            if (bridge.value) modelReadCache(bridge.value).statistics = undefined
        },
        { flush: "sync" },
    )
    const error = ref("")
    const saveMessage = ref("")
    const connectionMessage = ref("")
    useOperationMessage(() =>
        connectionMessage.value
            ? {
                  message: t(connectionMessage.value),
                  cancel: activeProjectRequestId.value ? cancelProjectRequest : undefined,
                  cancelLabel: t("connection.cancel"),
                  cancelDisabled: projectCancellationRequested.value,
              }
            : undefined,
    )
    const pendingChanges = computed(() => hasPendingModelUpdate(project.value))
    const { preferenceSaving, recentDeleting, recentProjects, recentLocalItems, updateRecentVisibility, deleteRecentProject } = useRecentProjects(
        bridge,
        busy,
        initializing,
        pendingChanges,
        error,
        () => mounted,
    )
    const needsVerification = computed(() => requiresModelVerification(project.value))
    const connectedDesktop = computed(() => isConnectedDesktopProject(project.value))
    const writable = computed(() => !!project.value && !isReadOnlyProject(project.value))
    const preview = __MOCK_BRIDGE__
    const base = import.meta.env.BASE_URL
    const title = computed(() => modules.value.find((m) => m[0] === page.value)?.[1] ?? t("app.connections"))
    const projectName = computed(() =>
        project.value?.projectPath.startsWith("model://") ? project.value.model.name : (project.value?.projectPath.split(/[\\/]/).pop() ?? t("")),
    )
    const online = computed(() => !!project.value?.projectPath.startsWith("model://"))
    const connectionLabel = computed(() =>
        project.value
            ? `${hostText(project.value.connection?.label) || (online.value ? t("app.onlineModel") : t("app.pbip"))}${writable.value ? t("app.readWrite") : t("app.readOnly")}`
            : t("app.disconnected"),
    )
    /** 生产必须连接真实桥接并先核验 app.info 响应; 浏览器模拟为显式构建模式, 失败保留连接错误. */
    async function connect() {
        try {
            const transport = preview ? new (await import("@/bridge/mock")).MockTransport(true, false) : hostTransport()
            const client = new BridgeClient(transport)
            startingBridge = client
            const info = await client.request("app.info", {}).promise
            if (!mounted) return
            try {
                const preferences = await client.request("ui.settings", {}).promise
                if (!mounted) return
                setUiSettings(preferences)
                if (preferences.warning) error.value = preferences.warning
            } catch (cause) {
                if (mounted) error.value = cause instanceof Error ? cause.message : t("app.connectionFailed")
            }
            try {
                const recent = await client.request("project.recent", {}).promise
                if (mounted) recentProjects.value = recent
            } catch (cause) {
                if (mounted) error.value = cause instanceof Error ? cause.message : t("app.recentFailed")
            }
            if (mounted) {
                bridge.value = client
                if (info.hasExternalToolTarget === true) await connectExternalTool()
            }
        } catch (e) {
            if (mounted) error.value = e instanceof Error ? e.message : t("app.connectionFailed")
            startingBridge?.dispose()
        } finally {
            if (mounted) initializing.value = false
        }
    }
    /** 消费当前桌面启动的唯一报表目标; 失败保留桥接和连接页, 取消等原请求终态后才解除初始化锁. */
    async function connectExternalTool() {
        if (!bridge.value || !mounted) return
        projectLoading.value = true
        projectCancellationRequested.value = false
        connectionMessage.value = "connection.connectingExternalTool"
        try {
            const request = bridge.value.request("project.connectExternalTool", {})
            activeProjectRequestId.value = request.id
            const snapshot = await request.promise
            if (!mounted) return
            acceptConnection(snapshot)
        } catch (cause) {
            if (mounted) {
                const message = cause instanceof Error ? cause.message : t("app.connectionFailed")
                if (cause instanceof BridgeError && cause.code === "CANCELLED") showFeedback({ message, type: "info" })
                else error.value = message
            }
        } finally {
            if (mounted) {
                projectLoading.value = false
                connectionMessage.value = ""
                activeProjectRequestId.value = ""
                projectCancellationRequested.value = false
            }
        }
    }
    /** 重开宿主绑定的最近文件或刷新当前模型; 按宿主阶段更新提示, 失败保留上一份快照. */
    async function openProject(refresh = false, recentId?: string) {
        if (!bridge.value || busy.value || (!refresh && !recentId)) return
        projectLoading.value = true
        projectInspecting.value = refresh
        projectCancellationRequested.value = false
        error.value = ""
        connectionMessage.value = refresh ? "app.refreshingModel" : "connection.connectingDesktop"
        try {
            const request = recentId
                ? bridge.value.request("project.openRecent", { id: recentId }, undefined, () => {
                      if (mounted) connectionMessage.value = "connection.connectingDesktop"
                  })
                : bridge.value.request("project.inspect", {})
            activeProjectRequestId.value = request.id
            const snapshot = await request.promise
            if (!mounted) return
            project.value = refresh ? reconcileProjectSnapshot(project.value, snapshot) : snapshot
            invalidateModelReads(bridge.value)
            if (recentId) {
                desktopRefreshKey.value++
                connectionNavigationPending.value = true
            }
            activeProjectRequestId.value = ""
            if (recentId) {
                const recent = await bridge.value.request("project.recent", {}).promise
                if (mounted) recentProjects.value = recent
            }
        } catch (e) {
            if (mounted) {
                const message = e instanceof Error ? e.message : t("app.projectFailed")
                if (e instanceof BridgeError && e.code === "CANCELLED") showFeedback({ message, type: "info" })
                else error.value = message
            }
        } finally {
            if (mounted) {
                projectLoading.value = false
                projectInspecting.value = false
                connectionMessage.value = ""
                activeProjectRequestId.value = ""
                projectCancellationRequested.value = false
            }
        }
    }
    /** 对当前文件连接或刷新发送一次取消, 原请求返回权威终态前继续锁定导航并保留旧模型. */
    async function cancelProjectRequest() {
        const requestId = activeProjectRequestId.value
        if (!bridge.value || !requestId || projectCancellationRequested.value) return
        projectCancellationRequested.value = true
        try {
            await bridge.value.request("request.cancel", { requestId }).promise
        } catch (cause) {
            if (mounted && activeProjectRequestId.value === requestId) {
                projectCancellationRequested.value = false
                error.value = cause instanceof Error ? cause.message : t("connection.cancelFailed")
            }
        }
    }
    /** 接收实际保存结果, 部分完成按警告展示, 不把引擎提交误报为文件保存. */
    function acceptEdit(snapshot: ProjectSnapshot) {
        if (!mounted) return
        project.value = snapshot
        const result = snapshot.saveResult
        saveMessage.value =
            result && connectedDesktop.value && (isModelUpdateComplete(snapshot) || (requiresModelVerification(snapshot) && !result.errorCode))
                ? ""
                : result && (result.errorCode || !result.modelSaved || !result.fileSaved || !result.desktopSynchronized)
                  ? result.message
                  : ""
    }
    /** Desktop 恢复只回读连接模型且不撤销或重放已发生提交; 离线目标沿用原保存和草稿恢复. */
    async function finishEdits(discard = false) {
        if (!bridge.value || busy.value || !pendingChanges.value || (needsVerification.value && !(connectedDesktop.value && discard))) return
        const identity = JSON.stringify([project.value?.projectId, project.value?.projectPath, project.value?.baselineId])
        try {
            if (discard)
                await ElMessageBox.confirm(
                    connectedDesktop.value ? t("app.reloadModelConfirm") : project.value?.saveResult?.errorCode ? t("app.discardSync") : t("app.discardDraft"),
                    t(connectedDesktop.value ? "app.reloadModel" : "app.discard"),
                    {
                        confirmButtonText: t(connectedDesktop.value ? "app.reloadModel" : "app.discard"),
                        cancelButtonText: t("app.cancel"),
                        type: "warning",
                    },
                )
        } catch {
            return
        }
        if (busy.value || identity !== JSON.stringify([project.value?.projectId, project.value?.projectPath, project.value?.baselineId])) return
        operationBusy.value = true
        error.value = ""
        saveMessage.value = ""
        try {
            const snapshot = await bridge.value.request(discard ? "model.discard" : "model.save", {}).promise
            acceptEdit(snapshot)
            if (!hasPendingModelUpdate(snapshot) && !saveMessage.value && snapshot.saveResult?.message)
                showFeedback({ message: snapshot.saveResult.message, type: preview ? "info" : "success" })
        } catch (e) {
            error.value = e instanceof Error ? e.message : t("app.saveFailed")
        } finally {
            operationBusy.value = false
        }
    }
    /** 按当前模块导航; 在途或未知目标不改变页面, tab 仅定位当前模块内区域. */
    function navigate(target: string, tab = "") {
        if (!busy.value && !initializing.value && modules.value.some(([key]) => key === target)) {
            if (page.value === target && targetTab.value === tab) return
            page.value = target
            targetTab.value = tab
            navigationId.value++
        }
    }
    /** 断开同时清除当前工作区展示和宿主持有的在线目标. */
    async function disconnect() {
        if (!bridge.value || busy.value) return
        operationBusy.value = true
        error.value = ""
        try {
            await bridge.value.request("project.disconnect", {}).promise
            project.value = undefined
            invalidateModelReads(bridge.value)
        } catch (e) {
            error.value = e instanceof Error ? e.message : t("app.disconnectFailed")
        } finally {
            operationBusy.value = false
        }
    }
    /** 接受已连接模型并排队进入模型页; 等待原请求链排空, 避免卸载中断后续上下文读取或留下导航锁. */
    function acceptConnection(snapshot: ProjectSnapshot) {
        if (!mounted) return
        if (bridge.value) invalidateModelReads(bridge.value)
        project.value = snapshot
        connectionNavigationPending.value = true
        saveMessage.value = ""
        error.value = ""
        if (bridge.value && (!snapshot.projectPath.startsWith("model://") || snapshot.connection?.kind === "desktop"))
            void bridge.value
                .request("project.recent", {})
                .promise.then((result) => (recentProjects.value = result))
                .catch((e: unknown) => {
                    error.value = e instanceof Error ? e.message : t("app.recentFailed")
                })
    }
    // 连接来源仍拥有完整请求链和 busy 生命周期; 成功导航不能绕过在途或初始化锁.
    watch(
        [connectionNavigationPending, busy, initializing],
        ([pending, blocked, starting]) => {
            if (!mounted || !pending || blocked || starting) return
            connectionNavigationPending.value = false
            navigate("model")
        },
        { flush: "post" },
    )
    onMounted(connect)
    onUnmounted(() => {
        mounted = false
        if (activeProjectRequestId.value && !projectCancellationRequested.value)
            void bridge.value?.request("request.cancel", { requestId: activeProjectRequestId.value }).promise.catch(() => undefined)
        startingBridge?.dispose()
        bridge.value?.dispose()
    })
    return {
        modules,
        page,
        collapsed,
        targetTab,
        navigationId,
        operationBusy,
        modelPanelBusy,
        modelEditorBusy,
        modelEditorIdentity,
        modelEditorCreated,
        modelEditorActive,
        modelEditorLocked,
        acceptModelEditorState,
        recentConfirming,
        hostDialogActive,
        projectInspecting,
        requestBusy,
        busy,
        initializing,
        project,
        modelEditSession,
        acceptModelEditSession,
        desktopRefreshKey,
        recentLocalItems,
        bridge,
        error,
        saveMessage,
        pendingChanges,
        needsVerification,
        connectedDesktop,
        writable,
        preview,
        base,
        title,
        projectName,
        connectionLabel,
        openProject,
        updateRecentVisibility,
        deleteRecentProject,
        acceptEdit,
        finishEdits,
        navigate,
        disconnect,
        acceptConnection,
        showRecentFiles,
        t,
        hostText,
        elementLocale,
    }
}
