/**
 * FilePath    : pbi-utils\web\src\bridge\mockSettings.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供显式预览的偏好、示例统计与诊断结果.
 */

import type { SampleDataState, UiSettings } from "./client"
import type { MockRequest, MockSend } from "./mockRequest"

/** 提供显式预览的偏好、示例统计与诊断结果. */
export class MockSettings {
    private uiSettings: UiSettings = { locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "long", warning: null, isSimulation: true }
    private acceptingConnections = true
    private logLevel = "Information"
    sampleState: SampleDataState = {
        outputPath: "C:/Users/preview/PowerBISampleData",
        hasData: false,
        lastFactDate: null,
        tables: [],
        isSimulation: true,
    }
    /** 绑定预览消息出口, 不读写用户设置或业务产物. */
    constructor(private send: MockSend) {}
    /** 处理本领域模拟请求; 返回是否已回复, 未匹配时交还分发器. */
    handle(request: MockRequest): boolean {
        const { id, method, params } = request
        const reply = (result: unknown) => this.send({ protocolVersion: 1, id, result })
        const fail = (code: string) => this.send({ protocolVersion: 1, id, error: { code, message: code } })
        if (method === "mcp.connections" || method === "mcp.setAdmission" || method === "mcp.disconnect") {
            const mcpParams: Record<string, unknown> = params
            if (method === "mcp.disconnect") {
                if (Object.keys(params).length !== 1 || typeof mcpParams.connectionId !== "string" || !mcpParams.connectionId.trim()) fail("VALIDATION_ERROR")
                else reply({ accepted: false })
            } else if (method === "mcp.setAdmission") {
                if (Object.keys(params).length !== 1 || typeof mcpParams.acceptingConnections !== "boolean") fail("VALIDATION_ERROR")
                else {
                    this.acceptingConnections = mcpParams.acceptingConnections
                    reply({ acceptingConnections: this.acceptingConnections, connections: [] })
                }
            } else if (Object.keys(params).length) fail("VALIDATION_ERROR")
            else reply({ acceptingConnections: this.acceptingConnections, connections: [] })
            return true
        }
        if (method === "mcp.configuration") {
            if (Object.keys(params).length) fail("VALIDATION_ERROR")
            else {
                const command = "C:/Program Files/pbi-utils/pbi-utils.exe"
                reply({
                    executablePath: command,
                    readWrite: { command, args: ["mcp", "serve", "--allow-write"] },
                    isSimulation: true,
                })
            }
            return true
        }
        if (method === "ui.settings" || method === "ui.saveSettings") {
            const expected = method === "ui.settings" ? [] : ["locale", "showRecentFiles", "daxFormatStyle"]
            if ((method === "ui.saveSettings" && Object.keys(params).length === 0) || Object.keys(params).some((key) => !expected.includes(key))) {
                fail("VALIDATION_ERROR")
                return true
            }
            if (method === "ui.saveSettings") {
                if (
                    (Object.hasOwn(params, "locale") && typeof params.locale !== "string") ||
                    (Object.hasOwn(params, "daxFormatStyle") && typeof params.daxFormatStyle !== "string")
                ) {
                    fail("VALIDATION_ERROR")
                    return true
                }
                if (Object.hasOwn(params, "locale") && params.locale !== "zh-cn" && params.locale !== "en-us") {
                    fail("INVALID_UI_LOCALE")
                    return true
                }
                if (Object.hasOwn(params, "showRecentFiles") && typeof params.showRecentFiles !== "boolean") {
                    fail("VALIDATION_ERROR")
                    return true
                }
                if (Object.hasOwn(params, "daxFormatStyle") && params.daxFormatStyle !== "long" && params.daxFormatStyle !== "short") {
                    fail("INVALID_DAX_FORMAT_STYLE")
                    return true
                }
                this.uiSettings = {
                    ...this.uiSettings,
                    locale: params.locale ?? this.uiSettings.locale,
                    showRecentFiles: params.showRecentFiles ?? this.uiSettings.showRecentFiles,
                    daxFormatStyle: params.daxFormatStyle ?? this.uiSettings.daxFormatStyle,
                }
            }
            reply(structuredClone(this.uiSettings))
            return true
        }
        if (method === "sample.status" || method === "sample.chooseDirectory") {
            reply(structuredClone(this.sampleState))
            return true
        }
        if (method === "sample.openDirectory") {
            reply({ opened: false, isSimulation: true })
            return true
        }
        if (method === "sample.clear") {
            this.sampleState = { ...this.sampleState, hasData: false, lastFactDate: null, tables: [] }
            reply(structuredClone(this.sampleState))
            return true
        }
        if (method === "sample.generate") {
            this.sampleState.hasData = true
            this.sampleState.lastFactDate = params.endDate ?? null
            this.sampleState.tables = [
                "T00_产品表",
                "T01_门店表",
                "T02_客户表",
                "T03_入库信息表",
                "T04_订单主表",
                "T05_订单子表",
                "T06_销售目标表",
                "D00_大区表",
                "D01_省份表",
                "D02_城市表",
                "D03_区县表",
            ].map((name, index) => ({
                name,
                rows: index === 0 ? (params.productCount ?? 666) : index === 1 ? (params.storeCount ?? 55) : 1000,
                size: 1024 * (index + 1),
            }))
            reply({
                state: structuredClone(this.sampleState),
                result: {
                    products: params.productCount ?? 666,
                    stores: params.storeCount ?? 55,
                    customers: 1000,
                    inventory: 1000,
                    orders: 1000,
                    orderItem: 1000,
                },
            })
            return true
        }
        if (method === "request.cancel") {
            // 当前预览不持有可取消的后台任务; 未知或已终结请求不生成第二个终态.
            reply({ accepted: false })
            return true
        }
        if (method === "app.info") {
            reply({
                version: "browser-preview",
                webView2: "未连接",
                hasExternalToolTarget: false,
            })
            return true
        }
        if (method === "powerbi.instances") {
            reply({
                timestamp: new Date().toISOString(),
                instances: [
                    {
                        processId: 1000,
                        instanceId: "mock-desktop-instance-1000",
                        title: "示例报表 (模拟数据)",
                        port: 50000,
                        status: "connected",
                        errorCode: null,
                        message: null,
                        models: [{ name: "示例模型", compatibilityLevel: 1600, tableCount: 6, measureCount: 12 }],
                    },
                ],
            })
            return true
        }
        if (method === "logging.settings" || method === "logging.save") {
            if (method === "logging.save") {
                const level = (params as { level?: string }).level
                if (!level || !["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"].includes(level)) {
                    fail("INVALID_LOG_LEVEL")
                    return true
                }
                this.logLevel = level
            }
            reply({ level: this.logLevel, directory: ".pbi-utils/logs（浏览器模拟）", warning: null, isSimulation: true })
            return true
        }
        if (method === "logging.openDirectory") {
            reply({ opened: false, isSimulation: true })
            return true
        }
        if (method === "doctor") {
            reply({
                diagnostic: {
                    version: "browser-preview",
                    environment: { os: "浏览器模拟", architecture: "未检测", runtime: "未检测", desktop: "simulation", powerBi: "simulation" },
                },
                webView2: "未检测",
                isSimulation: true,
            })
            return true
        }
        return false
    }
}
