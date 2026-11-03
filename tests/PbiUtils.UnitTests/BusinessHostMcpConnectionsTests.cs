// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessHostMcpConnectionsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用隔离真实管道检验 GUI 专属 MCP 管理、持久准入、元数据和排空竞态.

using System.Diagnostics;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有进程身份为当前测试进程, 不启动用户应用或访问模型; 进程退出由 CLI 集成测试另行验证.</summary>
public sealed class BusinessHostMcpConnectionsTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-management-" + Guid.NewGuid().ToString("N"));
    private readonly List<BusinessHostClient> clients = [];
    private BusinessHostSettings settings = null!;
    private BusinessHostServer? server;
    private IBusinessHostMcpConnections manager = null!;

    /// <summary>安装和运行状态均隔离, OS 身份校验仍使用实际测试进程句柄.</summary>
    public Task InitializeAsync()
    {
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule!.FileName;
        settings = new(root, Path.GetDirectoryName(executable)!, Path.Combine(root, "runtime"))
        { HostExecutable = executable, GuiExecutable = executable, CliExecutable = executable };
        return Task.CompletedTask;
    }

    /// <summary>列表只含 MCP, 控制器仅给 GUI; 名字不能改变权限, 错误或未知目标不能关闭 GUI.</summary>
    [Fact]
    public async Task AuthenticatedGuiOwnsManagementAndMetadataNeverGrantsWrite()
    {
        await StartAsync();
        var read = await ConnectAsync("mcp", false);
        var write = await ConnectAsync("mcp", true);
        await ConnectAsync("cli", true);
        await read.Peer.InvokeAsync("$host.mcpClientInfo", Json(new { clientName = "test AI", clientVersion = "1.0" }));
        var snapshot = await manager.ListAsync();
        Assert.True(snapshot.AcceptingConnections);
        Assert.Equal(2, snapshot.Connections.Count);
        var info = Assert.Single(snapshot.Connections, item => item.Id == read.ContextId);
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.Equal("test AI", info.ClientName);
        Assert.False(info.AllowWrite);
        Assert.Equal("connected", info.State);
        Assert.True(info.ProcessStartedAt <= info.ConnectedAt);
        Assert.True(Assert.Single(snapshot.Connections, item => item.Id == write.ContextId).AllowWrite);
        Assert.False((await manager.DisconnectAsync(Guid.NewGuid().ToString("N"))).Accepted);
        Assert.Equal("INVALID_ARGUMENT", (await Assert.ThrowsAsync<AppException>(() => manager.DisconnectAsync("123"))).Code);
        Assert.Equal("BUSINESS_HOST_METHOD_NOT_ALLOWED", (await Assert.ThrowsAsync<AppException>(() => write.Peer.InvokeAsync("mcp.setAdmission", Json(new { acceptingConnections = false })))).Code);
    }

    /// <summary>断开立即返回并取消准备; 提交后的原请求等待真实终态, 同伴保持可用.</summary>
    [Fact]
    public async Task DisconnectDrainsCommittedResultAndKeepsPeerAlive()
    {
        var entered = Signal(); var cancelled = Signal(); var finish = Signal();
        await StartAsync(async (_, _, token) =>
        {
            using var cancellation = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await finish.Task; return Json(new { committed = true });
        });
        var target = await ConnectAsync("mcp", true);
        var other = await ConnectAsync("mcp", false);
        var pending = target.Peer.InvokeAsync("commit", Json(new { }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await manager.DisconnectAsync(target.ContextId).WaitAsync(TimeSpan.FromSeconds(2))).Accepted);
            Assert.True((await manager.DisconnectAsync(target.ContextId)).Accepted);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var draining = Assert.Single((await manager.ListAsync()).Connections, item => item.Id == target.ContextId);
            Assert.Equal("draining", draining.State); Assert.Equal(1, draining.ActiveRequests);
            Assert.False(pending.IsCompleted);
            Assert.Equal("BUSINESS_HOST_STOPPING", (await Assert.ThrowsAsync<AppException>(() => target.Peer.InvokeAsync("echo", Json(new { })))).Code);
            Assert.True((await other.Peer.InvokeAsync("echo", Json(new { alive = true }))).GetProperty("alive").GetBoolean());
            finish.TrySetResult();
            Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("committed").GetBoolean());
            await target.Peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await target.Peer.DetachCompletion;
            // 同进程夹具仍存活, 因而不可只根据 IPC 已结束便从列表宣称进程退出.
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var retained = Assert.Single((await manager.ListAsync(observation.Token)).Connections, item => item.Id == target.ContextId);
                if (retained.State == "exiting") break;
                Assert.Equal("draining", retained.State);
                // 客户端读取最终帧与 Host finally 登记状态在不同任务上, 按公开轮询契约等待实际状态.
                await Task.Delay(10, observation.Token);
            }
        }
        finally { finish.TrySetResult(); }
    }

    /// <summary>暂停跨 Host 重启保留; GUI 仍能连接和恢复, 原拒绝连接不会自动重放.</summary>
    [Fact]
    public async Task PausePersistsAcrossHostRestartAndGuiCanResume()
    {
        await StartAsync();
        var target = await ConnectAsync("mcp", false);
        Assert.False((await manager.SetAdmissionAsync(false)).AcceptingConnections);
        await target.Peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("MCP_CONNECTIONS_PAUSED", (await Assert.ThrowsAsync<AppException>(() => ConnectAsync("mcp", true))).Code);
        await CloseCurrentAsync();
        await StartAsync();
        Assert.False((await manager.ListAsync()).AcceptingConnections);
        Assert.True((await manager.SetAdmissionAsync(true)).AcceptingConnections);
        var resumed = await ConnectAsync("mcp", false);
        Assert.NotEqual(target.ContextId, resumed.ContextId);
    }

    /// <summary>认证后业务组合仍未结束时暂停, 即使马上恢复也不能发布旧连接的成功 welcome.</summary>
    [Fact]
    public async Task PauseWinsAgainstContextConstructionAndResumeDoesNotReviveIt()
    {
        var entered = Signal(); var finish = Signal();
        server = await BusinessHostServer.StartAsync(settings, async (registration, _, _, controller) =>
        {
            if (registration.Options.Kind == "gui") manager = controller!;
            else { Assert.Null(controller); entered.TrySetResult(); await finish.Task; }
            return new Context((_, value, _) => Task.FromResult(value));
        });
        await ConnectAsync("gui", true);
        var connecting = ConnectAsync("mcp", false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.SetAdmissionAsync(false);
            await manager.SetAdmissionAsync(true);
            finish.TrySetResult();
            Assert.Equal("MCP_CONNECTIONS_PAUSED", (await Assert.ThrowsAsync<AppException>(() => connecting)).Code);
        }
        finally { finish.TrySetResult(); }
    }

    /// <summary>关闭 GUI 后保存的管理能力失效, 另一客户端不能借用旧 facade.</summary>
    [Fact]
    public async Task DetachedGuiCannotReuseManagementCapability()
    {
        await StartAsync();
        await ConnectAsync("mcp", false);
        await clients[0].DisposeAsync();
        Assert.Equal("MCP_MANAGEMENT_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => manager.ListAsync())).Code);
    }

    /// <summary>未知格式、重复字段和类型错误保留原文件且明确失败, 不回退为允许.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"acceptingConnections\":false,\"acceptingConnections\":true}")]
    [InlineData("{\"version\":2,\"acceptingConnections\":true}")]
    [InlineData("{\"version\":1,\"acceptingConnections\":\"false\"}")]
    [InlineData("{")]
    public void CorruptAdmissionIsNotAcceptedOrOverwritten(string text)
    {
        Directory.CreateDirectory(settings.CoordinationDirectory);
        var path = Path.Combine(settings.CoordinationDirectory, "mcp-admission.json");
        File.WriteAllText(path, text);
        Assert.Equal("MCP_ADMISSION_INVALID", Assert.Throws<AppException>(() => BusinessHostMcpAdmission.Read(settings)).Code);
        Assert.Throws<AppException>(() => BusinessHostMcpAdmission.Write(settings, true));
        Assert.Equal(text, File.ReadAllText(path));
    }

    /// <summary>仅真实缺失默认允许, 状态目录被文件占用不可被 DirectoryNotFound 当成缺失.</summary>
    [Fact]
    public void AdmissionDistinguishesMissingStateFromPathTypeFailure()
    {
        Assert.True(BusinessHostMcpAdmission.Read(settings));
        Directory.CreateDirectory(root);
        File.WriteAllText(settings.CoordinationDirectory, "collision");
        Assert.Equal("MCP_ADMISSION_INVALID", Assert.Throws<AppException>(() => BusinessHostMcpAdmission.Read(settings)).Code);
    }

    /// <summary>仅唯一 Host 组合根提供 GUI 管理能力, 业务回调保持明确白名单.</summary>
    private async Task StartAsync(BusinessHostRequestHandler? commit = null)
    {
        server = await BusinessHostServer.StartAsync(settings, (registration, _, _, controller) =>
        {
            if (registration.Options.Kind == "gui") manager = controller!;
            else Assert.Null(controller);
            return Task.FromResult<IBusinessHostContext>(new Context((method, value, token) => method switch
            {
                "echo" => Task.FromResult(value),
                "commit" when commit is not null => commit(method, value, token),
                _ => throw new AppException("BUSINESS_HOST_METHOD_NOT_ALLOWED", "测试业务只接受固定方法", 3)
            }));
        });
        await ConnectAsync("gui", true);
    }

    /// <summary>返回真实认证管道客户端, 测试独占保存其资源以正常关闭.</summary>
    private async Task<BusinessHostClient> ConnectAsync(string kind, bool write)
    {
        var client = await BusinessHostClient.ConnectAsync(settings, new(kind, write), null, null, default);
        clients.Add(client); return client;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private sealed class Context(BusinessHostRequestHandler handler) : IBusinessHostContext
    {
        public Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token) => handler(method, parameters, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>仅关闭本测试客户端和后台, 不清理用户或其他安装进程.</summary>
    private async Task CloseCurrentAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
        clients.Clear();
        if (server is not null) await server.DisposeAsync();
        server = null;
    }

    /// <summary>自然排空并删除隔离目录; 清理错误让测试失败.</summary>
    public async Task DisposeAsync()
    { await CloseCurrentAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
