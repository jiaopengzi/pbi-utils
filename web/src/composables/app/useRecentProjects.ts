/**
 * FilePath    : pbi-utils\web\src\composables\app\useRecentProjects.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本机最近工程记录、显示偏好及准确记录删除, 保留宿主权威回读.
 */
import { computed, ref, type Ref, type ShallowRef } from "vue"
import type { BridgeClient, Methods } from "@/bridge/client"
import { setUiSettings } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"

/** bridge 为本应用会话; busy/initializing/pendingChanges 保留全局禁止条件, error 接收共享留页错误, isMounted 阻止迟到结果; 返回列表、操作锁及两种用户操作, 失败保留已有记录和偏好. */
export function useRecentProjects(
    bridge: ShallowRef<BridgeClient | undefined>,
    busy: Readonly<Ref<boolean>>,
    initializing: Ref<boolean>,
    pendingChanges: Readonly<Ref<boolean>>,
    error: Ref<string>,
    isMounted: () => boolean,
) {
    const { showRecentFiles, t } = useI18n()
    const { showFeedback } = useFeedback()
    const preferenceSaving = ref(false)
    const recentDeleting = ref(false)
    const recentProjects = ref<Methods["project.recent"]["result"]>([])
    const recentLocalItems = computed(() => recentProjects.value.map((item) => ({ id: item.id, name: item.name, detail: item.path })))

    /** 仅保存连接页显示字段, 宿主锁内保留其他偏好最新值; 成功回读才隐藏最近文件, 失败保持原状态. */
    async function updateRecentVisibility(value: boolean) {
        if (!bridge.value || busy.value || initializing.value || value === showRecentFiles.value) return
        preferenceSaving.value = true
        error.value = ""
        try {
            const settings = await bridge.value.request("ui.saveSettings", { showRecentFiles: value }).promise
            if (isMounted()) setUiSettings(settings)
        } catch (cause) {
            if (isMounted()) error.value = cause instanceof Error ? cause.message : t("uiSettings.failed")
        } finally {
            if (isMounted()) preferenceSaving.value = false
        }
    }

    /** 仅删除已确认的宿主最近记录; 失败保留列表, 成功刷新权威历史且不影响当前模型. */
    async function deleteRecentProject(id: string) {
        if (!bridge.value || busy.value || initializing.value || pendingChanges.value || !recentProjects.value.some((item) => item.id === id)) return
        recentDeleting.value = true
        error.value = ""
        try {
            const result = await bridge.value.request("project.deleteRecent", { id }).promise
            if (!isMounted()) return
            recentProjects.value = recentProjects.value.filter((item) => item.id !== id)
            if (result.deleted) showFeedback({ message: t("app.recentRemoved"), type: "success" })
            const recent = await bridge.value.request("project.recent", {}).promise
            if (isMounted()) recentProjects.value = recent
        } catch (cause) {
            if (isMounted()) error.value = cause instanceof Error ? cause.message : t("app.recentRemoveFailed")
        } finally {
            if (isMounted()) recentDeleting.value = false
        }
    }

    return { preferenceSaving, recentDeleting, recentProjects, recentLocalItems, updateRecentVisibility, deleteRecentProject }
}
