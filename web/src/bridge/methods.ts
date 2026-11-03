/**
 * FilePath    : pbi-utils\web\src\bridge\methods.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 集中声明宿主白名单方法的输入与结果映射, 由机器契约测试逐项核验.
 */

import type { ScaffoldMethods } from "@/features/scaffold/contracts"
import type { ProductUpdateState, ProductUpdateSettings, ProductUpdateCheckResponse, ProductUpdateSource, ProductUpdateTarget } from "./updateContracts"
import type {
    ModelConfigurationRequest,
    ConfigurationContext,
    PowerBiSnapshot,
    ModelEditRequest,
    UdfLintResult,
    ModelExportLocation,
    ModelTableExportRequest,
    ModelTableExportResult,
    ModelTableStatisticsResult,
    SampleDataOptions,
    SampleDataState,
    SampleDataResponse,
    PowerBiServiceStatus,
    PowerBiLoginResult,
    PowerBiServiceModel,
    RecentPowerBiServiceModelsResult,
    UiSettings,
    ProjectSnapshot,
    RemoteConnectionForm,
    RemoteConnectionValues,
} from "./modelContracts"
import type { LogLevel, LoggingView } from "./loggingContracts"
import type { McpConfiguration, McpConnectionsSnapshot, McpDisconnectResult } from "./mcpContracts"

