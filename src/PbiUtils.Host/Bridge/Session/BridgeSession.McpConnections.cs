// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.McpConnections.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将桌面明确的连接管理意图送入 Host 权威 owner, 不暴露任意进程控制或 MCP 管理工具.

using System.Text.Json;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>严格解析 GUI 管理请求, 先核对取消再受理; 受理后关闭/暂停继续完成, 不将排空误报为进程退出.</summary>
    /// <param name="method">固定三个桌面入口, 不接收动态控制方法.</param>
    /// <param name="parameters">只允许空查询、准确连接身份或布尔准入状态.</param>
    /// <param name="flight">当前 GUI 请求的取消与受理边界, 不接管目标业务事务.</param>
    /// <returns>Host 实际快照或关闭受理结果; 不支持、输入和存储失败保留错误.</returns>
    private async Task<object?> DispatchMcpConnectionsAsync(string method, JsonElement parameters, Flight flight)
    {
        switch (method)
        {
            case "mcp.connections":
                RequireEmpty(parameters);
                if (mcpConnections is null) throw UnavailableMcpManagement();
                return await mcpConnections.ListAsync(flight.Cancellation.Token);
            case "mcp.disconnect":
                var connectionId = Strings(parameters, ["connectionId"], 32)["connectionId"];
                if (connectionId.Length != 32 || connectionId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new AppException("VALIDATION_ERROR", "MCP 连接身份无效");
                if (mcpConnections is null) throw UnavailableMcpManagement();
                BeginCommit(flight);
                return await mcpConnections.DisconnectAsync(connectionId, CancellationToken.None);
            case "mcp.setAdmission":
                if (parameters.EnumerateObject().Count() != 1
                    || !parameters.TryGetProperty("acceptingConnections", out var admission)
                    || admission.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new AppException("VALIDATION_ERROR", "MCP 接入设置必须为布尔值");
                if (mcpConnections is null) throw UnavailableMcpManagement();
                BeginCommit(flight);
                return await mcpConnections.SetAdmissionAsync(admission.GetBoolean(), CancellationToken.None);
            default:
                throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
    }

    /// <summary>独立测试组合或非 GUI owner 不具有跨连接管理权, 不伪造空快照或成功.</summary>
    private static AppException UnavailableMcpManagement() => new("UNSUPPORTED", "当前宿主不支持 MCP 连接管理", 3);
}
