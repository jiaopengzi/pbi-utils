/**
 * FilePath    : pbi-utils\web\src\bridge\mockWorkspace.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 维护显式预览的模型快照、编辑与本地最近记录.
 */

import type { Methods, ProjectSnapshot } from "./client"
import type { MockRequest, MockSend } from "./mockRequest"
import { previewProject } from "./preview"

/** 维护显式预览的模型快照、编辑与本地最近记录. */
export class MockWorkspace {
    connected = true
    private recentProjects: Methods["project.recent"]["result"] = []
    currentProject = structuredClone(previewProject)
    private savedProject = structuredClone(previewProject)
    /** 绑定当前预览传输, 不访问真实模型. */
    constructor(private send: MockSend) {}
    /** 将已创建的模拟工程绑定为唯一 Desktop 工作区并记录原文件; project 仅来自模拟配置上下文, 返回克隆快照, 不访问磁盘或启动进程. */
    connectCreatedDesktop(project: ProjectSnapshot): ProjectSnapshot {
        this.connected = true
        const name = project.projectPath.split(/[\\/]/).pop() ?? project.model.name
        this.currentProject = {
            ...structuredClone(project),
            projectPath: `model://created-preview-${project.projectId}`,
            modelFormat: "Power BI Desktop",
            reportFormat: "在线模型不含报表布局",
            pages: [],
            connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "mock-desktop-instance-1000" },
        }
        this.savedProject = structuredClone(this.currentProject)
        this.recentProjects = [
            { id: `created-${project.projectId}`, path: project.projectPath, name, openedAt: new Date().toISOString() },
            ...this.recentProjects.filter((item) => item.path !== project.projectPath),
        ]
        return structuredClone(this.currentProject)
    }
    /** 处理本领域模拟请求; 返回是否已回复, 未匹配时交还分发器. */
    handle(request: MockRequest): boolean {
        const { id, method, params } = request
        const reply = (result: unknown) => this.send({ protocolVersion: 1, id, result })
        const fail = (code: string) => this.send({ protocolVersion: 1, id, error: { code, message: code } })
        if (method === "project.connectExternalTool") {
            fail("EXTERNAL_TOOL_TARGET_UNAVAILABLE")
            return true
        }
        if (method === "project.recent") {
            reply(structuredClone(this.recentProjects))
            return true
        }
        if (method === "project.deleteRecent") {
            const remaining = this.recentProjects.filter((item) => item.id !== params.id)
            const deleted = remaining.length !== this.recentProjects.length
            this.recentProjects = remaining
            reply({ deleted })
            return true
        }
        if (method === "project.disconnect") {
            this.connected = false
            reply({ disconnected: true })
            return true
        }
        if (method === "project.open" || method === "project.openRecent" || method === "project.connectLocal" || method === "project.connectRemote") {
            if (method === "project.openRecent" && !this.recentProjects.some((item) => item.id === params.id)) {
                fail("RECENT_PROJECT_NOT_FOUND")
                return true
            }
            const desktop = method !== "project.connectRemote"
            this.connected = true
            this.currentProject = {
                ...structuredClone(previewProject),
                projectPath: "model://preview",
                modelFormat: desktop ? "Power BI Desktop" : "SSAS / XMLA",
                reportFormat: "在线模型不含报表布局",
                pages: [],
                connection: {
                    kind: desktop ? "desktop" : "ssas-xmla",
                    label: desktop ? "Power BI Desktop" : "SSAS / XMLA",
                    instanceId: desktop ? "mock-desktop-instance-1000" : null,
                },
            }
            if (method === "project.open")
                this.recentProjects = [{ id: "preview-pbip", path: previewProject.projectPath, name: "示例工程 (模拟)", openedAt: new Date().toISOString() }]
            reply(structuredClone(this.currentProject))
            return true
        }
        if (method === "dax.format") {
            if (params.style !== "short" && params.style !== "long") {
                fail("VALIDATION_ERROR")
                return true
            }
            reply({ expressions: JSON.parse(params.value ?? "[]"), isSimulation: true })
            return true
        }
        if (method === "model.udfLint") {
            if (params.projectId !== this.currentProject.projectId || params.baselineId !== this.currentProject.baselineId) fail("STALE_BASELINE")
            else
                reply({
                    projectId: params.projectId,
                    baselineId: params.baselineId,
                    unused: [],
                    totalCount: this.currentProject.model.functions.length,
                    complete: false,
                    isSimulation: true,
                })
            return true
        }
        if (method === "model.exportLocation") {
            reply({ id: null, displayPath: null, available: false, warning: null, isSimulation: true })
            return true
        }
        if (method === "model.openExportDirectory" || method === "model.tableRowCounts") {
            fail("MODEL_EXPORT_SIMULATION_UNAVAILABLE")
            return true
        }
        if (method === "model.tableStatistics") {
            reply({
                projectId: this.currentProject.projectId,
                baselineId: this.currentProject.baselineId,
                tables: this.currentProject.model.tables.map((table) => ({
                    table: table.name,
                    rowCount: null,
                    sizeBytes: null,
                    isEstimate: false,
                    isQueryCount: false,
                })),
                isSimulation: true,
            })
            return true
        }
        if (method === "model.exportTables") {
            // 浏览器预览没有真实引擎或原生保存选择器, 明确拒绝而不制造成功文件名与行数.
            fail("MODEL_EXPORT_SIMULATION_UNAVAILABLE")
            return true
        }
        if (method === "model.edit" || method === "model.save" || method === "model.discard") {
            if (!this.connected) {
                fail("NO_PROJECT")
                return true
            }
            if (this.currentProject.connection && !["pbip", "desktop"].includes(this.currentProject.connection.kind)) {
                fail("READ_ONLY_MODEL")
                return true
            }
            if (method === "model.edit") {
                if (params.operation === "udf.deleteUnused") {
                    fail("UNSUPPORTED")
                    return true
                }
                if (params.operation === "table.deleteBatch") {
                    if (!params.value || params.value.length > 30000) {
                        fail("VALIDATION_ERROR")
                        return true
                    }
                    let batch: { projectId?: string; baselineId?: string; tables?: unknown }
                    try {
                        batch = JSON.parse(params.value ?? "{}") as typeof batch
                    } catch {
                        fail("VALIDATION_ERROR")
                        return true
                    }
                    const tables = batch.tables
                    if (
                        !Array.isArray(tables) ||
                        !tables.length ||
                        tables.length > 2000 ||
                        tables.some((table) => typeof table !== "string" || !table.trim())
                    ) {
                        fail("VALIDATION_ERROR")
                        return true
                    }
                    if (batch.projectId !== this.currentProject.projectId || batch.baselineId !== this.currentProject.baselineId) {
                        fail("STALE_BASELINE")
                        return true
                    }
                    const names = new Set((tables as string[]).map((table) => table.toLowerCase()))
                    if (
                        names.size !== tables.length ||
                        [...names].some((name) => !this.currentProject.model.tables.some((table) => table.name.toLowerCase() === name))
                    ) {
                        fail("OBJECT_NOT_FOUND")
                        return true
                    }
                    const next = structuredClone(this.currentProject)
                    next.model.tables = next.model.tables.filter((table) => !names.has(table.name.toLowerCase()))
                    next.model.measures = next.model.measures.filter((measure) => !names.has(measure.table.toLowerCase()))
                    // 展示夹具不含关系端点; 只有明确删除全部表时把模拟关系数清零, 不编造部分清理数量.
                    if (!next.model.tables.length) next.model.relationshipCount = 0
                    this.currentProject = next
                }
                if (params.operation === "measure.batch") {
                    const batch = JSON.parse(params.value ?? "{}") as { baselineId: string; items: import("./client").MeasureChange[] }
                    if (batch.baselineId !== this.currentProject.baselineId) {
                        fail("STALE_BASELINE")
                        return true
                    }
                    const next = structuredClone(this.currentProject)
                    for (const item of batch.items) {
                        const index = next.model.measures.findIndex((m) => m.table === item.table && m.name === item.name)
                        if (item.action === "create") {
                            if (next.model.measures.some((m) => m.name.toLowerCase() === item.name.toLowerCase())) {
                                fail("OBJECT_CONFLICT")
                                return true
                            }
                            next.model.measures.push({
                                table: item.table,
                                name: item.name,
                                expression: item.expression ?? "",
                                description: item.description ?? "",
                                formatString: item.formatString ?? "",
                                displayFolder: item.displayFolder ?? "",
                                isHidden: item.isHidden ?? false,
                            })
                        } else {
                            if (index < 0) {
                                fail("OBJECT_NOT_FOUND")
                                return true
                            }
                            if (item.action === "delete") next.model.measures.splice(index, 1)
                            else {
                                const { action: _action, ...patch } = item
                                Object.assign(next.model.measures[index]!, patch)
                            }
                        }
                    }
                    this.currentProject = next
                }
                const measure = this.currentProject.model.measures.find((m) => m.table === params.table && m.name === params.objectName)
                if (
                    params.operation === "measure.set" &&
                    measure &&
                    params.property &&
                    ["description", "formatString", "displayFolder", "expression"].includes(params.property)
                )
                    Object.assign(measure, { [params.property]: params.value ?? "" })
                this.currentProject.hasPendingChanges = false
                this.currentProject.baselineId = crypto.randomUUID()
                this.savedProject = structuredClone(this.currentProject)
            } else {
                if (method === "model.discard") this.currentProject = structuredClone(this.savedProject)
                this.currentProject.hasPendingChanges = false
                this.savedProject = structuredClone(this.currentProject)
            }
            this.currentProject.saveResult =
                method === "model.discard"
                    ? null
                    : {
                          modelSaved: true,
                          fileSaved: true,
                          desktopSynchronized: true,
                          message: "浏览器模拟保存结果，没有提交真实模型、保存文件或同步 Desktop。",
                      }
            reply(structuredClone(this.currentProject))
            return true
        }
        if (method === "project.inspect") {
            if (this.connected) reply(structuredClone(this.currentProject))
            else fail("NO_PROJECT")
            return true
        }
        return false
    }
}
