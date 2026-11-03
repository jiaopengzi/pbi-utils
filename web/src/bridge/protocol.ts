/**
 * FilePath    : pbi-utils\web\src\bridge\protocol.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 定义桥接事件、传输、弹窗与请求阶段协议.
 */
import type { ProjectSnapshot } from "./modelContracts"

/** 有序的宿主执行事件, 由宿主业务操作共用. */
export interface OperationEvent {
    requestId: string
    sequence: number
    type: string
    timestamp: string
    payload: unknown
}
/** 宿主结构化消息传输接口. */
export interface Transport {
    postMessage(message: unknown): void
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void
}
/** 宿主一次性对话框的可显示资料; 不携带任意执行指令或凭据. */
export interface HostDialogRequest {
    id: string
    kind: "confirm" | "tool" | "notice"
    titleKey: string
    messageKey?: string | null
    messageArgs?: Record<string, string | number> | null
    data?: Record<string, unknown> | null
    actions?: string[] | null
}
/** 有界在途请求回调. */
export type BridgePhase =
    | "connecting"
    | "confirming"
    | "preparing"
    | "generating"
    | "exporting"
    | "canceling"
    | "committing"
    | "discoveringEnvironment"
    | "authenticating"

/** 单次导出的真实安全计数, 当前表行数不代表源表总量或已发布成功. */
export interface ModelTableExportProgress {
    requestId: string
    kind: "tableExport"
    stage: "reading" | "verifying" | "finalizing" | "publishing"
    completedTables: number
    totalTables: number
    tableIndex: number
    rowsRead: number
}
/** 宿主发布的连接/模型代际; requestId 只用于区分本页面发起的操作, project 不含秘密. */
export interface WorkspaceChanged {
    revision: number
    project: ProjectSnapshot | null
    requestId: string
}
