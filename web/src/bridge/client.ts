/**
 * FilePath    : pbi-utils\web\src\bridge\client.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 管理请求关联、事件、超时与监听器, 并保留领域契约的集中类型导出.
 */

import type { Methods } from "./methods"
import type { OperationEvent, BridgePhase, HostDialogRequest, ModelTableExportProgress, Transport, WorkspaceChanged } from "./protocol"
import { calendarEnvelopeAllowed } from "./calendarBudget"
import { parseModelExportProgress } from "./modelExportProgress"
import { usesWorkspace } from "./workspaceRevision"
import { requestEnvelope } from "./requestEnvelope"

export type * from "./modelContracts"
export type * from "./loggingContracts"
export type * from "./mcpContracts"
export type * from "./updateContracts"
export type * from "./protocol"
export type { Methods } from "./methods"

interface Pending {
    connectionRevision: number
    workspaceBound: boolean
    resolve: (value: unknown) => void
    reject: (reason: Error) => void
    timer: ReturnType<typeof setTimeout>
    event?: (item: OperationEvent) => void
    phase?: (phase: BridgePhase) => void
    currentPhase?: BridgePhase
    progress?: (progress: ModelTableExportProgress) => void
    lastProgress?: ModelTableExportProgress
    exportTotal: number
    sequence: number
    method: keyof Methods
    connecting: boolean
    generating: boolean
    exporting: boolean
    expiring: boolean
    committing: boolean
    expire: () => void
}
/** 类型化桥接错误. */
export class BridgeError extends Error {
    /** 保存稳定错误码与用户可读消息. */
    constructor(
        public code: string,
        message: string,
    ) {
        super(message)
    }
}
/** 管理请求关联、超时和事件的会话客户端. */
export class BridgeClient {
    private pending = new Map<string, Pending>()
    private closed = false
    private dialogListeners = new Set<(dialog: HostDialogRequest) => void>()
    private dialogClosedListeners = new Set<(id: string) => void>()
    private currentDialog?: HostDialogRequest
    private retiredDialogIds = new Set<string>()
    private connectionRevision = 0
    private latestConnectionChange?: Pick<WorkspaceChanged, "revision" | "requestId">
    /** 订阅单一消息监听器; transport 必须来自真实宿主或显式 mock. */
    constructor(
        private transport: Transport,
        private timeoutMs = 30000,
    ) {
        transport.addEventListener("message", this.receive)
    }
    /** 根据 ID 关联结果, 丢弃迟到响应和重复事件. */
    private receive = (event: MessageEvent) => {
        if (this.closed) return
        const message = event.data
        if (!message || message.protocolVersion !== 1) return
        if (message.workspaceChanged) {
            const change = message.workspaceChanged as WorkspaceChanged
            if (
                !Number.isSafeInteger(change.revision) ||
                change.revision <= Math.max(this.connectionRevision, this.latestConnectionChange?.revision ?? 0) ||
                typeof change.requestId !== "string" ||
                (change.project !== null && (!change.project || typeof change.project.projectId !== "string" || typeof change.project.projectPath !== "string"))
            )
                return
            const entry = this.pending.get(change.requestId)
            const ownRequest =
                entry?.workspaceBound &&
                entry.connectionRevision === this.connectionRevision &&
                (!this.latestConnectionChange || this.latestConnectionChange.revision === this.connectionRevision)
            this.latestConnectionChange = { revision: change.revision, requestId: change.requestId }
            if (ownRequest) this.connectionRevision = change.revision
            return
        }
        if (message.uiDialog) {
            const dialog = message.uiDialog as HostDialogRequest
            if (
                typeof dialog.id !== "string" ||
                !dialog.id ||
                dialog.id.length > 64 ||
                typeof dialog.titleKey !== "string" ||
                !["confirm", "tool", "notice"].includes(dialog.kind) ||
                (dialog.actions !== undefined &&
                    dialog.actions !== null &&
                    (!Array.isArray(dialog.actions) || dialog.actions.some((action) => typeof action !== "string")))
            )
                return
            if (this.retiredDialogIds.has(dialog.id) || this.currentDialog?.id === dialog.id) return
            if (this.currentDialog) this.retireDialog(this.currentDialog.id)
            this.currentDialog = dialog
            for (const listener of this.dialogListeners) listener(dialog)
            return
        }
        if (message.uiDialogClosed) {
            const id = message.uiDialogClosed.id
            if (typeof id !== "string" || !id || id.length > 64) return
            this.retireDialog(id)
            if (this.currentDialog?.id === id) this.currentDialog = undefined
            for (const listener of this.dialogClosedListeners) listener(id)
            return
        }
        // 进度只服务同一在途导出, 不因通知重置超时, 原请求取消或终结后不再更新.
        if (message.requestProgress) {
            const entry = this.pending.get(message.requestProgress.requestId)
            if (entry?.method === "model.exportTables" && !entry.expiring && !entry.committing && entry.progress) {
                const progress = parseModelExportProgress(message.requestProgress, entry.exportTotal, entry.lastProgress)
                if (progress) {
                    entry.lastProgress = progress
                    entry.progress(progress)
                }
            }
            return
        }
        // 只有当前连接请求的首次固定阶段通知可以重置时限, 重复通知不能无限延长请求.
        if (message.requestPhase) {
            const phase = message.requestPhase
            const entry = this.pending.get(phase.requestId)
            // 导出只在首次数据读取阶段获得一次有界预算; 到期请求取消并等待宿主原请求终态.
            if (entry?.method === "model.exportTables" && !entry.exporting && !entry.expiring && !entry.committing && phase.phase === "exporting") {
                entry.exporting = true
                entry.currentPhase = "exporting"
                clearTimeout(entry.timer)
                entry.timer = setTimeout(entry.expire, this.timeoutMs === 30000 ? 600000 : this.timeoutMs)
                entry.phase?.("exporting")
            }
            if (
                entry?.method === "powerbi.serviceSignIn" &&
                ((phase.phase === "discoveringEnvironment" && !entry.currentPhase) ||
                    (phase.phase === "authenticating" && entry.currentPhase !== "authenticating"))
            ) {
                entry.currentPhase = phase.phase
                entry.phase?.(phase.phase)
            }
            if (
                entry &&
                ["sample.generate", "scaffold.preview", "scaffold.create"].includes(entry.method) &&
                !entry.generating &&
                !entry.committing &&
                phase.phase === "generating"
            ) {
                entry.generating = true
                entry.currentPhase = "generating"
                clearTimeout(entry.timer)
                entry.phase?.("generating")
            }
            if (
                entry &&
                [
                    "model.edit",
                    "model.save",
                    "model.exportTables",
                    "model.openExportDirectory",
                    "configuration.apply",
                    "configuration.complete",
                    "configuration.discard",
                    "sample.generate",
                    "sample.clear",
                    "scaffold.create",
                    "scaffold.openDirectory",
                    "udf.localSave",
                    "udf.localDelete",
                    "udf.sourceSave",
                    "udf.sourceDelete",
                    "udf.catalogRefresh",
                ].includes(entry.method) &&
                !entry.committing &&
                phase.phase === "committing"
            ) {
                entry.committing = true
                entry.currentPhase = "committing"
                clearTimeout(entry.timer)
                entry.phase?.("committing")
            }
            if (
                entry &&
                ["project.connectRemote", "project.open", "project.openRecent", "scaffold.openDesktop"].includes(entry.method) &&
                !entry.connecting &&
                phase.phase === "connecting"
            ) {
                entry.connecting = true
                clearTimeout(entry.timer)
                entry.timer = setTimeout(entry.expire, entry.method !== "project.connectRemote" && this.timeoutMs === 30000 ? 185000 : this.timeoutMs)
                entry.phase?.("connecting")
            }
            return
        }
        if (message.event) {
            const item = message.event as OperationEvent
            const entry = this.pending.get(item.requestId)
            if (entry && item.sequence === entry.sequence + 1 && item.sequence <= 8192) {
                entry.sequence = item.sequence
                entry.event?.(item)
            }
            return
        }
        const entry = this.pending.get(message.id)
        if (!entry) return
        clearTimeout(entry.timer)
        this.pending.delete(message.id)
        if (entry.method === "calendar.preview" && new TextEncoder().encode(JSON.stringify(message)).length > 4 * 1024 * 1024) {
            entry.reject(new BridgeError("MESSAGE_TOO_LARGE", "消息超出该方法的内容大小限制"))
            return
        }
        if (
            entry.workspaceBound &&
            entry.connectionRevision !== (this.latestConnectionChange?.revision ?? this.connectionRevision) &&
            this.latestConnectionChange?.requestId !== message.id
        ) {
            entry.reject(new BridgeError("STALE_CONNECTION", "连接已改变, 请确认当前连接后重试"))
            return
        }
        // 仅当前目标成功回包可建立首次连接代际; 未关联通知、失败和无关结果不能改绑目标.
        if (
            !message.error &&
            entry.workspaceBound &&
            !this.latestConnectionChange &&
            Number.isSafeInteger(message.connectionRevision) &&
            message.connectionRevision > this.connectionRevision
        )
            this.connectionRevision = message.connectionRevision
        if (message.error) entry.reject(new BridgeError(message.error.code, message.error.message))
        else entry.resolve(message.result)
    }
    /** 保留最近 256 个已关闭或被替换身份, 阻止 UI 队列交错后的迟到打开; 不保存表单内容. */
    private retireDialog(id: string): void {
        this.retiredDialogIds.add(id)
        if (this.retiredDialogIds.size > 256) {
            const oldest = this.retiredDialogIds.values().next().value
            if (oldest !== undefined) this.retiredDialogIds.delete(oldest)
        }
    }
    /** 订阅独立于业务请求的宿主弹窗, 挂载时重放尚未关闭的当前弹窗; 返回解除订阅函数. */
    onDialog(listener: (dialog: HostDialogRequest) => void): () => void {
        this.dialogListeners.add(listener)
        if (this.currentDialog) listener(this.currentDialog)
        return () => this.dialogListeners.delete(listener)
    }
    /** 订阅权威关闭通知; 消费方必须按完整弹窗 ID 丢弃迟到通知. */
    onDialogClosed(listener: (id: string) => void): () => void {
        this.dialogClosedListeners.add(listener)
        return () => this.dialogClosedListeners.delete(listener)
    }
    /** 创建白名单请求并返回ID/结果Promise; 回调接收事件、阶段与可选真实导出计数, 进度不改变原超时/取消排空. */
    request<M extends keyof Methods>(
        method: M,
        params: Methods[M]["params"],
        onEvent?: (item: OperationEvent) => void,
        onPhase?: (phase: BridgePhase) => void,
        onProgress?: (progress: ModelTableExportProgress) => void,
    ): { id: string; promise: Promise<Methods[M]["result"]> } {
        const id = crypto.randomUUID()
        const promise = new Promise<Methods[M]["result"]>((resolve, reject) => {
            if (this.closed) {
                reject(new BridgeError("CLOSED", "桥接已关闭"))
                return
            }
            const workspaceBound = usesWorkspace(method, params)
            if (workspaceBound && this.latestConnectionChange && this.latestConnectionChange.revision !== this.connectionRevision) {
                reject(new BridgeError("STALE_CONNECTION", "连接已改变, 请确认当前连接后重试"))
                return
            }
            const envelope = requestEnvelope(method, params, id, this.connectionRevision)
            try {
                // 按完整 JSON 信封计量, 引号/反斜杠转义和 UTF-8 多字节都消耗宿主64KiB预算.
                // 超限在登记 pending/启动计时器前终结, 不等待宿主无法关联 ID 的拒绝包.
                if (!calendarEnvelopeAllowed(method, params, new TextEncoder().encode(JSON.stringify(envelope)).length)) {
                    reject(new BridgeError("MESSAGE_TOO_LARGE", "请求内容超过 64 KiB 限制, 请缩短内容后重试"))
                    return
                }
            } catch {
                reject(new BridgeError("TRANSPORT_FAILED", "无法发送到宿主"))
                return
            }
            const expire = () => {
                if (["model.exportTables", "model.tableStatistics", "model.tableRowCounts", "model.openExportDirectory"].includes(method)) {
                    const entry = this.pending.get(id)
                    if (!entry || entry.expiring || entry.committing) return
                    entry.expiring = true
                    entry.phase?.("canceling")
                    // 不提前拒绝原 Promise: 文件选择/导出/统计查询取消须由宿主回执终结, 防止界面解锁后与未排空操作交错.
                    this.request("request.cancel", { requestId: id }).promise.catch(() => undefined)
                    return
                }
                this.pending.delete(id)
                reject(new BridgeError("TIMEOUT", "请求超时"))
                if (method !== "request.cancel") this.request("request.cancel", { requestId: id }).promise.catch(() => undefined)
            }
            const timer = setTimeout(
                expire,
                method === "app.resetUserData" && this.timeoutMs === 30000
                    ? 390000
                    : method === "udf.catalogRefresh" && this.timeoutMs === 30000
                      ? 600000
                      : ["project.open", "project.openRecent", "scaffold.openDesktop"].includes(method) && this.timeoutMs === 30000
                        ? 185000
                        : [
                                "powerbi.serviceSignIn",
                                "model.edit",
                                "model.save",
                                "model.exportTables",
                                "model.tableRowCounts",
                                "model.openExportDirectory",
                                "configuration.apply",
                                "configuration.complete",
                                "configuration.discard",
                                "sample.chooseDirectory",
                                "sample.generate",
                                "sample.clear",
                                "scaffold.chooseOutput",
                                "scaffold.preview",
                                "scaffold.create",
                            ].includes(method) && this.timeoutMs === 30000
                          ? 180000
                          : this.timeoutMs,
            )
            const tables = method === "model.exportTables" ? (params as Methods["model.exportTables"]["params"])?.tables : undefined
            this.pending.set(id, {
                connectionRevision: this.connectionRevision,
                workspaceBound,
                resolve: resolve as (value: unknown) => void,
                reject,
                timer,
                event: onEvent,
                phase: onPhase,
                progress: onProgress,
                exportTotal: Array.isArray(tables) ? tables.length : 0,
                sequence: 0,
                method,
                connecting: false,
                generating: false,
                exporting: false,
                expiring: false,
                committing: false,
                expire,
            })
            try {
                // WebView2 的单参数桥接 API, 不是 window.postMessage; 来源由 C# 宿主校验.
                // oxlint-disable-next-line unicorn/require-post-message-target-origin
                this.transport.postMessage(envelope)
            } catch {
                clearTimeout(this.pending.get(id)?.timer ?? timer)
                this.pending.delete(id)
                reject(new BridgeError("TRANSPORT_FAILED", "无法发送到宿主"))
            }
        })
        return { id, promise }
    }
    /** 释放监听器、超时器和所有等待者; 组件销毁时调用. */
    dispose(): void {
        this.closed = true
        this.transport.removeEventListener("message", this.receive)
        for (const entry of this.pending.values()) {
            clearTimeout(entry.timer)
            entry.reject(new BridgeError("CLOSED", "桥接已关闭"))
        }
        this.pending.clear()
        if (this.currentDialog) for (const listener of this.dialogClosedListeners) listener(this.currentDialog.id)
        this.currentDialog = undefined
        this.retiredDialogIds.clear()
        this.dialogListeners.clear()
        this.dialogClosedListeners.clear()
        this.latestConnectionChange = undefined
    }
}

/** 仅返回真实 WebView2 传输; 生产缺少宿主时明确报错. */
export function hostTransport(): Transport {
    const host = (window as Window & { chrome?: { webview?: Transport } }).chrome?.webview
    if (!host) throw new BridgeError("HOST_UNAVAILABLE", "未检测到桌面宿主. 请从 pbi-utils 桌面启动, 或显式运行 devWeb 预览.")
    return host
}
