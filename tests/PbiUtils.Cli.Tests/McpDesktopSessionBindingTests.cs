// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpDesktopSessionBindingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享后台内每个 MCP 的独立上下文、会话拒绝、权限与取消隔离.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

public sealed class McpDesktopSessionBindingTests
{
    /// <summary>同时存活的两个 MCP 只发现自己的上下文, 观察代际不在客户端间共享.</summary>
    [Fact]
    public async Task ClientsDiscoverOnlyTheirOwnContextAndKeepIndependentObservations()
    {
        var firstContext = new Context("first", 7);
        var secondContext = new Context("second", 19);
        await using var first = Runtime(false, firstContext);
        await using var second = Runtime(false, secondContext);
        foreach (var (runtime, id) in new[] { (first, "first"), (second, "second") })
        {
            var discovery = await Call(runtime, "desktop_sessions", new { });
            Assert.False(discovery.IsError);
            var session = Assert.Single(discovery.StructuredContent!.Value.GetProperty("sessions").EnumerateArray());
            Assert.Equal(id, session.GetProperty("id").GetString());
        }
        Assert.False((await Call(first, "desktop_app_info", new { parameters = new { } })).IsError);
        Assert.False((await Call(second, "desktop_app_info", new { parameters = new { } })).IsError);
        Assert.False((await Call(first, "desktop_app_info", new { parameters = new { } })).IsError);
        Assert.Equal(new long?[] { null, 7 }, firstContext.Observations);
        Assert.Equal(new long?[] { null }, secondContext.Observations);
    }

    /// <summary>外部 sessionId 不能将本客户端请求重定向到其他 MCP 或 GUI 的上下文.</summary>
    [Theory]
    [InlineData("other-mcp")]
    [InlineData("gui-context")]
    [InlineData("expired-context")]
    public async Task ForeignOrExpiredContextIsRejectedBeforeDispatch(string sessionId)
    {
        var context = new Context("own", 1);
        await using var runtime = Runtime(false, context);
        var result = await Call(runtime, "desktop_app_info", new { sessionId, parameters = new { } });
        Assert.True(result.IsError);
        Assert.Equal("MCP_DESKTOP_SESSION_OWNED", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(context.Observations);
    }

    /// <summary>一个 MCP 的启动写许可不改变其他 MCP 的只读许可, 同时存活的调用分别进入自己的上下文.</summary>
    [Fact]
    public async Task WritePermissionDoesNotFlowBetweenClients()
    {
        var readContext = new Context("read", 1);
        var writeContext = new Context("write", 1);
        await using var reader = Runtime(false, readContext);
        await using var writer = Runtime(true, writeContext);
        var allowed = await Call(writer, "desktop_project_connectLocal", new { parameters = new { instanceId = "fixture" } });
        Assert.False(allowed.IsError);
        var denied = await Call(reader, "desktop_project_connectLocal", new { parameters = new { instanceId = "fixture" } });
        Assert.True(denied.IsError);
        Assert.Equal("MCP_WRITE_DISABLED", denied.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(readContext.Observations);
        Assert.Single(writeContext.Observations);
    }

    /// <summary>预先取消不触及上下文, 未注入上下文明确失败而不枚举/采用前台窗口.</summary>
    [Fact]
    public async Task CancellationAndMissingContextCannotAdoptAnotherSession()
    {
        var context = new Context("own", 1);
        await using var runtime = Runtime(false, context);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(runtime, "desktop_app_info", new { parameters = new { } }, cancelled.Token));
        Assert.Empty(context.Observations);
        await using var missing = Runtime(false, null);
        var result = await Call(missing, "desktop_sessions", new { });
        Assert.True(result.IsError);
        Assert.Equal("MCP_DESKTOP_REQUIRED", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>创建无真实连接的运行时; 参数固定权限与上下文, 不创建用户数据或访问模型.</summary>
    private static McpBusinessRuntime Runtime(bool allowWrite, IMcpDesktopContext? context) =>
        new(allowWrite, Path.GetTempPath(), NullLoggerFactory.Instance, desktopContext: context);

    /// <summary>将测试对象作为真实 MCP 工具参数调用, 保留服务端输入、权限和结果分类路径.</summary>
    private static Task<CallToolResult> Call(McpBusinessRuntime runtime, string name, object input, CancellationToken token = default)
    {
        var value = JsonSerializer.SerializeToElement(input);
        return runtime.CallAsync(new() { Name = name, Arguments = value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()) }, token).AsTask();
    }

    /// <summary>只替代真实模型适配, 记录进入独立上下文的观察代际; 不模拟权限检查或目标重定向.</summary>
    private sealed class Context(string sessionId, long revision) : IMcpDesktopContext
    {
        public string SessionId => sessionId;
        internal List<long?> Observations { get; } = [];

        /// <summary>记录原请求代际并返回此上下文的可识别结果; 已取消调用直接失败.</summary>
        public Task<McpDesktopResult> InvokeAsync(string method, JsonElement parameters, long? connectionRevision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observations.Add(connectionRevision);
            return Task.FromResult(new McpDesktopResult(new { context = sessionId }, false, null, false, revision));
        }
    }
}
