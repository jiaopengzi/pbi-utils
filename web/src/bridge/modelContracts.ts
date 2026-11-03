/**
 * FilePath    : pbi-utils\web\src\bridge\modelContracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 定义模型、连接、示例及配置的可显示数据契约, 不持有运行时资源.
 */

import type {
    TemplateDetails,
    TemplateMeasureDefinition,
    TemplateMeasureMode,
    ScaffoldCalendarOptions,
    ScaffoldPeriodOptions,
    UdfSelection,
} from "@/features/scaffold/contracts"

/** 两个入口共用的功能配置; 目标路径及一次确认只由宿主持有. */
export interface ModelConfigurationRequest {
    measures?: TemplateMeasureDefinition[] | null
    measuresMode?: TemplateMeasureMode
    /** 当前模板草稿读取时的文件修订, 仅随模板度量值动作提交, 不采用提交前刷新的新修订. */
    measureConfigurationRevision?: string | null
    sample?: SampleDataOptions | null
    calendar?: ScaffoldCalendarOptions | null
    periods?: ScaffoldPeriodOptions | null
    udfs?: UdfSelection[] | null
    allowCompatibilityUpgrade?: boolean
    locale?: string
}
/** 宿主绑定的配置上下文, 能力仅供展示, 后端仍独立复核权限与基线. */
export interface ConfigurationContext {
    targetId: string
    project: ProjectSnapshot
    details: TemplateDetails
    canWrite: boolean
    canGenerateSample: boolean
    /** 仅宿主确认可信 PBIP 资产可参与当前事务时允许 Config; 缺失按不可用处理. */
    canUseMeasureConfig?: boolean
    /** 宿主按当前可用来源读取的模式和六字段, 与 project.model 的引擎事实分开. */
    measureConfiguration?: { mode: TemplateMeasureMode; measures: TemplateMeasureDefinition[] } | null
    /** 配置文件的 SHA-256 修订或 missing, 不可读取的来源为 null. */
    measureConfigurationRevision?: string | null
    /** 有效 Config 文件的六字段; 空数组有效, null 表示尚无文件值. */
    configMeasures?: TemplateMeasureDefinition[] | null
    measureConfigUnavailableReason?: "layout" | "invalid" | null
    isCreatedTarget: boolean
    noChanges?: boolean
    isSimulation?: boolean
}

