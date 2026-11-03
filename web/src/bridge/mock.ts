/**
 * FilePath    : pbi-utils\web\src\bridge\mock.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供仅限显式浏览器预览的模拟桥接传输与取消行为.
 */

import type { Transport, HostDialogRequest, Methods } from "./client"
import type { MockRequest } from "./mockRequest"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import { ConfigurationMock } from "./configurationMock"
import { MockWorkspace } from "./mockWorkspace"
import { MockConnections } from "./mockConnections"
import { MockSettings } from "./mockSettings"
import { MockUpdates } from "./mockUpdates"

/** 仅用于浏览器显式预览, 不代表 C# 或 WebView2 验收. */
export class MockTransport implements Transport {
    private scaffold = new ScaffoldMock()
    private configuration = new ConfigurationMock()
    private workspace = new MockWorkspace((data) => this.send(data))
    private connections = new MockConnections(this.workspace, (data) => this.send(data))
    private settings = new MockSettings((data) => this.send(data))
    private updates = new MockUpdates((data) => this.send(data))
    private dialog?: { request: { id: string; method: string; params: Record<string, unknown> }; view: HostDialogRequest }
    private authorizedDialogs = new Set<string>()
    /** 仅浏览器产品预览启用交互弹窗; 服务单元夹具可保持直接返回模拟业务结果. */
    constructor(
        private showDialogs = false,
        connected = true,
    ) {
        this.workspace.connected = connected
    }
    private listeners = new Set<(event: MessageEvent) => void>()
    /** 注册预览消息监听器. */
    addEventListener(_type: "message", listener: (event: MessageEvent) => void) {
        this.listeners.add(listener)
    }
    /** 移除预览消息监听器. */
    removeEventListener(_type: "message", listener: (event: MessageEvent) => void) {
        this.listeners.delete(listener)
    }
    /** 发送结构化预览消息. */
    private send(data: unknown) {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data }))
    }
    /** 显式模拟宿主发起的一次性确认, 不保存表单秘密或读取任何用户资料. */
    private prompt(
        request: { id: string; method: string; params: Record<string, unknown> },
        kind: HostDialogRequest["kind"],
        key: string,
        data: Record<string, unknown> = {},
        actions = ["confirm"],
    ) {
        const view: HostDialogRequest = {
            id: crypto.randomUUID(),
            kind,
            titleKey: `hostDialog.${key}Title`,
            messageKey: `hostDialog.${key}Message`,
            messageArgs: { directory: this.settings.sampleState.outputPath },
            data: { ...data, isSimulation: true },
            actions,
        }
        this.dialog = { request, view }
        this.send({ protocolVersion: 1, uiDialog: view })
    }
    /** 模拟弹窗回复及取消排空; 只验证界面流程, 不连接服务器或保存输入的秘密. */
    private handleDialog(value: unknown): boolean {
        const request = value as { id: string; method: string; params: Record<string, unknown> }
        const { id, method, params } = request
        if (method === "ui.dialogRespond") {
            const pending = this.dialog
            const action = String(params.action)
            const accepted = !!pending && pending.view.id === params.dialogId && (action === "cancel" || pending.view.actions?.includes(action) === true)
            this.send({ protocolVersion: 1, id, result: { accepted } })
            if (!accepted || !pending) return true
            this.dialog = undefined
            this.send({ protocolVersion: 1, uiDialogClosed: { id: pending.view.id } })
            if (pending.request.method === "app.resetUserData") {
                if (action === "cancel") this.send({ protocolVersion: 1, id: pending.request.id, result: { scheduled: false, isSimulation: true } })
                else if (pending.view.titleKey === "hostDialog.resetDataTitle") this.prompt(pending.request, "confirm", "resetDataFinal")
                else this.send({ protocolVersion: 1, id: pending.request.id, result: { scheduled: true, isSimulation: true } })
            } else if (action === "cancel") {
                this.send({ protocolVersion: 1, id: pending.request.id, error: { code: "CANCELLED", message: "已取消预览" } })
            } else {
                this.authorizedDialogs.add(pending.request.id)
                // 恢复当前模拟传输的业务分发, 不是浏览器 window.postMessage.
                // oxlint-disable-next-line unicorn/require-post-message-target-origin
                this.postMessage(pending.request)
            }
            return true
        }
        if (method === "request.cancel" && this.dialog && this.dialog.request.id === params.requestId) {
            const pending = this.dialog
            this.dialog = undefined
            this.send({ protocolVersion: 1, id, result: { accepted: true } })
            this.send({ protocolVersion: 1, uiDialogClosed: { id: pending.view.id } })
            this.send({ protocolVersion: 1, id: pending.request.id, error: { code: "CANCELLED", message: "已取消预览" } })
            return true
        }
        // 重置必须完整演示两次确认, 即使测试传输未开启其他弹窗; 最终确认也不访问真实目录或模拟删除成功.
        if (method === "app.resetUserData") {
            if (Object.keys(params).length) this.send({ protocolVersion: 1, id, error: { code: "VALIDATION_ERROR", message: "VALIDATION_ERROR" } })
            else if (this.dialog) this.send({ protocolVersion: 1, id, error: { code: "BUSY", message: "BUSY" } })
            else this.prompt(request, "confirm", "resetData")
            return true
        }
        if (!this.showDialogs || this.authorizedDialogs.delete(id)) return false
        if (method === "sample.clear") this.prompt(request, "confirm", "sampleClear")
        else if (method === "sample.generate" && this.settings.sampleState.hasData && params.mode === "full") this.prompt(request, "confirm", "sampleOverwrite")
        else return false
        return true
    }
    /** 模拟预览请求与取消, 绝不在生产自动启用. */
    postMessage(value: unknown) {
        const scaffoldRequest = value as { id: string; method: string; params: Record<string, unknown> }
        if (this.updates.handle(scaffoldRequest)) return
        if (["learning.update", "learning.close", "learning.open"].includes(scaffoldRequest.method)) {
            const result =
                scaffoldRequest.method === "learning.update"
                    ? { embedded: false, isSimulation: true }
                    : scaffoldRequest.method === "learning.close"
                      ? { closed: true, isSimulation: true }
                      : { opened: false, isSimulation: true }
            this.send({ protocolVersion: 1, id: scaffoldRequest.id, result })
            return
        }
        try {
            if (scaffoldRequest.method.startsWith("configuration.")) {
                const result =
                    scaffoldRequest.method === "configuration.context"
                        ? this.configuration.context(scaffoldRequest.params.target as "created" | "current", this.workspace.currentProject)
                        : scaffoldRequest.method === "configuration.apply"
                          ? this.configuration.apply(
                                scaffoldRequest.params as unknown as Methods["configuration.apply"]["params"],
                                this.workspace.currentProject,
                            )
                          : this.configuration.recover(
                                scaffoldRequest.params as unknown as Methods["configuration.complete"]["params"],
                                this.workspace.currentProject,
                            )
                this.send({ protocolVersion: 1, id: scaffoldRequest.id, result })
                return
            }
            let result = this.scaffold.handle(scaffoldRequest.method, scaffoldRequest.params)
            if (result !== undefined) {
                if (scaffoldRequest.method === "scaffold.create") this.configuration.bind(result as import("@/features/scaffold/contracts").ScaffoldResult)
                if (scaffoldRequest.method === "scaffold.openDesktop") {
                    this.send({ protocolVersion: 1, requestPhase: { requestId: scaffoldRequest.id, phase: "connecting" } })
                    const project = this.configuration.context("created", this.workspace.currentProject).project
                    result = { opened: true, project: this.workspace.connectCreatedDesktop(project), isSimulation: true }
                }
                this.send({ protocolVersion: 1, id: scaffoldRequest.id, result })
                return
            }
        } catch (cause) {
            this.send({
                protocolVersion: 1,
                id: scaffoldRequest.id,
                error: { code: cause instanceof Error ? cause.message : "MOCK_ERROR", message: cause instanceof Error ? cause.message : "Mock error" },
            })
            return
        }
        if (this.handleDialog(value)) return
        const request = value as MockRequest
        if (this.connections.handle(request) || this.settings.handle(request) || this.workspace.handle(request)) return
        this.send({ protocolVersion: 1, id: request.id, error: { code: "UNKNOWN_METHOD", message: "UNKNOWN_METHOD" } })
    }
}
