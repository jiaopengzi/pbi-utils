// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\IMcpDesktopContext.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将 MCP 业务运行时绑定到共享宿主内本客户端独立的模型上下文.

using System.Text.Json;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>宿主创建的上下文能力, 不接受客户端构造的会话或权限对象.</summary>
public interface IMcpDesktopContext
{
    /// <summary>当前连接唯一且不复用的上下文身份, 不能用于访问其他客户端.</summary>
    string SessionId { get; }

    /// <summary>调用受限业务白名单, 保留连接代际、取消边界和真实结果分类.</summary>
    /// <param name="method">已由 MCP 工具目录核验的方法.</param>
    /// <param name="parameters">冻结的业务参数, 不包含宿主权限.</param>
    /// <param name="connectionRevision">本客户端此前实际观察的连接代际.</param>
    /// <param name="token">提交前取消, 提交后由业务上下文排空.</param>
    /// <returns>真实业务结果与回读代际; 身份或业务失败必须明确拒绝.</returns>
    Task<McpDesktopResult> InvokeAsync(string method, JsonElement parameters, long? connectionRevision, CancellationToken token);
}