export interface Methods extends ScaffoldMethods {
    "updates.status": { params: Record<string, never>; result: ProductUpdateState }
    "updates.save": { params: Partial<ProductUpdateSettings>; result: ProductUpdateState }
    "updates.check": { params: { source: ProductUpdateSource; automatic: boolean }; result: ProductUpdateCheckResponse }
    "updates.open": {
        params: { source: ProductUpdateSource; version: string; target: ProductUpdateTarget }
        result: { opened: boolean; isSimulation: boolean }
    }
    "mcp.connections": { params: Record<string, never>; result: McpConnectionsSnapshot }
    "mcp.disconnect": { params: { connectionId: string }; result: McpDisconnectResult }
    "mcp.setAdmission": { params: { acceptingConnections: boolean }; result: McpConnectionsSnapshot }
    "mcp.configuration": { params: Record<string, never>; result: McpConfiguration }
    "learning.update": {
        params: { visible: boolean; left: number; top: number; width: number; height: number; viewportWidth: number; viewportHeight: number }
        result: { embedded: boolean; isSimulation: boolean }
    }
    "learning.close": { params: Record<string, never>; result: { closed: boolean; isSimulation: boolean } }
    "learning.open": { params: Record<string, never>; result: { opened: boolean; isSimulation: boolean } }
    "configuration.context": {
        params: { target: "created" | "current" }
        result: ConfigurationContext
    }
    "configuration.apply": {
        params: { targetId: string; baselineId: string; section: "sample" | "measures" | "calendar" | "udfs"; request: ModelConfigurationRequest }
        result: ConfigurationContext
    }
    "configuration.complete": { params: { targetId: string; baselineId: string }; result: ConfigurationContext }
    "configuration.discard": Methods["configuration.complete"]
    "ui.dialogRespond": { params: { dialogId: string; action: string; values: Record<string, string> }; result: { accepted: boolean } }
    "ui.settings": { params: Record<string, never>; result: UiSettings }
    "app.resetUserData": { params: Record<string, never>; result: { scheduled: boolean; isSimulation?: boolean } }
    "ui.saveSettings": { params: Partial<Pick<UiSettings, "locale" | "showRecentFiles" | "daxFormatStyle">>; result: UiSettings }
    "dax.format": { params: { value: string; style: "short" | "long" }; result: { expressions: string[]; isSimulation?: boolean } }
    "model.edit": { params: ModelEditRequest; result: ProjectSnapshot }
    "model.udfLint": { params: { projectId: string; baselineId: string }; result: UdfLintResult }
    "model.exportTables": { params: ModelTableExportRequest; result: ModelTableExportResult }
    "model.tableStatistics": { params: { projectId: string; baselineId: string }; result: ModelTableStatisticsResult }
    "model.tableRowCounts": { params: { projectId: string; baselineId: string; tables: string[] }; result: ModelTableStatisticsResult }
    "model.exportLocation": { params: Record<string, never>; result: ModelExportLocation }
    "model.openExportDirectory": { params: { id: string }; result: { opened: boolean; isSimulation: boolean } }
    "model.save": { params: Record<string, never>; result: ProjectSnapshot }
    "model.discard": { params: Record<string, never>; result: ProjectSnapshot }
    "logging.settings": { params: Record<string, never>; result: LoggingView }
    "logging.save": { params: { level: LogLevel }; result: LoggingView }
    "logging.openDirectory": { params: Record<string, never>; result: { opened: boolean; isSimulation: boolean } }
    "powerbi.serviceStatus": { params: Record<string, never>; result: PowerBiServiceStatus }
    "powerbi.serviceSignIn": { params: { email: string; environmentName: string; remember: boolean }; result: PowerBiLoginResult }
    "powerbi.serviceDeleteLogin": { params: { id: string }; result: PowerBiServiceStatus }
    "powerbi.serviceSignOut": Methods["powerbi.serviceStatus"]
    "powerbi.serviceModels": { params: Record<string, never>; result: PowerBiServiceModel[] }
    "powerbi.serviceRecentModels": { params: Record<string, never>; result: RecentPowerBiServiceModelsResult }
    "powerbi.serviceDeleteRecentModel": { params: { id: string }; result: { deleted: boolean } }
    "project.connectService": { params: { modelId: string }; result: ProjectSnapshot }
    "project.connectLocal": { params: { processId: string; instanceId?: string }; result: ProjectSnapshot }
    /** 只消费已认证桌面启动上下文中的一次性目标, 网页不能传入服务器或数据库. */
    "project.connectExternalTool": { params: Record<string, never>; result: ProjectSnapshot }
    "remote.form": { params: Record<string, never>; result: RemoteConnectionForm }
    "remote.mutate": { params: { formId: string; action: "delete" | "clearPassword"; recordId: string }; result: RemoteConnectionForm }
    "project.connectRemote": { params: { formId: string; values: RemoteConnectionValues }; result: ProjectSnapshot }
    "project.disconnect": { params: Record<string, never>; result: { disconnected: boolean } }
    "project.recent": { params: Record<string, never>; result: { id: string; path: string; name: string; openedAt: string }[] }
    "project.openRecent": { params: { id: string }; result: ProjectSnapshot }
    "project.deleteRecent": { params: { id: string }; result: { deleted: boolean } }
    "sample.status": { params: Record<string, never>; result: SampleDataState }
    "sample.chooseDirectory": Methods["sample.status"]
    "sample.generate": { params: SampleDataOptions & { mode: "full" | "incremental" }; result: SampleDataResponse }
    "sample.clear": Methods["sample.status"]
    "sample.openDirectory": { params: Record<string, never>; result: { opened: boolean; isSimulation?: boolean } }
    /** 仅可信 MCP 通道可调用; 普通网页调用返回 MCP_ONLY. */
    "project.openPath": { params: { path: string }; result: ProjectSnapshot }
    "project.open": { params: Record<string, never>; result: ProjectSnapshot }
    "project.inspect": { params: Record<string, never>; result: ProjectSnapshot }
    "app.info": {
        params: Record<string, never>
        result: {
            version: string
            webView2: string
            hasExternalToolTarget?: boolean
        }
    }
    "powerbi.instances": { params: Record<string, never>; result: PowerBiSnapshot }
    doctor: {
        params: Record<string, never>
        result: {
            diagnostic: { version: string; environment: { os: string; architecture: string; runtime: string; desktop: string; powerBi: string } }
            webView2: string
            isSimulation?: boolean
        }
    }
    "request.cancel": {
        params: { requestId: string }
        result: { accepted: boolean; reason?: string }
    }
}
