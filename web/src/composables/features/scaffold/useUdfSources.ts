/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfSources.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF来源切换和受宿主保护的自定义来源登记管理.
 */
import type { UdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import { normalizeUdfRepositoryUrl, validateUdfRepositoryReference, validateUdfSourceName } from "@/features/scaffold/udfSourceRepository"
/** 页面只传登记来源 ID, 删除成功才清除同源选择, 不按名称自动改选.
 * @param state 当前来源登记、草稿和显式函数选择.
 * @param requests 当前挂载的串行请求入口.
 * @param listLocal 明确切换本地来源时读取本地库.
 * @returns 来源切换与管理动作; 校验和请求失败保留错误及原登记.
 */
export function useUdfSources(state: UdfState, requests: UdfRequests, listLocal: () => Promise<void>, loadCatalog: () => Promise<void>) {
    const {
        source,
        query,
        searched,
        prerelease,
        catalogCache,
        catalogRetrievedAt,
        error,
        sources,
        gitHubOnly,
        sourceManager,
        sourceName,
        sourceUrl,
        sourceDeleteTarget,
        model,
    } = state
    const { t } = useI18n()
    const { call } = requests
    /** 来源切换只触发明确本地库读取, 保留已有显式选择. */
    async function changeSource(): Promise<void> {
        query.value = ""
        state.packageItems.value = []
        state.total.value = 0
        state.page.value = 1
        state.packageTags.value = {}
        state.tagOptions.value = {}
        state.icons.value = {}
        state.detail.value = undefined
        state.uncertain.value = false
        state.catalogStatus.value = undefined
        searched.value = false
        prerelease.value = false
        catalogCache.value = false
        catalogRetrievedAt.value = null
        error.value = ""
        if (source.value === "local") await listLocal()
        else if (source.value) await loadCatalog()
    }
    /** 来源登记是本地元数据读取, 即使可选步骤未启用也不发起网络请求. */
    async function listSources(): Promise<void> {
        const result = await call("udf.sources", {}, true)
        if (result) {
            sources.value = result.items
            gitHubOnly.value = result.gitHubOnly
        }
    }
    /** 打开管理器并重读来源和更新偏好, 不联网. */
    async function openSources(): Promise<void> {
        gitHubOnly.value = undefined
        sourceManager.value = true
        await listSources()
    }
    /** 保存下次更新使用的偏好; 请求拒绝时保留已确认的开关状态.
     * @param value 是否强制从 GitHub 更新内置 DAXLib.
     */
    async function saveDaxLibPolicy(value: boolean): Promise<void> {
        if (gitHubOnly.value === undefined || gitHubOnly.value === value || state.busy.value) return
        const result = await call("udf.daxlibPolicySave", { gitHubOnly: value })
        if (result) gitHubOnly.value = result.gitHubOnly
    }
    /** 输入先做 HTTP(S) 仓库及引用校验, 宿主继续独立验证和原子持久化; 保存本身不联网. */
    async function saveSource(): Promise<void> {
        let baseUrl: string, gitRef: string | null
        try {
            validateUdfSourceName(sourceName.value.trim())
            baseUrl = normalizeUdfRepositoryUrl(sourceUrl.value.trim())
            gitRef = validateUdfRepositoryReference(state.sourceGitRef.value.trim() || null)
        } catch {
            error.value = t("scaffold.sourceInvalid")
            return
        }
        const value = await call("udf.sourceSave", {
            name: sourceName.value.trim(),
            baseUrl,
            protocol: "git",
            gitRef,
        })
        if (value) {
            sources.value = [...sources.value.filter((item) => item.id !== value.id), value]
            sourceName.value = ""
            sourceUrl.value = ""
            state.sourceGitRef.value = ""
        }
    }
    /** 只有删除权威终态才能清除相应来源选择, 不自动改选其他来源. */
    async function removeSource(): Promise<void> {
        const current = sourceDeleteTarget.value
        if (!current?.baseline) return
        const result = await call("udf.sourceDelete", { id: current.id, baseline: current.baseline, confirm: true })
        if (result?.deleted) {
            sources.value = sources.value.filter((item) => item.id !== current.id)
            model.value = model.value.filter((item) => item.source !== current.id)
            sourceDeleteTarget.value = undefined
            if (source.value === current.id) {
                source.value = ""
                await changeSource()
            }
        }
    }
    /** 固定来源标签本地化, 第三方名称保留用户原文. */
    function sourceLabel(id: string): string {
        return id === "local" ? t("scaffold.local") : (sources.value.find((item) => item.id === id)?.name ?? id)
    }
    return { changeSource, listSources, openSources, saveDaxLibPolicy, saveSource, removeSource, sourceLabel }
}
export type UdfSources = ReturnType<typeof useUdfSources>
