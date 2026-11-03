/**
 * FilePath    : pbi-utils\web\src\i18n\diagnosticMessages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 运行环境分区标签、说明和状态的中英文界面文案.
 */

/** 诊断界面双语文案, 真实系统字段与版本不属于翻译内容. */
export const diagnosticMessages = {
    "zh-cn": {
        "diagnostic.heading": "运行环境",
        "diagnostic.version": "应用版本",
        "diagnostic.versionNote": "当前 pbi-utils 版本",
        "diagnostic.os": "操作系统",
        "diagnostic.osNote": "应用所在的系统",
        "diagnostic.architecture": "进程架构",
        "diagnostic.architectureNote": "当前应用的运行架构",
        "diagnostic.runtime": ".NET 运行时",
        "diagnostic.runtimeNote": "应用使用的运行时版本",
        "diagnostic.desktop": "桌面支持",
        "diagnostic.desktopNote": "Windows 桌面界面",
        "diagnostic.desktopAvailable": "Windows 桌面可用",
        "diagnostic.desktopUnavailable": "当前环境未提供桌面能力",
        "diagnostic.browserPreview": "浏览器预览",
        "diagnostic.browserSimulation": "浏览器模拟",
        "diagnostic.notDetected": "未检测",
        "diagnostic.webViewNote": "桌面界面使用的浏览器运行时",
        "diagnostic.discoveryAvailable": "本机实例只读发现可用",
        "diagnostic.discoveryUnavailable": "当前环境未提供实例发现",
        "diagnostic.simulation": "模拟数据",
        "diagnostic.powerBiNote": "实际打开的实例请在连接页查看",
        "diagnostic.completed": "检测完成",
        "diagnostic.loading": "正在读取运行环境…",
        "diagnostic.simulationNotice": "浏览器预览使用模拟信息，未检测本机运行环境。",
        "diagnostic.temporarilyUnavailable": "运行环境暂时无法读取",
        "diagnostic.failed": "运行环境检测失败",
    },
    "en-us": {
        "diagnostic.heading": "Runtime environment",
        "diagnostic.version": "App version",
        "diagnostic.versionNote": "Current pbi-utils version",
        "diagnostic.os": "Operating system",
        "diagnostic.osNote": "System hosting the application",
        "diagnostic.architecture": "Process architecture",
        "diagnostic.architectureNote": "Architecture of the running application",
        "diagnostic.runtime": ".NET runtime",
        "diagnostic.runtimeNote": "Runtime version used by the application",
        "diagnostic.desktop": "Desktop support",
        "diagnostic.desktopNote": "Windows desktop interface",
        "diagnostic.desktopAvailable": "Windows desktop available",
        "diagnostic.desktopUnavailable": "Desktop features unavailable in this environment",
        "diagnostic.browserPreview": "Browser preview",
        "diagnostic.browserSimulation": "Browser simulation",
        "diagnostic.notDetected": "Not detected",
        "diagnostic.webViewNote": "Browser runtime used by the desktop interface",
        "diagnostic.discoveryAvailable": "Read-only local instance discovery available",
        "diagnostic.discoveryUnavailable": "Instance discovery unavailable in this environment",
        "diagnostic.simulation": "Simulated data",
        "diagnostic.powerBiNote": "View open instances in Connections",
        "diagnostic.completed": "Detection complete",
        "diagnostic.loading": "Reading the runtime environment…",
        "diagnostic.simulationNotice": "Browser preview uses simulated information; the local runtime environment has not been checked.",
        "diagnostic.temporarilyUnavailable": "The runtime environment is temporarily unavailable",
        "diagnostic.failed": "Runtime environment detection failed",
    },
} as const
