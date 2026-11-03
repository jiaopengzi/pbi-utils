/**
 * FilePath    : pbi-utils\web\src\bridge\mockConnections.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 维护显式预览的远端表单及按账号隔离的 Service 记录.
 */

import type { PowerBiServiceStatus, Methods, RecentRemoteConnection } from "./client"
import type { MockRequest, MockSend } from "./mockRequest"
import { MockWorkspace } from "./mockWorkspace"
import { previewProject } from "./preview"

/** 维护显式预览的远端表单及按账号隔离的 Service 记录. */
export class MockConnections {
    private remoteRecents: RecentRemoteConnection[] = []
    private remoteFormId = ""
    private serviceStatus: PowerBiServiceStatus = { account: null, recentLogins: [], isSimulation: true }
    private serviceRecentModels = new Map<string, Methods["powerbi.serviceRecentModels"]["result"]["items"]>()
    /** 绑定本次模拟会话的工作区与消息出口. */
    constructor(
        private workspace: MockWorkspace,
        private send: MockSend,
    ) {}
    /** 处理本领域模拟请求; 返回是否已回复, 未匹配时交还分发器. */
    handle(request: MockRequest): boolean {
        const { id, method, params } = request
        const reply = (result: unknown) => this.send({ protocolVersion: 1, id, result })
        const fail = (code: string) => this.send({ protocolVersion: 1, id, error: { code, message: code } })
        if (method === "remote.form" || method === "remote.mutate") {
            if (method === "remote.mutate") {
                if (!params.formId || params.formId !== this.remoteFormId || !this.remoteRecents.some((item) => item.id === params.recordId)) {
                    fail("REMOTE_FORM_EXPIRED")
                    return true
                }
                this.remoteRecents = this.remoteRecents
                    .filter((item) => params.action !== "delete" || item.id !== params.recordId)
                    .map((item) => (item.id === params.recordId ? Object.assign({}, item, { hasSavedPassword: false }) : item))
            }
            this.remoteFormId = crypto.randomUUID()
            reply({ formId: this.remoteFormId, recent: structuredClone(this.remoteRecents), isSimulation: true })
            return true
        }
        if (method === "project.connectRemote") {
            if (!params.formId || params.formId !== this.remoteFormId) {
                fail("REMOTE_FORM_EXPIRED")
                return true
            }
            this.remoteFormId = ""
            const values = params.values
            if (!values?.server.trim() || !values.database.trim()) {
                fail("VALIDATION_ERROR")
                return true
            }
            // 模拟只保存非秘密摘要, 输入密码不得进入预览存储或回显.
            if (values.remember)
                this.remoteRecents = [
                    {
                        id: crypto.randomUUID(),
                        server: values.server,
                        database: values.database,
                        authenticationMode: values.authenticationMode,
                        userName: values.userName,
                        hasSavedPassword: false,
                    },
                ]
        }
        if (method === "powerbi.serviceDeleteLogin") {
            this.serviceStatus.recentLogins = this.serviceStatus.recentLogins.filter((item) => item.id !== params.id)
            reply(structuredClone(this.serviceStatus))
            return true
        }
        if (method === "powerbi.serviceStatus" || method === "powerbi.serviceSignIn" || method === "powerbi.serviceSignOut") {
            if (method === "powerbi.serviceSignIn") {
                const saved = this.serviceStatus.recentLogins.find((item) => item.email.toLowerCase() === params.email?.trim().toLowerCase())
                const environmentName = params.environmentName || saved?.environmentName || ""
                if (!environmentName) {
                    this.send({ protocolVersion: 1, requestPhase: { requestId: id, phase: "discoveringEnvironment" } })
                    reply({
                        ...structuredClone(this.serviceStatus),
                        environments: [
                            { name: "GlobalCloud", displayName: "全球版" },
                            { name: "ChinaCloud", displayName: "世纪互联 (中国)" },
                        ],
                    })
                    return true
                }
                this.send({ protocolVersion: 1, requestPhase: { requestId: id, phase: "authenticating" } })
                this.serviceStatus.account = {
                    username: `${params.email?.trim() ?? "preview@example.com"} (模拟)`,
                    environmentName,
                    environmentLabel: environmentName === "ChinaCloud" ? "世纪互联 (中国)" : "全球版",
                }
                if (params.remember) {
                    const loginRecordId = `${params.email}/${environmentName}`
                    this.serviceStatus.recentLogins = [
                        {
                            id: loginRecordId,
                            email: params.email ?? "",
                            environmentName,
                            environmentLabel: this.serviceStatus.account.environmentLabel,
                            lastUsedAt: new Date().toISOString(),
                        },
                        ...this.serviceStatus.recentLogins.filter((item) => item.id !== loginRecordId),
                    ].slice(0, 20)
                }
                reply({ ...structuredClone(this.serviceStatus), environments: [] })
                return true
            }
            if (method === "powerbi.serviceSignOut") this.serviceStatus.account = null
            reply(structuredClone(this.serviceStatus))
            return true
        }
        if (method === "powerbi.serviceModels") {
            if (!this.serviceStatus.account) {
                fail("POWER_BI_SIGN_IN_REQUIRED")
                return true
            }
            reply([{ id: "preview-service-model", name: "销售模型 (模拟)", workspace: "示例工作区", unavailableReason: null }])
            return true
        }
        if (method === "powerbi.serviceRecentModels") {
            const account = this.serviceStatus.account
            if (!account) {
                fail("POWER_BI_SIGN_IN_REQUIRED")
                return true
            }
            reply({ items: structuredClone(this.serviceRecentModels.get(`${account.username}/${account.environmentName}`) ?? []), storageWarning: null })
            return true
        }
        if (method === "powerbi.serviceDeleteRecentModel") {
            const account = this.serviceStatus.account
            if (!account) {
                fail("POWER_BI_SIGN_IN_REQUIRED")
                return true
            }
            const key = `${account.username}/${account.environmentName}`
            const current = this.serviceRecentModels.get(key) ?? []
            const remaining = current.filter((item) => item.id !== params.id)
            this.serviceRecentModels.set(key, remaining)
            reply({ deleted: remaining.length !== current.length })
            return true
        }
        if (method === "project.connectService") {
            if (!this.serviceStatus.account || params.modelId !== "preview-service-model") {
                fail("INVALID_SERVICE_MODEL")
                return true
            }
            this.workspace.connected = true
            this.workspace.currentProject = {
                ...structuredClone(previewProject),
                projectPath: "model://preview-service",
                modelFormat: "Power BI Service",
                reportFormat: "在线模型不含报表布局",
                pages: [],
                model: { ...structuredClone(previewProject.model), name: "销售模型 (模拟)" },
                connection: { kind: "powerbi-service", label: "Power BI Service", modelId: params.modelId },
            }
            const account = this.serviceStatus.account
            const key = `${account.username}/${account.environmentName}`
            this.serviceRecentModels.set(key, [
                {
                    id: "preview-service-recent",
                    modelId: params.modelId,
                    name: "销售模型 (模拟)",
                    workspace: "示例工作区",
                    lastUsedAt: new Date().toISOString(),
                },
            ])
            reply(structuredClone(this.workspace.currentProject))
            return true
        }
        return false
    }
}
