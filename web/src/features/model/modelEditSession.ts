/**
 * FilePath    : pbi-utils\web\src\features\model\modelEditSession.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 描述当前应用实例拥有的单模型字段编辑会话, 不保存请求或授权状态.
 */
import type { ProjectSnapshot } from "@/bridge/client"

/** 模型面板向外部属性编辑器声明当前显示意图和旁观锁, 不转交请求或模型写权限. */
export interface ModelEditorState {
    targetIdentity: string
    active: boolean
    locked: boolean
}

/** 原生文本选区只属于同一目标/字段和精确文本, 不保存焦点或浏览器撤销栈. */
export interface ModelEditSelection {
    fieldIdentity: string
    value: string
    start: number
    end: number
    direction: "forward" | "backward" | "none"
}

/** 仅持久到当前应用工作区内存的编辑事实; sourceValue用于重新挂载时核对外部字段变化. */
export interface ModelEditSession {
    targetIdentity: string
    operation: string
    table: string
    objectName: string
    property: string
    value: string
    sourceValue: string
    existingTarget: boolean
    conflict: boolean
    selection?: ModelEditSelection
}

/** 精确区分宿主绑定目标, 不把普通基线、统计或显示名称变化当作新模型. */
export function modelEditTargetIdentity(project: ProjectSnapshot): string {
    return JSON.stringify([project.projectId, project.projectPath, project.connection?.kind, project.connection?.instanceId, project.connection?.modelId])
}
