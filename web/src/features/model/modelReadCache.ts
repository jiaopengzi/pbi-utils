/**
 * FilePath    : pbi-utils\web\src\features\model\modelReadCache.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前桥接会话的单份模型读取缓存, 显式刷新与断开撤销代际, 不保存授权或失败结果.
 */
import { ref, type Ref } from "vue"
import type { BridgeClient, ModelExportLocation, ModelTableStatisticsResult, ProjectSnapshot } from "@/bridge/client"
import { modelEditTargetIdentity } from "./modelEditSession"

interface ModelReadCache {
    revision: Ref<number>
    statistics?: { identity: string; result: ModelTableStatisticsResult }
    exportLocation?: ModelExportLocation
}

const sessions = new WeakMap<BridgeClient, ModelReadCache>()

/** 返回桥接会话唯一内存缓存; 不持久化, 不跨应用或连接客户端复用. */
export function modelReadCache(bridge: BridgeClient): ModelReadCache {
    let cache = sessions.get(bridge)
    if (!cache) {
        cache = { revision: ref(0) }
        sessions.set(bridge, cache)
    }
    return cache
}

/** 统计绑定完整目标、基线和保存事实, 同名模型或恢复状态变化不能沿用旧结果. */
export function modelReadIdentity(project: ProjectSnapshot): string {
    return JSON.stringify([modelEditTargetIdentity(project), project.baselineId, project.hasPendingChanges === true, project.saveResult ?? null])
}

/** 显式刷新、断开或新连接后撤销旧读取结果; 响应式代际使已挂载页面重读且拒绝迟到响应. */
export function invalidateModelReads(bridge: BridgeClient): void {
    const cache = modelReadCache(bridge)
    cache.statistics = undefined
    cache.exportLocation = undefined
    cache.revision.value++
}
