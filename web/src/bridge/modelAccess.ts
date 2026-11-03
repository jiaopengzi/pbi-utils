/**
 * FilePath    : pbi-utils\web\src\bridge\modelAccess.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前模型来源的统一显示能力判断, 后端仍独立验证写入权限.
 */
import type { ProjectSnapshot } from "./client"

/** 本地 PBIP 和 Desktop 可编辑; 旧在线快照默认只读, 不推断远端写权限. */
export function isReadOnlyProject(project: ProjectSnapshot): boolean {
    return project.connection ? !["pbip", "desktop"].includes(project.connection.kind) : project.projectPath.startsWith("model://")
}

/** 仅宿主已绑定的本机 Desktop 在线模型采用直接提交语义, 不把旧在线或离线文件推断为 Desktop. */
export function isConnectedDesktopProject(project?: ProjectSnapshot): boolean {
    return project?.connection?.kind === "desktop" && project.projectPath.startsWith("model://")
}

/** 未知提交或 Desktop 提交后尚无完整引擎回读时保持待核验; 无错误码不等于已取得完成证据. */
export function requiresModelVerification(project?: ProjectSnapshot): boolean {
    return (
        project?.saveResult?.errorCode?.includes("NEEDS_VERIFICATION") === true ||
        (isConnectedDesktopProject(project) && project?.saveResult?.modelSaved === true && !project.saveResult.desktopSynchronized)
    )
}

/** 任一未完成草稿、权威保存错误或缺失的提交回读证据均阻止继续写入, 不把 Desktop 手动文件保存当作挂起. */
export function hasPendingModelUpdate(project?: ProjectSnapshot): boolean {
    return project?.hasPendingChanges === true || !!project?.saveResult?.errorCode || requiresModelVerification(project)
}

/** 按目标判断本轮权威修改完成; Desktop 仅要求模型提交, 文件保存仍保留真实布尔值, 待核验和未完成草稿一律拒绝. */
export function isModelUpdateComplete(project: ProjectSnapshot): boolean {
    const result = project.saveResult
    return (
        !hasPendingModelUpdate(project) &&
        (!result || (result.modelSaved && result.desktopSynchronized && (isConnectedDesktopProject(project) || result.fileSaved)))
    )
}
