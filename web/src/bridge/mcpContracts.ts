/**
 * FilePath    : pbi-utils\web\src\bridge\mcpContracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 定义宿主核实的本地 MCP 启动命令, 页面仅展示与复制.
 */

/** 客户端通过标准输入输出启动的本地命令, 参数由宿主固定生成. */
export interface McpLaunchConfiguration {
    command: string
    args: string[]
}

/** 当前安装位置的允许修改启动配置; 模拟配置不能用于判断真实安装状态. */
export interface McpConfiguration {
    executablePath: string
    readWrite: McpLaunchConfiguration
    isSimulation: boolean
}

/** 同用户同安装副本的 MCP 连接展示事实; 客户端名称仅为自报展示数据. */
export interface McpConnectionInfo {
    id: string
    processId: number
    processStartedAt: string
    connectedAt: string
    clientName: string | null
    clientVersion: string | null
    allowWrite: boolean
    state: "connected" | "draining" | "exiting"
    activeRequests: number
}

/** 宿主持久接入状态及尚未确认退出的连接, 不包含模型内容. */
export interface McpConnectionsSnapshot {
    acceptingConnections: boolean
    connections: McpConnectionInfo[]
}

/** accepted 仅代表断开意图已受理, 不代表进程已经退出. */
export interface McpDisconnectResult {
    accepted: boolean
}
