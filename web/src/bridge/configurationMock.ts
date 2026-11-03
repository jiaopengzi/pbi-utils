/**
 * FilePath    : pbi-utils\web\src\bridge\configurationMock.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟配置上下文, 隔离新建与当前目标, 不声明真实文件或引擎写入.
 */
import type { ConfigurationContext, Methods, ProjectSnapshot } from "./client"
import type { ScaffoldResult, TemplateMeasureDefinition } from "@/features/scaffold/contracts"
import { previewProject } from "./preview"

/** 仅在显式 MockTransport 内使用, 所有结果都标记模拟. */
export class ConfigurationMock {
    private created?: ProjectSnapshot
    private createdId = ""
    private currentId = ""
    private currentIdentity = ""
    private measureFiles = new Map<string, { measures: TemplateMeasureDefinition[]; revision: string }>()

    /** 用模拟创建回执建立独立目标, 不替换传输的当前模型. */
    bind(result: ScaffoldResult): void {
        if (!result.projectCreated || !result.verified) return
        this.created = structuredClone(previewProject)
        this.created.projectPath = result.projectPath
        this.created.projectId = crypto.randomUUID()
        this.created.baselineId = result.baselineId ?? crypto.randomUUID()
        this.created.connection = { kind: "pbip", label: "PBIP" }
        this.createdId = crypto.randomUUID()
    }

    /** 返回 target 对应的模拟配置上下文; current 为模拟当前模型, 无新建目标时抛错, 不读取或创建磁盘目录. */
    context(target: "created" | "current", current: ProjectSnapshot): ConfigurationContext {
        const project = target === "created" ? this.created : current
        if (!project) throw new Error("CREATED_PROJECT_NOT_AVAILABLE")
        if (target === "current" && this.currentIdentity !== `${project.projectId}|${project.projectPath}`) {
            this.currentIdentity = `${project.projectId}|${project.projectPath}`
            this.currentId = crypto.randomUUID()
        }
        const tables = project.model.tables.map((table) => ({
            name: table.name,
            columns: table.columns.map((column) => ({ name: column.name, dataType: column.dataType })),
            measures: project.model.measures.filter((measure) => measure.table === table.name).map((measure) => measure.name),
            isDate: table.columns.some((column) => column.dataType === "DateTime"),
        }))
        const date = tables.find((table) => table.isDate)
        const canWrite = !project.connection || ["pbip", "desktop"].includes(project.connection.kind)
        // 显式模拟把内置 Desktop 目标视为已绑定本机文件, model:// 只表示模拟引擎; 真实能力仍完全由宿主核验并发放.
        const canUseMeasureConfig = canWrite && (project.connection?.kind === "desktop" || /\.(pbip|pbix)$/i.test(project.projectPath))
        const targetId = target === "created" ? this.createdId : this.currentId
        const file = this.measureFiles.get(targetId)
        const engine = project.model.templateMeasureConfiguration
        const effective = engine && {
            mode: engine.mode === "config" && canUseMeasureConfig && file ? ("config" as const) : ("tmdl" as const),
            measures: structuredClone(engine.mode === "config" && canUseMeasureConfig && file ? file.measures : engine.measures),
        }
        return {
            targetId,
            project: structuredClone(project),
            details: {
                tables,
                defaultDateTable: date?.name ?? null,
                defaultDateColumn: date?.columns.find((column) => column.dataType === "DateTime")?.name ?? null,
                defaultMeasureTable: tables.find((table) => table.measures.length)?.name ?? null,
            },
            canWrite,
            canGenerateSample: canWrite && project.projectPath.toLowerCase().endsWith(".pbip"),
            canUseMeasureConfig,
            measureConfiguration: effective ?? null,
            measureConfigurationRevision: canUseMeasureConfig ? (file?.revision ?? "missing") : null,
            configMeasures: canUseMeasureConfig && file ? structuredClone(file.measures) : null,
            measureConfigUnavailableReason: canUseMeasureConfig ? null : "layout",
            isCreatedTarget: target === "created",
            isSimulation: true,
        }
    }

    /** 按 input 的目标、基线、来源修订模拟修改 current 或新建目标, 返回模拟新上下文; 失效或只读输入抛错, 不执行公式或磁盘写入. */
    apply(input: Methods["configuration.apply"]["params"], current: ProjectSnapshot): ConfigurationContext {
        const target = input.targetId === this.createdId ? "created" : "current"
        const context = this.context(target, current)
        if (context.targetId !== input.targetId) throw new Error("STALE_CONFIGURATION_TARGET")
        if (!context.canWrite) throw new Error("READ_ONLY_MODEL")
        if (context.project.baselineId !== input.baselineId) throw new Error("STALE_BASELINE")
        if (input.section === "sample" && !context.canGenerateSample) throw new Error("PBIP_REQUIRED")
        if (input.request.measures && input.request.measuresMode === "config" && !context.canUseMeasureConfig) throw new Error("PBIP_REQUIRED")
        if (input.request.measures && input.request.measureConfigurationRevision !== context.measureConfigurationRevision)
            throw new Error("STALE_CONFIGURATION_SOURCE")
        const project = target === "created" ? this.created! : current
        const request = input.request
        if (request.measures) {
            if (request.measuresMode === "config")
                this.measureFiles.set(context.targetId, {
                    measures: structuredClone(request.measures),
                    // 模拟修订仅遵守宿主数据形状, 不宣称对磁盘内容计算过哈希.
                    revision: (crypto.randomUUID() + crypto.randomUUID()).replaceAll("-", ""),
                })
            else this.measureFiles.delete(context.targetId)
            // 模拟只保留模式和参数供上下文回读, 不导入真实配置表或刷新外部文件.
            project.model.templateMeasureConfiguration = { mode: request.measuresMode ?? "tmdl", measures: structuredClone(request.measures) }
            for (const item of request.measures.filter((candidate) => candidate.enabled)) {
                const old = project.model.measures.find((measure) => measure.name === item.name)
                const next = {
                    table: old?.table ?? "Measure",
                    name: item.name,
                    expression: JSON.stringify(item.value),
                    description: item.description,
                    formatString: "",
                    displayFolder: item.displayFolder,
                    isHidden: false,
                }
                if (old) Object.assign(old, next)
                else project.model.measures.push(next)
            }
        }
        project.baselineId = crypto.randomUUID()
        return this.context(target, current)
    }

    /** 仅模拟完成尾部保存, 不重新应用任何配置. */
    recover(input: Methods["configuration.complete"]["params"], current: ProjectSnapshot): ConfigurationContext {
        const target = input.targetId === this.createdId ? "created" : "current"
        const context = this.context(target, current)
        if (context.targetId !== input.targetId) throw new Error("STALE_CONFIGURATION_TARGET")
        if (context.project.baselineId !== input.baselineId) throw new Error("STALE_BASELINE")
        const project = target === "created" ? this.created! : current
        project.hasPendingChanges = false
        project.saveResult = null
        return this.context(target, current)
    }
}
