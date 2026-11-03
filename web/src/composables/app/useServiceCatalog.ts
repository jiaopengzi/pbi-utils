/**
 * FilePath    : pbi-utils\web\src\composables\app\useServiceCatalog.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按当前账号与云核验 Service 模型目录、工作区选择及最近模型记录.
 */
import { computed, nextTick, readonly, ref, watch, type Ref } from "vue"
import type { PowerBiServiceModel, PowerBiServiceStatus, ProjectSnapshot, RecentPowerBiServiceModel } from "@/bridge/client"
import type { ServiceAccountCall } from "@/composables/app/useServiceAccount"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"

type ServiceCatalogOptions = {
    source: Ref<string>
    blocked: Readonly<Ref<boolean>>
    status: Ref<PowerBiServiceStatus | undefined>
    serviceRecents: Ref<RecentPowerBiServiceModel[]>
    recentStorageWarning: Ref<string>
    call: ServiceAccountCall
    run: (action: () => Promise<void>, showProgress?: boolean) => Promise<void>
    getActiveProject: () => ProjectSnapshot | undefined
    isMounted: () => boolean
    accept: (snapshot: ProjectSnapshot) => void
    onViewModel: () => void
}
/** 工作区重名时仍以服务标识分组; 个人工作区使用其名称作为后备. */
function workspaceKey(model: PowerBiServiceModel) {
    return model.workspaceId || model.workspace
}

/** options 绑定当前账号、来源和同一连接请求门闩; 返回精确模型选择与历史操作, 失败撤销旧目录可用性, 不按同名推断身份或重放连接. */
export function useServiceCatalog({
    source,
    blocked,
    status,
    serviceRecents,
    recentStorageWarning,
    call,
    run,
    getActiveProject,
    isMounted,
    accept,
    onViewModel,
}: ServiceCatalogOptions) {
    const { t } = useI18n()
    const { showFeedback } = useFeedback()
    const models = ref<PowerBiServiceModel[]>([])
    const recentStorageReady = ref(false)
    const workspace = ref("")
    const modelId = ref("")
    const workspaces = computed(() => [
        ...new Map(models.value.map((model) => [workspaceKey(model), { id: workspaceKey(model), name: model.workspace }])).values(),
    ])
    const visibleModels = computed(() => models.value.filter((model) => workspaceKey(model) === workspace.value))
    const selectedModel = computed(() => visibleModels.value.find((model) => model.id === modelId.value))
    const currentService = computed(() => (getActiveProject()?.connection?.kind === "powerbi-service" ? getActiveProject() : undefined))
    /** Service 当前目录条目只按宿主绑定的非空句柄匹配, 标题和模型名称不能代替身份. */
    const selectedIsCurrent = computed(() => !!currentService.value?.connection?.modelId && currentService.value.connection.modelId === selectedModel.value?.id)

    /** 重名模型附带服务标识, 帮助用户区分独立的目录条目. */
    function modelLabel(model: PowerBiServiceModel) {
        const repeated = visibleModels.value.filter((item) => item.name === model.name).length > 1
        return `${model.name}${repeated ? ` · ${model.modelId || model.id}` : ""}${model.unavailableReason ? t("connection.unsupported") : ""}`
    }

    /** 账号目录刷新后清理已失效选择, 同名模型仍以独立句柄区分. */
    async function loadModels() {
        recentStorageReady.value = false
        serviceRecents.value = []
        recentStorageWarning.value = ""
        models.value = []
        try {
            const recent = await loadServiceRecents(false)
            const result = await call("powerbi.serviceModels", {})
            if (!isMounted()) return
            models.value = result
            // 两次读取均成功后才发布历史, 以最后的目录再次剔除已撤销或不可连接的模型.
            serviceRecents.value = recent.items.flatMap((item) => {
                const model = result.find((entry) => entry.id === item.modelId && !entry.unavailableReason)
                return model ? [{ ...item, name: model.name, workspace: model.workspace }] : []
            })
            recentStorageWarning.value = recent.storageWarning ?? ""
            const current = result.find((model) => model.id === currentService.value?.connection?.modelId)
            if (!workspaces.value.some((item) => item.id === workspace.value))
                workspace.value = current ? workspaceKey(current) : (workspaces.value[0]?.id ?? "")
            // 工作区 watch 清空旧选项后再恢复宿主绑定的当前目录条目, 不用名称推断当前模型.
            await Promise.resolve()
            if (!visibleModels.value.some((model) => model.id === modelId.value)) modelId.value = ""
            if (!modelId.value && current && workspaceKey(current) === workspace.value) modelId.value = current.id
            recentStorageReady.value = true
        } catch (cause) {
            serviceRecents.value = []
            recentStorageWarning.value = ""
            throw cause
        }
    }

    /** 读取当前账号、云和最新权限核验的历史; publish 为 false 时暂存结果, 等完整目录成功后发布. */
    async function loadServiceRecents(publish = true) {
        recentStorageReady.value = false
        serviceRecents.value = []
        recentStorageWarning.value = ""
        const result = await call("powerbi.serviceRecentModels", {})
        if (isMounted() && status.value?.account && publish) {
            serviceRecents.value = result.items
            recentStorageWarning.value = result.storageWarning ?? ""
            recentStorageReady.value = true
        }
        return result
    }

    /** 重开前刷新目录并按最新宿主句柄同步目标; 工作区选择清空旧模型后再回填, 失败保留实际当前模型. */
    async function openServiceRecent(id: string) {
        if (blocked.value || source.value !== "service" || !status.value?.account) return
        let viewCurrent = false
        await run(async () => {
            await loadModels()
            const item = serviceRecents.value.find((recent) => recent.id === id)
            const target = models.value.find((model) => model.id === item?.modelId && !model.unavailableReason)
            if (!item || !target) throw new Error(t("connection.recentUnavailable"))
            workspace.value = workspaceKey(target)
            await nextTick()
            if (!isMounted()) return
            modelId.value = target.id
            if (currentService.value?.connection?.modelId === item.modelId) {
                viewCurrent = true
                return
            }
            accept(await call("project.connectService", { modelId: item.modelId }))
            if (isMounted()) await loadServiceRecents()
        })
        if (isMounted() && viewCurrent) onViewModel()
    }

    /** 只按已展示的当前账号记录删除历史, 保留当前连接和目录选择; 失败可重试. */
    async function deleteServiceRecent(id: string) {
        if (blocked.value || source.value !== "service" || !status.value?.account || !serviceRecents.value.some((item) => item.id === id)) return
        await run(async () => {
            const result = await call("powerbi.serviceDeleteRecentModel", { id })
            if (!isMounted()) return
            serviceRecents.value = serviceRecents.value.filter((item) => item.id !== id)
            if (result.deleted) showFeedback({ message: t("app.recentRemoved"), type: "success" })
        })
    }

    watch(workspace, () => {
        modelId.value = ""
    })
    // 来源切换后的登录核验也属于未读取状态; 失败或取消不能把临时空警告发布为真实清除.
    watch(source, () => {
        recentStorageReady.value = false
    })

    return {
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
        recentStorageReady: readonly(recentStorageReady),
        openServiceRecent,
        deleteServiceRecent,
    }
}
