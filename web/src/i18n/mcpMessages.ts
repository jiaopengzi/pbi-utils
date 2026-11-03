/**
 * FilePath    : pbi-utils\web\src\i18n\mcpMessages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地 MCP 配置、权限与数据边界的双语说明.
 */
export const mcpMessages = {
    "zh-cn": {
        "mcp.connectionsTitle": "已连接的 MCP",
        "mcp.connectionsRefresh": "刷新连接",
        "mcp.connectionsDescription":
            "仅管理当前用户、当前安装的 MCP 连接。名称和版本由客户端自报，未提供时显示 -。断开会等待在途操作排空；暂停接入会持久阻止新连接并排空全部连接，恢复接入后，本应用不会主动重连，AI 客户端可能自动重连。更新文件前仍需正常关闭桌面；桌面和后台自身仍会占用文件。",
        "mcp.connectionsWorking": "正在更新 MCP 连接管理状态…",
        "mcp.connectionsRefreshed": "连接状态已刷新。",
        "mcp.connectionsFailed": "无法读取 MCP 连接状态。",
        "mcp.disconnectAccepted": "已受理断开请求，正在等待操作排空及进程退出。",
        "mcp.connectionGone": "目标连接已离开或已过期，正在刷新列表。",
        "mcp.admissionUpdated": "接入状态已更新，连接退出进度以列表为准。",
        "mcp.pauseAdmission": "暂停接入并断开全部",
        "mcp.resumeAdmission": "恢复接入",
        "mcp.acceptingStatus": "允许接入 · {count} 个连接",
        "mcp.pausedStatus": "已暂停接入 · {count} 个连接",
        "mcp.connectionsStale": "连接状态尚未核验，管理操作已禁用，请刷新。",
        "mcp.connectionsEmpty": "当前没有 MCP 连接。",
        "mcp.disconnect": "断开",
        "mcp.connectionPid": "进程 PID",
        "mcp.processStarted": "进程启动时间",
        "mcp.connectedAt": "连接时间",
        "mcp.connectionPermission": "权限",
        "mcp.canWrite": "允许修改",
        "mcp.readOnly": "只读",
        "mcp.connectionState": "状态",
        "mcp.activeRequests": "在途请求",
        "mcp.state.connected": "已连接",
        "mcp.state.draining": "正在排空",
        "mcp.state.exiting": "等待进程退出",

        "mcp.title": "本地 MCP",
        "mcp.searchDescription": "让第三方 AI 使用 pbi-utils",
        "mcp.description":
            "让支持本地 MCP 的 AI 客户端使用 pbi-utils。复制下方提示词，交给 AI 完成配置并按统一表格反馈校验结果。服务已随本应用安装，支持连接和编辑本地模型、生成文件及修改设置；复制提示词不会自动更改客户端权限。",
        "mcp.desktop": "桌面应用与多个 MCP 客户端可以同时运行，各自的连接和权限相互独立。共享业务后台按需启动，写入冲突会明确反馈。",
        "mcp.boundary": "Power BI Service / SSAS 模型始终只读。",
        "mcp.privacy": "第三方 AI 可能把工具输入和返回的模型信息发送给其服务提供方，请先确认其隐私设置。",
        "mcp.simulation": "浏览器模拟路径，仅用于预览。请在桌面应用中复制真实接入提示词。",
        "mcp.loadFailed": "无法读取本地 MCP 配置。",
        "mcp.reload": "重新读取",
        "mcp.copyPrompt": "复制 AI 接入提示词",
        "mcp.setupPrompt": `请帮我在当前 AI 客户端配置 pbi-utils 本地 MCP。
1. 确认客户端支持本地 stdio MCP、下方 command 程序存在。按客户端格式解析配置，比对 command 和完整有序 args：匹配即复用，缺失才新增，差异仅作必要修正，保留其他服务。实际回读完整 command/args 的行内 JSON，match 填 true/false/unknown。
2. 当前客户端依次调用 server_info {} → desktop_sessions {} → desktop_app_info {"parameters":{},"sessionId":"本次 sessions[0].id"}。上下文须恰好一个且 id 非空，不猜 id；三项成功即可确认工具可用。
3. 仅做接入诊断，不连接模型或执行业务写入。连接不可用就停止，保留本次错误码并给当前客户端必要操作；未执行标未验证，不从历史日志或旧状态文件猜原因。
4. 最终仅用下表回复，保留 3 列、7 个数据行，填实际值或原因，未知写“-”。状态为通过/失败/未验证；前六项全部通过则结论通过，有失败则失败，其余待完成；实际写入固定未验证。

结论：<通过/待完成/失败>

| 检查项 | 状态 | 证据或原因 |
| --- | --- | --- |
| 配置 | <状态> | <客户端；配置位置；处理结果；程序存在性>；readback=\`<JSON>\`；match=<true/false/unknown> |
| 工具发现 | <状态> | <本次三项诊断是否成功> |
| server_info | <状态> | <version 非空；transport=stdio> |
| desktop_sessions | <状态> | <上下文数量；本次 sessionId> |
| desktop_app_info | <状态> | <返回的非空 version> |
| 写权限 | <状态> | <本次 server_info.allowWrite，须为 true> |
| 实际写入测试 | 未验证 | 仅验证接入，未执行写入。 |

下一步：<无需操作或当前客户端必要操作>
隐私：本地 MCP 无需 AI 密钥；第三方 AI 可能外传工具输入和结果，请核对客户端隐私设置。

启动配置：
{configuration}`,
    },
    "en-us": {
        "mcp.connectionsTitle": "Connected MCP clients",
        "mcp.connectionsRefresh": "Refresh connections",
        "mcp.connectionsDescription":
            "Manage only MCP connections for this user and installation. Client names and versions are self-reported; missing values display -. Disconnect waits for active operations to drain. Pausing admission persists, blocks new connections and drains all connections. This app does not initiate reconnection after resuming; AI clients may reconnect automatically. Close the desktop app normally before updating files; the desktop app and background service themselves also hold files open.",
        "mcp.connectionsWorking": "Updating MCP connection management…",
        "mcp.connectionsRefreshed": "Connection status refreshed.",
        "mcp.connectionsFailed": "Could not read MCP connection status.",
        "mcp.disconnectAccepted": "Disconnect request accepted. Waiting for operations to drain and the process to exit.",
        "mcp.connectionGone": "The connection has left or expired. Refreshing the list.",
        "mcp.admissionUpdated": "Admission updated. Check the list for process exit progress.",
        "mcp.pauseAdmission": "Pause admission and disconnect all",
        "mcp.resumeAdmission": "Resume admission",
        "mcp.acceptingStatus": "Accepting connections · {count} connected",
        "mcp.pausedStatus": "Admission paused · {count} connected",
        "mcp.connectionsStale": "Connection status is unverified. Management is disabled; refresh to continue.",
        "mcp.connectionsEmpty": "No MCP clients connected.",
        "mcp.disconnect": "Disconnect",
        "mcp.connectionPid": "Process PID",
        "mcp.processStarted": "Process started",
        "mcp.connectedAt": "Connected at",
        "mcp.connectionPermission": "Permission",
        "mcp.canWrite": "Allow changes",
        "mcp.readOnly": "Read-only",
        "mcp.connectionState": "State",
        "mcp.activeRequests": "Active requests",
        "mcp.state.connected": "Connected",
        "mcp.state.draining": "Draining",
        "mcp.state.exiting": "Waiting for process exit",

        "mcp.title": "Local MCP",
        "mcp.searchDescription": "Use pbi-utils with a third-party AI",
        "mcp.description":
            "Use pbi-utils from an AI client that supports local MCP. Copy the prompt below to ask AI to configure it and report verification in a consistent table. The server is bundled with this app and supports connecting and editing local models, generating files and changing settings. Copying the prompt does not change client permissions.",
        "mcp.desktop":
            "The desktop app and multiple MCP clients can run together; their connections and permissions are independent. The shared background service starts on demand. Write conflicts are reported explicitly.",
        "mcp.boundary": "Power BI Service / SSAS models always remain read-only.",
        "mcp.privacy": "Third-party AI may send tool inputs and returned model information to its provider. Check the client's privacy settings first.",
        "mcp.simulation": "Simulated browser path for preview only. Copy the actual setup prompt from the desktop app.",
        "mcp.loadFailed": "Could not read the local MCP configuration.",
        "mcp.reload": "Reload",
        "mcp.copyPrompt": "Copy AI setup prompt",
        "mcp.setupPrompt": `Configure pbi-utils as a local MCP server in this AI client.
1. Confirm local stdio MCP support and that the command executable below exists. Parse the client's configuration and compare command and the complete ordered args: reuse a match, add only if missing, otherwise make only necessary corrections. Preserve other services. Show the complete command/args actually read back as inline JSON with match=true/false/unknown.
2. Through this client, call server_info {} → desktop_sessions {} → desktop_app_info {"parameters":{},"sessionId":"sessions[0].id from this run"}. Require exactly one context with a nonempty id; never guess it. Three successful diagnostics confirm the tools are available.
3. Perform setup diagnostics only; do not connect to or modify models or perform business writes. If the connection is unavailable, stop, preserve this attempt's error code and give the necessary action for this client. Mark unexecuted checks UNRUN; do not infer causes from historical logs or old state files.
4. Reply briefly using only the template below, keeping 3 columns and 7 data rows. Fill in actual values or reasons; unknown values use "-". Status is PASS/FAIL/UNRUN. Result is PASS if the first six checks pass, FAIL if any fails, otherwise PENDING. The write test stays UNRUN.

Result: <PASS/PENDING/FAIL>

| Check | Status | Evidence or reason |
| --- | --- | --- |
| Configuration | <status> | <Client; config location; action; executable exists>; readback=\`<JSON>\`; match=<true/false/unknown> |
| Tool discovery | <status> | <Whether all three diagnostics succeeded this run> |
| server_info | <status> | <Nonempty version; transport=stdio> |
| desktop_sessions | <status> | <Context count; this run's sessionId> |
| desktop_app_info | <status> | <Returned nonempty version> |
| Write permission | <status> | <This run's server_info.allowWrite, must be true> |
| Actual write test | UNRUN | Setup verification only; no writes performed. |

Next step: <No action needed or necessary action for this client>
Privacy: Local MCP needs no AI keys; third-party AI may send tool inputs and results to its provider. Check the client's privacy settings.

Launch configuration:
{configuration}`,
    },
}