/** 三种来源共用的只读计数; null 表示宿主没有提供, 已知零值仍保留. */
export interface ModelSummary {
    compatibilityLevel: number | null
    tableCount: number | null
    measureCount: number | null
    reportPageCount: number | null
}
/** 只读模型概要, 不包含模型表达式或数据源凭据. */
export interface PowerBiModel {
    name: string
    compatibilityLevel: number
    tableCount: number
    measureCount: number
    summary?: ModelSummary | null
}
/** 单个本机实例, 不可用时保留明确状态. */
export interface PowerBiInstance {
    processId: number
    /** 宿主发现的引擎身份摘要, 只供展示比对; 缺失时不能标记当前已连接. */
    instanceId?: string | null
    /** 官方 Desktop Bridge 核验的路径, 不可读取时保留明确状态. */
    filePath?: string | null
    fileIdentityStatus?: "verified" | "unsaved" | "unavailable" | null
    fileIdentityErrorCode?: string | null
    title: string
    port: number | null
    status: "connected" | "starting" | "unavailable"
    errorCode: string | null
    message: string | null
    models: PowerBiModel[]
}
/** 一次扫描结果, 时间为宿主 UTC 时间. */
export interface PowerBiSnapshot {
    timestamp: string
    instances: PowerBiInstance[]
}
/** 模型编辑请求不包含路径或连接地址, 仅绑定宿主当前模型. */
export interface ModelEditRequest {
    operation: string
    objectName: string
    property: string
    value: string
    table: string
    pageId: string
}
/** 绑定当前完整基线的只读结果; 未完整识别时禁止删除. */
export interface UdfLintResult {
    projectId: string
    baselineId: string
    unused: string[]
    totalCount: number
    complete: boolean
    isSimulation?: boolean
}
/** 多表导出只绑定当前模型和精确基线, 文件/目录路径与固定查询由宿主持有. */
export interface ModelTableExportRequest {
    rowLimit: number | null
    projectId: string
    baselineId: string
    tables: string[]
    format: "xlsx" | "csv"
    includeLog: boolean
    locale: "zh-cn" | "en-us"
}
/** 导出完成只返回文件名和实际行数, 模拟预览不得冒充真实文件. */
export interface ModelTableExportResult {
    rowLimit: number | null
    locationWarning: string | null
    fileName: string
    rowCount: number
    format: "xlsx" | "csv"
    tables: { table: string; rowCount: number }[]
    isSimulation: boolean
}
/** 统计独立于定义指纹; 架构成员数带估计标记, 未提供字段保持null. */
export interface ModelTableStatisticsResult {
    projectId: string
    baselineId: string
    tables: {
        table: string
        rowCount: number | null
        sizeBytes: number | null
        isEstimate: boolean
        isQueryCount: boolean
        rowCountUnavailableReason?: "no_queryable_columns" | null
    }[]
    isSimulation: boolean
}
/** 度量值批次仅允许已声明属性; 未提供表示保留, 空字符串表示清空. */
export interface MeasureChange {
    action: "create" | "update" | "delete"
    table: string
    name: string
    expression?: string
    description?: string
    formatString?: string
    displayFolder?: string
    isHidden?: boolean
}
/** 示例生成只传业务参数, 输出目录由原生宿主绑定. */
export interface SampleDataOptions {
    locale: "zh-cn" | "en-us"
    productCount: number
    storeCount: number
    inventoryCycle: number
    startDate: string
    endDate: string
    seed?: number
}
/** 宿主实际读取的示例产物状态, 模拟预览必须显式标识. */
export interface SampleDataState {
    outputPath: string
    hasData: boolean
    lastFactDate: string | null
    tables: { name: string; rows: number; size: number }[]
    isSimulation?: boolean
}
/** 生成完成后的真实产物统计. */
export interface SampleDataResponse {
    state: SampleDataState
    result: { products: number; stores: number; customers: number; inventory: number; orders: number; orderItem: number }
}
/** 服务账号只含显示名称; 模拟状态不能用作真实登录证据. */
export interface PowerBiServiceStatus {
    account: { username: string; environmentName: string; environmentLabel: string } | null
    recentLogins: RecentPowerBiLogin[]
    storageWarning?: string | null
    isSimulation: boolean
}
/** 本机最近登录只含邮箱与云环境; 令牌和密码不进入桥接. */
export interface RecentPowerBiLogin {
    id: string
    email: string
    environmentName: string
    environmentLabel: string
    lastUsedAt: string
}
/** 单一登录用例在多云未选择时返回环境, 否则返回已登录账号. */
export interface PowerBiLoginResult extends PowerBiServiceStatus {
    environments: { name: string; displayName: string }[]
}
/** 服务目录只传后端发布的标识, 不传地址或令牌. */
export interface PowerBiServiceModel {
    id: string
    name: string
    workspace: string
    unavailableReason: string | null
    workspaceId?: string | null
    modelId?: string | null
}
/** 云端最近连接只含当前账号和权限已核验的目录句柄, 不接收网页地址. */
export interface RecentPowerBiServiceModel {
    id: string
    modelId: string
    name: string
    workspace: string
    lastUsedAt: string
}
/** 最近云端模型独立返回本机存储警告, 不掩盖目录或权限读取失败. */
export interface RecentPowerBiServiceModelsResult {
    items: RecentPowerBiServiceModel[]
    storageWarning: string | null
}
/** 宿主统一界面偏好, 模拟值明确标识且不代表实际持久化. */
export interface UiSettings {
    locale: "zh-cn" | "en-us"
    showRecentFiles: boolean
    daxFormatStyle: "short" | "long"
    warning: string | null
    isSimulation: boolean
}
/** 离线工程的真实只读快照, 字段由共享 C# 服务产生. */
export interface ProjectSnapshot {
    summary?: ModelSummary | null
    saveResult?: { modelSaved: boolean; fileSaved: boolean; desktopSynchronized: boolean; message: string; errorCode?: string | null } | null
    hasPendingChanges?: boolean
    connection?: {
        kind: "pbip" | "desktop" | "powerbi-service" | "ssas-xmla"
        label: string
        isLimited?: boolean
        /** 仅 Desktop 来源可返回宿主引擎身份摘要; 旧快照或远端来源不使用它判断已连接. */
        instanceId?: string | null
        /** 当前实例的权威文件路径; 不可核验时不从名称猜测. */
        filePath?: string | null
        fileIdentityStatus?: "verified" | "unsaved" | "unavailable" | null
        fileIdentityErrorCode?: string | null
        /** 宿主注册的 Service 目录句柄, 只用于当前模型展示匹配. */
        modelId?: string | null
    } | null
    projectPath: string
    projectId: string
    baselineId: string
    modelFormat: string
    reportFormat: string
    modelVersion: string
    reportVersion: string | null
    model: {
        name: string
        compatibilityLevel: number
        culture: string
        tables: {
            name: string
            description: string | null
            isHidden: boolean
            columns: { name: string; kind: string; dataType: string; isHidden: boolean; description: string | null }[]
            partitionKinds: string[]
        }[]
        measures: {
            table: string
            name: string
            expression: string
            description: string | null
            formatString: string | null
            displayFolder: string | null
            isHidden: boolean
        }[]
        functions: { name: string; expression: string; description: string | null }[]
        periods?: ScaffoldPeriodOptions | null
        /** 宿主完整核验的模板参数和来源模式, 用于首次初始化当前目标草稿. */
        templateMeasureConfiguration?: { mode: TemplateMeasureMode; measures: TemplateMeasureDefinition[] } | null
        relationshipCount: number
        roleCount: number
    }
    pages: {
        id: string
        displayName: string
        width: number | null
        height: number | null
        visuals: { id: string; type: string; x: number | null; y: number | null; width: number | null; height: number | null }[]
    }[]
    files: { path: string; sha256: string }[]
    warnings: string[]
}
/** 远端历史只包含非秘密摘要, 已保存密码始终不回填网页. */
export interface RecentRemoteConnection {
    id: string
    server: string
    database: string
    authenticationMode: string
    userName?: string | null
    hasSavedPassword: boolean
}
/** 远端平铺表单的宿主一次性上下文, 不含保存的密码. */
export interface RemoteConnectionForm {
    formId: string
    recent: RecentRemoteConnection[]
    error?: string | null
    isSimulation?: boolean
}
/** 只在本次连接请求传递新输入秘密, 不写入前端存储或其他业务消息. */
export interface RemoteConnectionValues {
    server: string
    database: string
    authenticationMode: string
    userName: string
    password: string
    remember: boolean
    rememberPassword: boolean
    recentId: string
}

/** 最近成功导出的宿主目录记录, id只用于匹配打开, displayPath仅展示. */
export interface ModelExportLocation {
    id: string | null
    displayPath: string | null
    available: boolean
    warning: string | null
    isSimulation: boolean
}
