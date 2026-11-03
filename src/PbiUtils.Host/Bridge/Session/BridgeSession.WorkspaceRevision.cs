// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.WorkspaceRevision.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定网页已采用的连接代际, 外部切换或模型修改后拒绝旧草稿写入并广播权威快照.

using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>当前目标的读取、修改和切换绑定代际; 其他领域仍执行各自独立目标与修订校验.</summary>
    /// <param name="method">固定业务方法.</param>
    /// <param name="parameters">已解析参数, created 配置的读取不依赖当前连接.</param>
    /// <returns>请求是否依赖调用方当前采用的工作区.</returns>
    private static bool UsesConnection(string method, JsonElement parameters) => method.StartsWith("model.", StringComparison.Ordinal)
        || method is "project.inspect" or "project.openPath" or "project.open" or "project.openRecent" or "project.connectLocal" or "project.connectRemote"
            or "project.connectExternalTool" or "project.connectService" or "project.disconnect" or "powerbi.serviceSignOut" or "scaffold.openDesktop"
            or "configuration.apply" or "configuration.complete" or "configuration.discard"
        || method == "configuration.context" && parameters.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String && target.GetString() == "current";

    /// <summary>MCP 按宿主真实目标判断连接依赖; 独立 created 配置及不影响 Service 模型的账户登出不观察模型代际.</summary>
    /// <param name="method">已核验的固定 MCP 业务方法.</param>
    /// <param name="parameters">配置目标句柄继续由宿主精确核验.</param>
    /// <returns>是否必须绑定调用方观察过的当前模型; 调用方须持有会话锁读取 connectionKind.</returns>
    private bool UsesMcpConnection(string method, JsonElement parameters)
        => UsesConnection(method, parameters) && !UsesCreatedMcpConfiguration(method, parameters)
            && (method != "powerbi.serviceSignOut" || connectionKind == "powerbi-service");

    /// <summary>仅明确读取当前目标或选择连接的 MCP 请求可重新观察宿主代际, 不让其他工具隐式采用新目标.</summary>
    /// <param name="method">固定 MCP 业务方法.</param>
    /// <param name="parameters">配置只允许 current 目标建立当前连接观察.</param>
    /// <returns>是否允许在本次接收时采用宿主当前代际.</returns>
    private static bool CanObserveMcpConnection(string method, JsonElement parameters)
        => method is "project.inspect" or "project.openPath" or "project.open" or "project.openRecent" or "project.connectLocal"
            or "project.connectRemote" or "project.connectService" or "scaffold.openDesktop"
        || method == "configuration.context" && parameters.TryGetProperty("target", out var target)
            && target.ValueKind == JsonValueKind.String && target.GetString() == "current";

    /// <summary>仅宿主已签发且仍有效的 created 配置句柄可脱离当前连接观察, 原配置服务继续复核目标与基线.</summary>
    /// <param name="method">配置写入或恢复方法, 不扩大其他 MCP 能力的豁免范围.</param>
    /// <param name="parameters">含宿主精确 targetId 的业务参数, 不按客户端命名或前缀推定身份.</param>
    /// <returns>是否由独立 created 目标身份约束本次请求.</returns>
    private bool UsesCreatedMcpConfiguration(string method, JsonElement parameters)
        => method is "configuration.apply" or "configuration.complete" or "configuration.discard"
            && parameters.TryGetProperty("targetId", out var targetId) && targetId.ValueKind == JsonValueKind.String
            && configuration?.IsCreatedTargetId(targetId.GetString()!) == true;

    /// <summary>入站与真正提交前在同一会话锁内核对, 不允许旧草稿借用刚切换的新目标.</summary>
    /// <param name="flight">接收时冻结的调用方代际, 明确读取或选择目标的 MCP 请求可采用宿主当前代际.</param>
    /// <exception cref="AppException">目标已变化时必须由用户明确采用新快照后重新发起.</exception>
    private void RequireConnectionRevision(Flight flight)
    {
        if (flight.BindsConnection && flight.ConnectionRevision != connectionRevision)
            throw new AppException("STALE_CONNECTION", "连接已改变, 请确认当前连接后重试", 3);
    }

    /// <summary>实际发布后递增代际并广播, UI 自发请求可采用; 外部变更保留旧草稿直到明确同步.</summary>
    /// <param name="snapshot">权威当前快照; 断开为 null.</param>
    /// <param name="flight">实际完成该变更的调用, 用其身份区分 UI 与外部操作.</param>
    private void PublishWorkspaceChanged(ProjectSnapshot? snapshot, Flight flight)
    {
        lock (sync)
        {
            connectionRevision++;
            flight.ConnectionRevision = connectionRevision;
            Send(new { protocolVersion = 1, workspaceChanged = new { revision = connectionRevision, project = snapshot, requestId = flight.Id } });
        }
    }

    /// <summary>MCP 修改当前模型后也撤销旧网页草稿代际, 不把离线 created 目标误报为当前连接.</summary>
    /// <param name="result">共享业务实际返回值.</param>
    /// <param name="flight">持有同一提交与回执的工具请求.</param>
    private void PublishMcpModelChange(object? result, Flight flight)
    {
        if (flight.Method is not ("model.edit" or "model.save" or "model.discard" or "configuration.apply" or "configuration.complete" or "configuration.discard")) return;
        var snapshot = result switch { ProjectSnapshot project => project, ModelConfigurationContext context => context.Project, _ => null };
        lock (sync)
        {
            if (snapshot is null || snapshot.ProjectPath != projectPath || snapshot.ProjectId != workspaceId) return;
            PublishWorkspaceChanged(snapshot, flight);
        }
    }
}
