/**
 * FilePath    : pbi-utils\web\src\features\scaffold\contracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 精确映射共享脚手架与函数包 DTO, 不提供任意文件路径能力.
 */
import type { ProjectSnapshot, SampleDataOptions } from "@/bridge/client"
/** 模板参数写入 TMDL 常量或由项目 Config 导入表读取, 不表示模型写授权. */
export type TemplateMeasureMode = "tmdl" | "config"
export interface TemplateMeasureDefinition {
    name: string
    value: string
    dataCategory: string
    enabled: boolean
    description: string
    displayFolder: string
}
export interface PbipTemplateInfo {
    id: string
    name: string
    source: string
    baseline: string
    description: string
    warnings: string[]
}
export interface ScaffoldPeriodTemplate {
    id: string
    group: string
    label: string
    description: string
    dependencies: string[]
}
export interface WorkdayOverride {
    date: string
    isWorkday: boolean
    name: string
}
export interface ScaffoldCalendarOptions {
    kind: string
    startYear: number
    endYear: number
    tableName: string
    overrides: WorkdayOverride[]
    fields?: string[]
    numberedColumns?: boolean
    workdays?: number[]
}
/** 宿主权威字段目录, 序号与稳定语义永不随筛选或语言变化. */
export interface CalendarFieldDefinition {
    id: string
    number: number
    dataType: string
    group: string
    description: string
    sortBy?: string | null
    requiresLunar: boolean
}
/** 只展示宿主核验的原列映射和必需字段, 不根据列名前缀推断归属. */
export interface CalendarCapability {
    tableName: string
    mode: "new" | "existing"
    fields: string[]
    columns: { field: string; name: string }[]
    lockedFields: { field: string; reason: string }[]
    dateColumn?: string | null
    warning?: string | null
}
/** 通用字段方案不包含目标、年份、工作日或写入授权. */
export interface CalendarPreferences {
    fields: string[]
    numberedColumns: boolean
    warning?: string | null
    isSimulation?: boolean
}
/** 预览上下文只引用宿主已有目标或精确模板基线, 不携带路径及列映射权限. */
export type CalendarPreviewContext =
    | { kind: "template"; templateId: string; templateBaseline: string }
    | { kind: "model"; targetId: string; baselineId: string }
