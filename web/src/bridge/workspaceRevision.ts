/**
 * FilePath    : pbi-utils\web\src\bridge\workspaceRevision.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 分类绑定当前工作区的桥接请求, 为外部切换后的草稿保护提供客户端防线.
 */
import type { Methods } from "./methods"

/** 当前连接的读取、编辑和切换必须绑定已采用代际; created 配置由独立 targetId 继续在宿主核验. */
export function usesWorkspace(method: keyof Methods, params: unknown): boolean {
    return (
        method.startsWith("model.") ||
        [
            "project.inspect",
            "project.open",
            "project.openRecent",
            "project.connectLocal",
            "project.connectExternalTool",
            "project.connectService",
            "project.connectRemote",
            "project.disconnect",
            "powerbi.serviceSignOut",
            "scaffold.openDesktop",
        ].includes(method) ||
        (method === "configuration.context" && (params as { target?: string })?.target === "current") ||
        ["configuration.apply", "configuration.complete", "configuration.discard"].includes(method)
    )
}
