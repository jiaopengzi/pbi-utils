// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\IBusinessHostMcpConnections.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅认证桌面可使用的 MCP 连接查看、排空和持久准入控制契约.

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>绑定已认证 GUI 上下文的管理能力, 不向 MCP 或普通 CLI 工厂提供.</summary>
public interface IBusinessHostMcpConnections
{
    /// <summary>返回当前安装已认证 MCP 的安全身份和实际生命周期, 已失效 GUI 明确失败.</summary>
    Task<McpConnectionsSnapshot> ListAsync(CancellationToken token = default);
    /// <summary>登记指定 N32 连接的排空意图并立即返回, 不强杀进程或等待提交.</summary>
    Task<McpDisconnectResult> DisconnectAsync(string connectionId, CancellationToken token = default);
    /// <summary>原子持久化准入; 暂停时同时登记所有 MCP 排空, 存储失败不伪报成功.</summary>
    Task<McpConnectionsSnapshot> SetAdmissionAsync(bool acceptingConnections, CancellationToken token = default);
}

/// <summary>当前安装 MCP 准入与连接快照; 无模型、账号、路径或进程 ticks.</summary>
public sealed record McpConnectionsSnapshot(bool AcceptingConnections, IReadOnlyList<McpConnectionInfo> Connections);

/// <summary>Host 签发身份和 OS 身份; 名称版本仅是 SDK 客户端的不可信展示信息.</summary>
public sealed record McpConnectionInfo(string Id, int ProcessId, DateTimeOffset ProcessStartedAt,
    DateTimeOffset ConnectedAt, string? ClientName, string? ClientVersion, bool AllowWrite, string State, int ActiveRequests);

/// <summary>仅代表排空意图已接受, 实际退出继续由列表状态核验.</summary>
public sealed record McpDisconnectResult(bool Accepted);
