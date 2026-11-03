/**
 * FilePath    : pbi-utils\web\src\features\model\operationFreshness.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : Desktop 操作准备前读取同一权威目标, 保留待处理状态和精确请求边界.
 */
import { BridgeError, type BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { hasPendingModelUpdate, isConnectedDesktopProject, isReadOnlyProject } from "@/bridge/modelAccess"

/** 仅比较宿主目标身份, 不把名称或可替换的基线当作另一个模型的授权. */
export function sameModelTarget(left: ProjectSnapshot, right: ProjectSnapshot): boolean {
    return (
        left.projectId === right.projectId &&
        left.projectPath === right.projectPath &&
        left.connection?.kind === right.connection?.kind &&
        left.connection?.instanceId === right.connection?.instanceId &&
        left.connection?.modelId === right.connection?.modelId
    )
}

/**
 * Desktop 操作准备前只读最新模型; onRequest 交接当前请求身份以支持取消.
 * 返回同目标权威快照, 其他来源保留原快照; 身份变化或待处理状态抛出稳定错误, 不重放写入.
 */
export async function readModelForOperation(bridge: BridgeClient, original: ProjectSnapshot, onRequest: (id: string) => void): Promise<ProjectSnapshot> {
    if (hasPendingModelUpdate(original)) throw new BridgeError("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存或核验")
    if (!isConnectedDesktopProject(original)) return original
    const request = bridge.request("project.inspect", {})
    onRequest(request.id)
    const fresh = await request.promise
    if (!sameModelTarget(original, fresh)) throw new BridgeError("STALE_CONFIGURATION_TARGET", "配置目标已失效, 请重新读取")
    if (hasPendingModelUpdate(fresh)) throw new BridgeError("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存或核验")
    if (isReadOnlyProject(fresh)) throw new BridgeError("READ_ONLY_MODEL", "当前远端模型只读")
    return fresh
}
