// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpConnectionManagementBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证桌面 MCP 连接管理的严格输入、受限组合与外部工具隔离.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class McpConnectionManagementBridgeTests
{
    /// <summary>没有已认证 GUI 管理能力时必须明确不可用, 不返回空列表或虚假断开成功.</summary>
    [Theory]
    [InlineData("mcp.connections", "{}")]
    [InlineData("mcp.disconnect", "{\"connectionId\":\"11111111111111111111111111111111\"}")]
    [InlineData("mcp.setAdmission", "{\"acceptingConnections\":false}")]
    public async Task MissingGuiControllerIsUnsupported(string method, string parameters)
    {
        var response = await InvokeAsync(method, parameters);
        Assert.Equal("UNSUPPORTED", response.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>管理输入只允许精确身份和布尔准入值, 不接收进程号、角色、路径或额外字段.</summary>
    [Theory]
    [InlineData("mcp.connections", "{\"kind\":\"gui\"}")]
    [InlineData("mcp.disconnect", "{}")]
    [InlineData("mcp.disconnect", "{\"connectionId\":\"\"}")]
    [InlineData("mcp.disconnect", "{\"connectionId\":\"11111111111111111111111111111111\",\"processId\":123}")]
    [InlineData("mcp.setAdmission", "{}")]
    [InlineData("mcp.setAdmission", "{\"acceptingConnections\":\"false\"}")]
    [InlineData("mcp.setAdmission", "{\"acceptingConnections\":false,\"force\":true}")]
    public async Task InvalidManagementParametersAreRejected(string method, string parameters)
    {
        var response = await InvokeAsync(method, parameters);
        Assert.Equal("VALIDATION_ERROR", response.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>即使可写 MCP 也不能通过业务目录管理其他连接.</summary>
    [Theory]
    [InlineData("mcp.connections")]
    [InlineData("mcp.disconnect")]
    [InlineData("mcp.setAdmission")]
    public void ManagementIsNotAnMcpTool(string method)
    {
        Assert.False(McpDesktopCatalog.TryGet(method, out _));
    }

    /// <summary>GUI 查询和管理透传权威 owner 的真实结果, 不将受理标志改写为已退出.</summary>
    [Fact]
    public async Task GuiDelegatesExactIdentityAndAdmissionToOwner()
    {
        var controller = new Management();
        var list = await InvokeAsync("mcp.connections", "{}", controller);
        Assert.True(list.GetProperty("result").GetProperty("acceptingConnections").GetBoolean());
        Assert.Equal(1, controller.Reads);
        var disconnect = await InvokeAsync("mcp.disconnect", "{\"connectionId\":\"11111111111111111111111111111111\"}", controller);
        Assert.False(disconnect.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("11111111111111111111111111111111", controller.DisconnectedId);
        var pause = await InvokeAsync("mcp.setAdmission", "{\"acceptingConnections\":false}", controller);
        Assert.False(pause.GetProperty("result").GetProperty("acceptingConnections").GetBoolean());
        var resume = await InvokeAsync("mcp.setAdmission", "{\"acceptingConnections\":true}", controller);
        Assert.True(resume.GetProperty("result").GetProperty("acceptingConnections").GetBoolean());
        Assert.Equal(2, controller.AdmissionChanges);
    }

    /// <summary>管理被受理后网页取消不得撤销关闭或宣称取消成功; 唯一实际结果仍送达.</summary>
    [Fact]
    public async Task CancellationAfterManagementAcceptedWaitsForActualResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new Management
        {
            BeforeAdmission = async token =>
            {
                Assert.False(token.CanBeCanceled);
                entered.SetResult();
                await release.Task;
            }
        };
        ConcurrentQueue<JsonElement> messages = new();
        using var session = Session(messages, controller);
        var pending = session.ReceiveAsync(OriginPolicy.Production,
            Request("management", "mcp.setAdmission", JsonSerializer.SerializeToElement(new { acceptingConnections = false })));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await session.ReceiveAsync(OriginPolicy.Production,
                Request("cancel", "request.cancel", JsonSerializer.SerializeToElement(new { requestId = "management" })));
            var cancellation = Assert.Single(messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "cancel");
            Assert.False(cancellation.GetProperty("result").GetProperty("accepted").GetBoolean());
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); await pending; }
        var result = Assert.Single(messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "management");
        Assert.False(result.GetProperty("result").GetProperty("acceptingConnections").GetBoolean());
    }

    /// <summary>无可信页面来源或请求在受理前取消均不能调用管理 owner.</summary>
    [Fact]
    public async Task UntrustedOriginAndPreAcceptanceCancellationCannotManage()
    {
        var controller = new Management();
        ConcurrentQueue<JsonElement> messages = new();
        using var session = Session(messages, controller);
        var request = Request("management", "mcp.setAdmission", JsonSerializer.SerializeToElement(new { acceptingConnections = false }));
        await session.ReceiveAsync("https://untrusted.invalid", request);
        Assert.Empty(messages);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await session.ReceiveAsync(OriginPolicy.Production, request, cancellation.Token);
        Assert.Equal("CANCELLED", Assert.Single(messages).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, controller.AdmissionChanges);
    }

    /// <summary>通过实际生产来源和完整桥接信封调用, 返回唯一终态供断言.</summary>
    private static async Task<JsonElement> InvokeAsync(string method, string parameters, IBusinessHostMcpConnections? controller = null)
    {
        ConcurrentQueue<JsonElement> messages = new();
        using var session = Session(messages, controller);
        using var values = JsonDocument.Parse(parameters);
        await session.ReceiveAsync(OriginPolicy.Production, Request("mcp-management", method, values.RootElement));
        return Assert.Single(messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "mcp-management");
    }

    /// <summary>只替换管理 owner, 保持真实来源、信封、取消与终态处理.</summary>
    private static BridgeSession Session(ConcurrentQueue<JsonElement> messages, IBusinessHostMcpConnections? controller) =>
        new(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", mcpConnections: controller);

    /// <summary>构造唯一请求身份的生产桥接信封.</summary>
    private static string Request(string id, string method, JsonElement parameters) =>
        JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters });

    /// <summary>可控 owner 用于验证参数与取消边界, 不连接或关闭任何真实进程.</summary>
    private sealed class Management : IBusinessHostMcpConnections
    {
        internal int Reads { get; private set; }
        internal int AdmissionChanges { get; private set; }
        internal string? DisconnectedId { get; private set; }
        internal Func<CancellationToken, Task>? BeforeAdmission { get; init; }

        /// <summary>返回独立空列表, 计数真实查询并保持令牌.</summary>
        public Task<McpConnectionsSnapshot> ListAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(new McpConnectionsSnapshot(true, []));
        }

        /// <summary>记录准确目标并模拟目标已离开, 不伪造关闭成功.</summary>
        public Task<McpDisconnectResult> DisconnectAsync(string connectionId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            DisconnectedId = connectionId;
            return Task.FromResult(new McpDisconnectResult(false));
        }

        /// <summary>可阻塞受理之后的状态保存, 返回实际准入状态用于取消竞争断言.</summary>
        public async Task<McpConnectionsSnapshot> SetAdmissionAsync(bool acceptingConnections, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (BeforeAdmission is not null) await BeforeAdmission(token);
            AdmissionChanges++;
            return new(acceptingConnections, []);
        }
    }
}
