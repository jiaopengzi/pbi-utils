// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享业务后台的固定连接能力、上下文和双向调用契约.

using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>正式客户端从启动参数构造的不可变能力, 不接受业务参数重新授权.</summary>
public sealed record BusinessHostClientOptions(string Kind, bool AllowWrite, string? WebViewVersion = null, ExternalToolLaunchTarget? ExternalToolTarget = null);

/// <summary>后台签发并绑定实际 OS 客户端身份的独立上下文.</summary>
public sealed record BusinessHostRegistration(string ContextId, string HostGeneration,
    BusinessHostClientOptions Options, int ProcessId, long ProcessStartTicks);

/// <summary>处理已认证连接的固定方法, 实现方继续验证业务白名单、权限和输入预算.</summary>
public delegate Task<JsonElement> BusinessHostRequestHandler(string method, JsonElement parameters, CancellationToken token);

/// <summary>接收当前上下文的定向事件, 不从事件内容获得执行权限.</summary>
public delegate Task BusinessHostEventHandler(string name, JsonElement parameters, CancellationToken token);

/// <summary>为每条认证连接创建独立业务 owner, 工厂返回前不得调用对端.</summary>
public delegate Task<IBusinessHostContext> BusinessHostContextFactory(
    BusinessHostRegistration registration, BusinessHostPeer peer, CancellationToken token, IBusinessHostMcpConnections? mcpConnections);

/// <summary>单客户端业务生命周期; Dispose 必须取消准备并排空已提交请求后释放资源.</summary>
public interface IBusinessHostContext : IAsyncDisposable
{
    /// <summary>执行固定业务方法并返回真实值; token 只请求取消, 实际提交边界由业务拥有.</summary>
    Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token);
}
