/**
 * FilePath    : pbi-utils\web\src\app\reconcileProjectSnapshot.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 同一模型刷新时复用未变化的快照分支, 保留完整权威结果.
 */

import type { ProjectSnapshot } from "@/bridge/client"

/** 按完整宿主身份核对刷新对象, 不用模型名称或数组条目推测身份. */
function sameProject(current: ProjectSnapshot, incoming: ProjectSnapshot): boolean {
    return (
        current.projectId === incoming.projectId &&
        current.projectPath === incoming.projectPath &&
        current.connection?.kind === incoming.connection?.kind &&
        current.connection?.instanceId === incoming.connection?.instanceId &&
        current.connection?.modelId === incoming.connection?.modelId
    )
}

/** 对 JSON 对象及数组递归复用相等分支, 变化和缺失字段始终以新值为准. */
function shareUnchanged(current: unknown, incoming: unknown): unknown {
    if (Object.is(current, incoming)) return current
    if (Array.isArray(incoming)) {
        if (!Array.isArray(current)) return incoming
        const shared = incoming.map((value, index) => shareUnchanged(current[index], value))
        return current.length === shared.length && shared.every((value, index) => Object.is(value, current[index])) ? current : shared
    }
    if (incoming === null || current === null || typeof incoming !== "object" || typeof current !== "object" || Array.isArray(current)) return incoming

    const oldObject = current as Record<string, unknown>
    const newObject = incoming as Record<string, unknown>
    const keys = Object.keys(newObject)
    const shared = { ...newObject }
    let unchanged = Object.keys(oldObject).length === keys.length
    for (const key of keys) {
        shared[key] = shareUnchanged(oldObject[key], newObject[key])
        if (!Object.hasOwn(oldObject, key) || !Object.is(shared[key], oldObject[key])) unchanged = false
    }
    return unchanged ? current : shared
}

/** 合并只读刷新结果, 身份变化时直接采用新快照, 同一模型仅复用相等引用. */
export function reconcileProjectSnapshot(current: ProjectSnapshot | undefined, incoming: ProjectSnapshot): ProjectSnapshot {
    if (!current || !sameProject(current, incoming)) return incoming
    return shareUnchanged(current, incoming) as ProjectSnapshot
}
