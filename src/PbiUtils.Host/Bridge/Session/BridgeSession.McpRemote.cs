// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.McpRemote.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : MCP 以严格无秘密参数和当前 Windows 身份直连 SSAS, 复用模型门闩、取消与权威发布.

using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>仅解析服务器和数据库, 拒绝秘密字段、连接串及需要交互组织认证的 XMLA 地址.</summary>
    /// <param name="parameters">固定 MCP 远端连接参数, 与可信 GUI 凭据表单完全独立.</param>
    /// <returns>不含显式凭据和持久化意图的当前 Windows 身份地址.</returns>
    /// <exception cref="AppException">字段、地址或认证方式不满足静默 SSAS 连接约束.</exception>
    private static RemoteModelAddress ReadMcpRemoteAddress(JsonElement parameters)
    {
        var fields = Strings(parameters, ["server", "database"], 1024);
        var address = new RemoteModelAddress(fields["server"], fields["database"]);
        RemoteModelAddressPolicy.Validate(address);
        if (address.Server.Contains("://", StringComparison.Ordinal))
            throw new AppException("INVALID_AUTHENTICATION_MODE", "MCP 远端连接仅支持当前 Windows 身份的 SSAS, Service 请使用已登录的服务模型", 3);
        return address;
    }

    /// <summary>在原宿主门闩内读取并发布真实只读快照, 失败撤销注册并保留旧目标, 不保存连接或密码.</summary>
    /// <param name="parameters">已经在回执创建前验证的精确目标参数.</param>
    /// <param name="flight">拥有原请求取消、连接代际及可靠回执的执行状态.</param>
    /// <returns>通过共享连接服务权威读取且已发布的只读模型.</returns>
    /// <exception cref="AppException">没有连接服务、未保存修改、忙碌、目标过期或读取失败.</exception>
    private async Task<ProjectSnapshot> ConnectMcpRemoteAsync(JsonElement parameters, Flight flight)
    {
        if (connections is null) throw new AppException("UNSUPPORTED", "当前宿主不支持远端连接", 3);
        RequireSavedModel();
        using var address = ReadMcpRemoteAddress(parameters);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "工程操作正在执行", 3);
        try
        {
            var snapshot = await connections.RemoteAsync(address, flight.Cancellation.Token);
            try { PublishProject(snapshot, flight); return snapshot; }
            catch { connections.Forget(snapshot.ProjectPath); throw; }
        }
        finally { projectGate.Release(); }
    }
}
