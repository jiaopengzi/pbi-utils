/**
 * FilePath    : pbi-utils\web\src\composables\app\useProductUpdates.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 管理应用实例更新状态、启动后台检查和请求代际, 不获取模型操作锁.
 */
import { computed, onScopeDispose, ref, shallowRef, watch } from "vue"
import { BridgeError, type BridgeClient } from "@/bridge/client"
import type { ProductUpdateCheck, ProductUpdateSettings, ProductUpdateState, ProductUpdateTarget } from "@/bridge/updateContracts"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"

/**
 * 创建当前应用实例的更新通道; bridge 和 ready 明确控制启动, startup 控制是否自动检查.
 * 返回权威状态和受限操作; 保存失败保留旧设置, 取消或代际过期不展示迟到结果.
 */
export function useProductUpdates(bridge: () => BridgeClient | undefined, ready: () => boolean, startup = true) {
    const state = shallowRef<ProductUpdateState>()
    const currentVersion = ref("-")
    const loading = ref(false)
    const saving = ref(false)
    const checking = ref(false)
    const opening = ref(false)
    const cancellationRequested = ref(false)
    const error = ref("")
    const { t } = useI18n()
    const { showFeedback } = useFeedback()
    const notified = new Set<string>()
    // 仅当前已接受的完整检查拥有链接授权; 缓存回读、失败和失效请求均保守撤销, 不累计版本授权.
    const verifiedCheck = shallowRef<ProductUpdateCheck | null>(null)
    const activeRequests = new Map<string, BridgeClient>()
    let disposed = false
    let generation = 0
    let checkId = ""
    let checkAutomatic = false
    const checkResult = computed(() => (state.value?.lastCheck?.source === state.value?.settings.source ? state.value?.lastCheck : null))
    const linksVerified = computed(() => {
        const value = checkResult.value
        return !!value?.latestVersion && value === verifiedCheck.value
    })

    /** 将取消发送给原请求的桥接, 不把取消确认当作业务终态; 页面不显示原始宿主异常. */
    function cancelRequest(id: string): void {
        const client = activeRequests.get(id)
        if (client) void client.request("request.cancel", { requestId: id }).promise.catch(() => undefined)
    }
    /** 失效当前检查身份并请求取消, 旧 finally 不会解锁新请求. */
    function invalidateCheck(): void {
        generation++
        verifiedCheck.value = null
        if (checkId && !cancellationRequested.value) {
            cancellationRequested.value = true
            cancelRequest(checkId)
        }
    }
    /** 用户取消后等待原请求终态才解除检查守卫; 面板卸载可只取消手动检查, 保留全局启动任务. */
    function cancelCheck(manualOnly = false): void {
        if (!checkId || (manualOnly && checkAutomatic)) return
        invalidateCheck()
    }
    /** 只在当前应用实例首次见到非模拟新版本时发非阻塞通知, 不占用模型导航锁. */
    function notifyAvailable(): void {
        const value = checkResult.value
        if (!value || value.status !== "available" || !value.latestVersion || value.isSimulation || notified.has(value.latestVersion)) return
        notified.add(value.latestVersion)
        showFeedback({ message: t("updates.availableNotice", { version: value.latestVersion }), type: "info" })
    }
    /** 读取设置及产品版本; 仅明确的自动启动路径发后台检查, 失败可在设置重试. */
    async function load(): Promise<void> {
        const client = bridge()
        if (!client || loading.value || disposed) return
        loading.value = true
        verifiedCheck.value = null
        error.value = ""
        const request = client.request("updates.status", {})
        const info = client.request("app.info", {})
        activeRequests.set(request.id, client)
        activeRequests.set(info.id, client)
        try {
            const [settings, version] = await Promise.allSettled([request.promise, info.promise])
            if (disposed || client !== bridge()) return
            if (settings.status === "rejected") {
                error.value = t("updates.loadFailed")
                return
            }
            state.value = settings.value
            currentVersion.value = version.status === "fulfilled" ? version.value.version : (settings.value.lastCheck?.currentVersion ?? "-")
            if (startup && settings.value.settings.automaticCheck) void check(true)
        } catch {
            if (!disposed && client === bridge()) error.value = t("updates.loadFailed")
        } finally {
            activeRequests.delete(request.id)
            activeRequests.delete(info.id)
            if (!disposed) loading.value = false
        }
    }
    /** 仅提交本次变更字段, 宿主锁内合并最新设置后回读; 失败不改变设置, 缓存回读不继承检查授权. */
    async function save(patch: Partial<ProductUpdateSettings>): Promise<void> {
        const client = bridge()
        if (!client || !state.value || saving.value || disposed || !Object.keys(patch).length) return
        invalidateCheck()
        saving.value = true
        error.value = ""
        const request = client.request("updates.save", patch)
        activeRequests.set(request.id, client)
        try {
            const value = await request.promise
            if (disposed || client !== bridge()) return
            state.value = value
            showFeedback({ message: t(__MOCK_BRIDGE__ ? "updates.simulationSaved" : "updates.saved"), type: __MOCK_BRIDGE__ ? "info" : "success" })
        } catch {
            if (!disposed) showFeedback({ message: t("updates.saveFailed"), type: "error" })
        } finally {
            activeRequests.delete(request.id)
            if (!disposed) saving.value = false
        }
    }
    /** 使用当前已保存来源检查; 自动错误只保留状态, 手动错误反馈; 代际失效后不更新或提示. */
    async function check(automatic = false): Promise<void> {
        const client = bridge()
        const settings = state.value?.settings
        if (!client || !settings || checking.value || saving.value || disposed || (automatic && !settings.automaticCheck)) return
        const identity = ++generation
        const source = settings.source
        verifiedCheck.value = null
        checking.value = true
        checkAutomatic = automatic
        error.value = ""
        const request = client.request("updates.check", { source, automatic })
        checkId = request.id
        activeRequests.set(request.id, client)
        try {
            const response = await request.promise
            if (disposed || generation !== identity || client !== bridge() || state.value?.settings.source !== source) return
            state.value = { settings: state.value.settings, lastCheck: response.check?.source === source ? response.check : null }
            if (response.check) currentVersion.value = response.check.currentVersion
            if (
                response.checkedNow &&
                response.check?.source === source &&
                ["available", "current"].includes(response.check.status) &&
                response.check.latestVersion
            )
                verifiedCheck.value = response.check
            notifyAvailable()
            if (!automatic && response.check?.status !== "available")
                showFeedback({ message: t(`updates.${response.check?.status ?? "notChecked"}`), type: response.check?.status === "failed" ? "error" : "info" })
        } catch (cause) {
            if (disposed || generation !== identity || (cause instanceof BridgeError && cause.code === "CANCELLED")) return
            error.value = t("updates.failed")
            if (!automatic) showFeedback({ message: error.value, type: "error" })
        } finally {
            activeRequests.delete(request.id)
            if (!disposed && checkId === request.id) {
                checkId = ""
                checking.value = false
                cancellationRequested.value = false
            }
        }
    }
    /** 仅对当前成功检查中的精确版本和资产启用入口, 真实 URL 继续由宿主核验. */
    function canOpen(target: ProductUpdateTarget): boolean {
        const value = checkResult.value
        return (
            !checking.value &&
            !saving.value &&
            !opening.value &&
            !error.value &&
            linksVerified.value &&
            !!value &&
            ["available", "current"].includes(value.status) &&
            (target === "release" ? !!value.releaseUrl : value.assets.some((asset) => asset.dotNetMode === target))
        )
    }
    /** 用户点击才提交 source/version/target, 不传任意 URL; 模拟与未打开均不声明成功. */
    async function open(target: ProductUpdateTarget): Promise<void> {
        const client = bridge()
        const value = checkResult.value
        if (!client || !value?.latestVersion || !canOpen(target) || disposed) return
        opening.value = true
        const identity = generation
        const request = client.request("updates.open", { source: value.source, version: value.latestVersion, target })
        activeRequests.set(request.id, client)
        try {
            const response = await request.promise
            if (disposed || generation !== identity) return
            showFeedback({
                message: t(response.isSimulation ? "updates.simulationOpen" : response.opened ? "updates.opened" : "updates.openFailed"),
                type: response.isSimulation ? "info" : response.opened ? "success" : "error",
            })
        } catch {
            if (!disposed && generation === identity) {
                verifiedCheck.value = null
                showFeedback({ message: t("updates.openFailed"), type: "error" })
            }
        } finally {
            activeRequests.delete(request.id)
            if (!disposed) opening.value = false
        }
    }
    watch(
        [bridge, ready],
        ([client, available]) => {
            if (client && available && !state.value) void load()
        },
        { immediate: true },
    )
    onScopeDispose(() => {
        disposed = true
        generation++
        for (const id of activeRequests.keys()) cancelRequest(id)
    })
    return {
        state,
        currentVersion,
        loading,
        saving,
        checking,
        opening,
        cancellationRequested,
        error,
        checkResult,
        linksVerified,
        load,
        save,
        check,
        cancelCheck,
        canOpen,
        open,
    }
}
export type ProductUpdates = ReturnType<typeof useProductUpdates>
