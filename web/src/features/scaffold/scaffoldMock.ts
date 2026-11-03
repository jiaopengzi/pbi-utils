/**
 * FilePath    : pbi-utils\web\src\features\scaffold\scaffoldMock.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟的脚手架草稿、冻结与创建, 不进行网络或磁盘操作.
 */
import type { ScaffoldPlan, ScaffoldRequest, TemplateMeasureDefinition, UdfSelection } from "./contracts"
import { calendarColumnName, calendarFields, recommendedFields } from "./calendarFields"
import { copy } from "./mockValue"
import { CalendarMock } from "./calendarMock"
import { UdfMock } from "./udfMock"
import templateQueries from "./templateQueries.json"
/** 提供显式模拟专用状态机, 不作为缺失宿主时的回退. */
export class ScaffoldMock {
    private calendar = new CalendarMock()
    private udf = new UdfMock()
    private draft = { id: "", revision: 0, value: {} as Record<string, unknown> }
    private plan?: ScaffoldPlan
    private output = ""
    private created = false
    /** 仅处理已有命名空间, 其他方法仍返回undefined交还原模拟分发器. */
    handle(method: string, params: Record<string, unknown>): unknown {
        if (method.startsWith("calendar.")) return this.calendar.handle(method, params)
        if (method.startsWith("udf.")) return this.udf.handle(method, params)
        if (!method.startsWith("scaffold.")) return undefined
        if (method === "scaffold.templates")
            return {
                isSimulation: true,
                calendarFields: copy(calendarFields),
                templateQueries: copy(templateQueries),
                templateDirectory: "C:/Mock/Templates",
                templates: ["blank"].map((id, index) => ({
                    id,
                    name: id,
                    source: "builtin",
                    baseline: String(index + 1).repeat(64),
                    description: `PBIP · ${id}`,
                    warnings: [],
                })),
                defaultMeasures: ["refresh_time", "version", "author", "weburl_text", "weburl", "weburl_tip"].map((name) => ({
                    name,
                    value: "",
                    dataCategory: "Uncategorized",
                    enabled: true,
                    description: "",
                    displayFolder: "",
                })),
                periods: [7, 10, 10, 10, 10, 4].flatMap((count, group) =>
                    Array.from({ length: count }, (_, index) => ({
                        id: `${["D", "W", "M", "Q", "H", "Y"][group]}${index + 1}`,
                        group: ["日/当前值", "周", "月", "季度", "半年", "年度"][group],
                        label: `${["日", "周", "月", "季度", "半年", "年度"][group]} ${index + 1}`,
                        description: "",
                        dependencies: index > 1 ? [`${["D", "W", "M", "Q", "H", "Y"][group]}1`] : [],
                    })),
                ),
            }
        if (method === "scaffold.templateDetails")
            return {
                calendarFields: copy(calendarFields),
                calendarCapabilities: [
                    {
                        tableName: "Calendar",
                        mode: "new",
                        fields: [...recommendedFields],
                        columns: [],
                        lockedFields: (params.templateId === "retail" ? ["Dates", "StartOfMonth", "StartOfYear"] : ["Dates"]).map((field) => ({
                            field,
                            reason: "templateReference",
                        })),
                    },
                ],
                tables: [
                    {
                        name: "Calendar",
                        columns: recommendedFields.map((field) => ({
                            name: calendarColumnName(field, true),
                            dataType: calendarFields.find((item) => item.id === field)!.dataType,
                        })),
                        measures: [],
                        isDate: true,
                    },
                    { name: "Measures", columns: [], measures: ["Revenue", "Quantity"], isDate: false },
                ],
                defaultDateTable: "Calendar",
                defaultDateColumn: "C01_Dates",
                defaultMeasureTable: "Measures",
            }
        if (method === "scaffold.openTemplates") return { opened: true, isSimulation: true }
        if (method === "scaffold.openDirectory" || method === "scaffold.openDesktop") {
            if (Object.keys(params).length) throw new Error("INVALID_SCAFFOLD_INPUT")
            if (!this.created) throw new Error("SCAFFOLD_PROJECT_NOT_CREATED")
            return { opened: true, isSimulation: true }
        }
        if (method === "scaffold.chooseOutput") {
            this.output = "C:/Mock/MyPowerBIProject"
            this.plan = undefined
            this.created = false
            return { directory: this.output, selectionId: "mock-selection", isSimulation: true }
        }
        if (method === "scaffold.updateDraft") {
            if (!params.draftId) this.draft = { id: crypto.randomUUID(), revision: 0, value: {} }
            else if (params.draftId !== this.draft.id || params.revision !== this.draft.revision) throw new Error("SCAFFOLD_DRAFT_STALE")
            const section = String(params.section),
                value = copy(params.value)
            if (section === "base") Object.assign(this.draft.value, value)
            else if (section === "measure") {
                const item = value as { index: number; item: TemplateMeasureDefinition | null }
                const items = this.draft.value.measures as TemplateMeasureDefinition[]
                if (item.item) items[item.index] = item.item
                else items.splice(item.index, 1)
            } else if (section === "workday") (this.draft.value.calendar as { overrides: unknown[] }).overrides.push(value)
            else if (section === "udfFunction") {
                const item = value as UdfSelection & { functionName: string }
                const list = this.draft.value.udfs as UdfSelection[]
                let target = list.find((entry) => entry.source === item.source && entry.packageId === item.packageId)
                if (!target) {
                    target = { ...item, functions: [] }
                    list.push(target)
                }
                target.functions.push(item.functionName)
            } else this.draft.value[section] = value
            this.plan = undefined
            this.draft.revision++
            return { draftId: this.draft.id, revision: this.draft.revision }
        }
        if (method === "scaffold.preview") {
            this.checkDraft(params)
            if (!this.output) throw new Error("SCAFFOLD_OUTPUT_REQUIRED")
            const request = { schemaVersion: 1, ...copy(this.draft.value) } as unknown as ScaffoldRequest
            this.plan = {
                id: crypto.randomUUID(),
                digest: "a".repeat(64),
                expiresAt: new Date(Date.now() + 900000).toISOString(),
                outputPath: this.output,
                request,
                entryFile: `pbip/${request.projectName}.pbip`,
                compatibilityLevel: request.udfs?.length ? 1702 : 1606,
                files: [
                    {
                        path: "Model/definition/model.tmdl",
                        sha256: "b".repeat(64),
                        bytes: 123,
                        before: "model Template",
                        after: `model ${request.projectName}`,
                    },
                ],
                tables: [{ name: "Calendar", rowCount: 0, bytes: 12, state: "empty" }],
                warnings: [],
                udfFunctions:
                    request.udfs?.flatMap((item) =>
                        item.functions.map((name) => ({
                            name,
                            source: item.source,
                            packageId: item.packageId,
                            version: item.version ?? "1.0.0",
                            isDependency: false,
                        })),
                    ) ?? [],
                isSimulation: true,
            }
            return copy(this.plan)
        }
        if (method === "scaffold.create") {
            this.checkDraft(params)
            const plan = this.plan
            if (!plan || plan.digest !== params.digest) throw new Error("INVALID_SCAFFOLD_PLAN")
            this.plan = undefined
            this.created = true
            return {
                planId: plan.id,
                state: "published",
                projectCreated: true,
                verified: true,
                gitInitialized: plan.request.gitInitialize,
                projectPath: `${plan.outputPath.replace(/[\\/]+$/, "")}/${plan.entryFile}`,
                baselineId: "mock-baseline",
                tables: plan.tables,
                warnings: [],
                isSimulation: true,
            }
        }
        if (method === "scaffold.discard") {
            const discarded = this.plan?.digest === params.digest
            if (discarded) this.plan = undefined
            return { discarded }
        }
        throw new Error("UNKNOWN_METHOD")
    }
    /** 读取和提交始终复核相同草稿身份和版本. */
    private checkDraft(params: Record<string, unknown>): void {
        if (params.draftId !== this.draft.id || params.revision !== this.draft.revision) throw new Error("SCAFFOLD_DRAFT_STALE")
    }
}
