// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\BusinessContextTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实 Host 业务组合与隔离本地管道核验模型上下文能力和跨入口权限隔离.

using PbiUtils.Infrastructure.Logging;
using System.Diagnostics;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Host;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>所有状态和日志都在测试临时根, 只调用本地元数据, 不启动应用、登录或连接模型.</summary>
public sealed class BusinessContextTests : IAsyncLifetime
{
    private static readonly string[] RemoteMetadataFields = ["error", "formId", "recent"];
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-host-context-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<BusinessHostClient> clients = [];
    private BusinessHostSettings settings = null!;
    private BusinessHostServer server = null!;
    private LoggingRuntime logging = null!;

    /// <summary>产品 composition 原样运行, 只替换当前测试进程的认证安装路径和用户根.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule!.FileName;
        settings = new(root, Path.GetDirectoryName(executable)!, Path.Combine(root, "runtime"))
        { HostExecutable = executable, GuiExecutable = executable, CliExecutable = executable, BootstrapTimeout = TimeSpan.FromSeconds(5) };
        logging = new LoggingRuntime(root, "test");
        server = await BusinessHostServer.StartAsync(settings, (registration, peer, _, mcpConnections) => BusinessContext.CreateAsync(registration, peer, root, logging, mcpConnections));
    }

    /// <summary>MCP 元数据能力真实可用, 只返回非秘密字段且各连接保留自己的表单身份.</summary>
    [Fact]
    public async Task McpRemoteMetadataRemainsAvailableAndContextScoped()
    {
        var first = await ConnectAsync("mcp", false);
        var second = await ConnectAsync("mcp", false);
        var catalog = await first.Peer.InvokeAsync("mcp.tools", Json(new { }));
        Assert.Contains(catalog.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "desktop_remote_form");
        var one = await CallAsync(first, "desktop_remote_form", new { });
        var two = await CallAsync(second, "desktop_remote_form", new { });
        Assert.False(one.GetProperty("isError").GetBoolean(), one.GetRawText());
        Assert.False(two.GetProperty("isError").GetBoolean(), two.GetRawText());
        var firstForm = one.GetProperty("structuredContent");
        var secondForm = two.GetProperty("structuredContent");
        Assert.Equal(RemoteMetadataFields, firstForm.EnumerateObject().Select(field => field.Name).OrderBy(name => name));
        Assert.Empty(firstForm.GetProperty("recent").EnumerateArray());
        Assert.NotEqual(firstForm.GetProperty("formId").GetString(), secondForm.GetProperty("formId").GetString());
        var forbidden = await CallAsync(first, "desktop_remote_mutate", new { formId = firstForm.GetProperty("formId").GetString(), action = "delete", recordId = "absent" });
        Assert.True(forbidden.GetProperty("isError").GetBoolean());
        Assert.Equal("MCP_WRITE_DISABLED", forbidden.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>GUI 业务确实在 Host 执行并回传既有信封, 客户端角色不能调用另一入口.</summary>
    [Fact]
    public async Task GuiRunsRemoteBusinessAndRolesCannotCrossInvoke()
    {
        var received = new TaskCompletionSource<GuiHostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gui = await BusinessHostClient.ConnectAsync(settings, new("gui", true, "test"), null, (name, value, _) =>
        {
            if (name == "bridge.message") received.TrySetResult(value.Deserialize<GuiHostMessage>(GuiHostJson.Options)!);
            return Task.CompletedTask;
        }, default);
        clients.Add(gui);
        var completion = await gui.Peer.InvokeAsync("gui.invoke", Json(new GuiHostRequest(0,
            Json(new { protocolVersion = 1, id = "metadata", method = "remote.form", @params = new { } }))));
        Assert.False(completion.GetProperty("resetScheduled").GetBoolean());
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("metadata", message.Message.GetProperty("id").GetString());
        Assert.Equal(64, message.Message.GetProperty("result").GetProperty("formId").GetString()!.Length);
        Assert.Equal("HOST_METHOD_NOT_ALLOWED", (await Assert.ThrowsAsync<AppException>(() => gui.Peer.InvokeAsync("mcp.tools", Json(new { })))).Code);
        var mcp = await ConnectAsync("mcp", false);
        Assert.Equal("HOST_METHOD_NOT_ALLOWED", (await Assert.ThrowsAsync<AppException>(() => mcp.Peer.InvokeAsync("gui.prepareClose", Json(new { })))).Code);
    }

    /// <summary>连接当前隔离后台, 不触发产品可执行文件启动.</summary>
    private async Task<BusinessHostClient> ConnectAsync(string kind, bool allowWrite)
    {
        var client = await BusinessHostClient.ConnectAsync(settings, new(kind, allowWrite), null, null, default);
        clients.Add(client);
        return client;
    }

    /// <summary>使用真实 MCP SDK 请求 DTO 形状, 不直接调用 Bridge 绕过协议和运行时.</summary>
    private static Task<JsonElement> CallAsync<T>(BusinessHostClient client, string name, T parameters) =>
        client.Peer.InvokeAsync("mcp.call", Json(new { name, arguments = new { parameters } }));
    private static JsonElement Json<T>(T value) => GuiHostJson.Element(value);

    /// <summary>排空真实业务组合并释放日志后, 只删除本测试生成的独占临时根.</summary>
    public async Task DisposeAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
        if (server is not null) await server.DisposeAsync();
        logging?.Dispose();
        Directory.Delete(root, true);
    }
}