/** 函数和调用均为宿主生成的完整原文, 摘要只对应此次输入而非写入授权. */
export interface CalendarCodeProgram {
    functionName: string
    functionExpression: string
    invocationExpression: string
    inputDigest: string
    contextBaseline: string
    generatorVersion: string
    isSimulation?: boolean
}
export interface ScaffoldPeriodOptions {
    dateTable: string
    dateColumn: string
    measureTable: string
    baseMeasures: string[]
    templates: string[]
    folder: string
    numberDigits: number
    percentDigits: number
}
export interface UdfSelection {
    source: string
    packageId: string
    version: string | null
    functions: string[]
    baseline: string | null
    sourceBaseline?: string | null
    catalogBaseline?: string | null
}
/** 完整本地目录的宿主状态, 时间只表示最后成功更新. */
export interface UdfCatalogStatus {
    initialized: boolean
    baseline: string | null
    retrievedAt: string | null
    packageCount: number
    versionCount: number
    functionCount: number
}
export interface UdfSourceEntry {
    id: string
    name: string
    baseUrl: string | null
    baseline: string | null
    kind: "builtin" | "local" | "custom"
    protocol: "git"
    gitRef?: string | null
}
export interface ScaffoldRequest {
    schemaVersion: number
    templateId: string
    templateBaseline: string
    projectName: string
    locale: string
    gitInitialize: boolean
    measures: TemplateMeasureDefinition[] | null
    measuresMode?: TemplateMeasureMode
    sample: SampleDataOptions | null
    calendar: ScaffoldCalendarOptions | null
    periods: ScaffoldPeriodOptions | null
    udfs: UdfSelection[] | null
    templateQueries?: string[] | null
    templateQueryGroups?: Record<string, string> | null
}
/** 宿主固定查询目录, 原文供预览复制与创建共用, 不接受网页编辑代码. */
export interface TemplateQueryDefinition {
    id: string
    queryGroup: string
    expression: string
}
export interface ScaffoldTableResult {
    name: string
    rowCount: number
    bytes: number
    state: string
}
export interface ScaffoldPlan {
    id: string
    digest: string
    expiresAt: string
    outputPath: string
    request: ScaffoldRequest
    entryFile: string
    files: { path: string; sha256: string; bytes: number; before?: string | null; after?: string | null }[]
    tables: ScaffoldTableResult[]
    warnings: string[]
    compatibilityLevel: number
    udfFunctions?: { name: string; source: string; packageId: string; version: string; isDependency: boolean }[] | null
    isSimulation?: boolean
}
export interface ScaffoldResult {
    planId: string
    state: string
    projectCreated: boolean
    verified: boolean
    gitInitialized: boolean
    projectPath: string
    baselineId: string | null
    tables: ScaffoldTableResult[]
    warnings: string[]
    errorCode?: string | null
    isSimulation?: boolean
}
export interface ModelConfigurationCatalog {
    tables: { name: string; columns: { name: string; dataType: string }[]; measures: string[]; isDate: boolean }[]
    defaultDateTable: string | null
    defaultDateColumn: string | null
    defaultMeasureTable: string | null
    calendarFields?: CalendarFieldDefinition[]
    calendarCapabilities?: CalendarCapability[]
    periods?: ScaffoldPeriodOptions | null
}
/** 模板与当前模型共用中性目录形状, 保留原类型名兼容已有桥接. */
export type TemplateDetails = ModelConfigurationCatalog
export interface UdfPackageSummary {
    id: string
    version: string
    description: string
}
export interface UdfPackage {
    id: string
    version: string
    hash: string
    description: string
    readme: string
    licenseFiles: Record<string, string>
    dependencies: { id: string; version: string }[]
    functions: { name: string; expression: string; description: string; annotations: Record<string, string> }[]
    retrievedAt: string
    fromCache: boolean
    /** 精确版本目录最后修改 Git commit 的 committer 时间; 无法证明时为 null, 不借用缓存时间. */
    committedAt: string | null
    metadata?: UdfPackageMetadata | null
}
/** 包内作者/分类与可选安全本地图标; 旧完整缓存可以没有此元数据. */
export interface UdfPackageMetadata {
    authors: string
    tags: string[]
    releaseNotes: string
    projectUrl: string | null
    repositoryUrl: string | null
    iconDataUrl?: string | null
}
/** 当前页以精确包为最小单元, 不携 README、许可或表达式. */
export interface UdfCatalogPackage {
    id: string
    version: string
    hash: string
    description: string
    authors: string
    tags: string[]
    /** 精确版本目录最后修改 Git commit 的 committer 时间; 无法证明时为 null, 不借用缓存时间. */
    committedAt: string | null
    iconKey: string | null
    functionCount: number
}
/** 单个有界传输页; nextOffset 按实际包数量前进, 前端补足当前显示页. */
export interface UdfPackagePage {
    items: UdfCatalogPackage[]
    total: number
    nextOffset: number | null
    status: UdfCatalogStatus
}
/** 图标只接受宿主返回的身份及包内PNG数据, 不接受网页URL或路径. */
export interface UdfCatalogIcon {
    key: string
    dataUrl: string | null
}
/** 本地函数依赖绑定精确来源、版本及内容基线. */
export interface LocalUdfDependency {
    source: string
    packageId: string
    version: string
    function: string
    baseline: string | null
    sourceBaseline?: string | null
}
export interface LocalUdfDefinition {
    schemaVersion: 1
    name: string
    version: string
    expression: string
    description: string
    dependencies: LocalUdfDependency[]
}
export interface LocalUdfEntry {
    id: string
    baseline: string
    definition: LocalUdfDefinition
}
export interface DraftIdentity {
    draftId: string
    revision: number
}
export interface ScaffoldMethods {
    "calendar.preview": {
        params: { context: CalendarPreviewContext; calendar: ScaffoldCalendarOptions; locale: "zh-cn" | "en-us" }
        result: CalendarCodeProgram
    }
    "calendar.preferences": { params: Record<string, never>; result: CalendarPreferences }
    "calendar.savePreferences": { params: { fields: string[]; numberedColumns: boolean }; result: CalendarPreferences }
    "calendar.parseWorkdays": {
        params: { text: string; delimiter: "csv" | "tsv"; startYear: number; endYear: number }
        result: { overrides: WorkdayOverride[] }
    }
    "scaffold.templates": {
        params: Record<string, never>
        result: {
            templates: PbipTemplateInfo[]
            defaultMeasures: TemplateMeasureDefinition[]
            templateQueries: TemplateQueryDefinition[]
            measurePreferencesWarning?: string | null
            periods: ScaffoldPeriodTemplate[]
            calendarFields?: CalendarFieldDefinition[]
            templateDirectory: string
            git?: { available: boolean; reason?: string | null }
            isSimulation?: boolean
        }
    }
    "scaffold.templateDetails": { params: { templateId: string; templateBaseline: string }; result: TemplateDetails }
    "scaffold.chooseOutput": { params: Record<string, never>; result: { directory: string; selectionId: string; isSimulation?: boolean } }
    "scaffold.openTemplates": { params: Record<string, never>; result: { opened: boolean; isSimulation?: boolean } }
    "scaffold.openDirectory": { params: Record<string, never>; result: { opened: boolean; isSimulation?: boolean } }
    "scaffold.openDesktop": { params: Record<string, never>; result: { opened: boolean; project: ProjectSnapshot; isSimulation?: boolean } }
    "scaffold.updateDraft": { params: { draftId?: string; revision?: number; section: string; value: unknown }; result: DraftIdentity }
    "scaffold.preview": { params: DraftIdentity; result: ScaffoldPlan }
    "scaffold.create": { params: DraftIdentity & { digest: string }; result: ScaffoldResult }
    "scaffold.discard": { params: { digest: string }; result: { discarded: boolean } }
    "udf.catalog": {
        params: {
            action: "status" | "search" | "versions" | "package" | "packages" | "icon"
            catalogBaseline?: string | null
            offset?: number
            limit?: number
            source?: string
            sourceBaseline?: string | null
            query?: string
            includePrerelease?: boolean
            packageId?: string
            version?: string
            packageVersions?: Record<string, string>
            iconKey?: string
        }
        result: {
            total?: number
            nextOffset?: number | null
            status?: UdfCatalogStatus
            packages?: UdfPackageSummary[]
            versions?: string[]
            package?: UdfPackage
            fromCache?: boolean
            retrievedAt?: string | null
            packagePage?: UdfPackagePage
            icon?: UdfCatalogIcon
        }
    }
    "udf.catalogRefresh": { params: { source?: string; sourceBaseline?: string | null }; result: UdfCatalogStatus }

    "udf.sources": { params: Record<string, never>; result: { items: UdfSourceEntry[] } }
    "udf.sourceSave": {
        params: { name: string; baseUrl: string; id?: string; baseline?: string; protocol?: "git"; gitRef?: string | null }
        result: UdfSourceEntry
    }
    "udf.sourceDelete": { params: { id: string; baseline: string; confirm: true }; result: { deleted: boolean } }
    "udf.localList": { params: Record<string, never>; result: { items: LocalUdfEntry[] } }
    "udf.localGet": { params: { id: string }; result: LocalUdfEntry }
    "udf.localSave": { params: { id?: string; baseline?: string; definition: LocalUdfDefinition }; result: LocalUdfEntry }
    "udf.localDelete": { params: { id: string; baseline: string; confirm: true }; result: { deleted: boolean } }
}
