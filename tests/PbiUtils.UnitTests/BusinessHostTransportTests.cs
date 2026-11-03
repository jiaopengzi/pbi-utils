// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessHostTransportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在隔离命名管道验证共享后台的上下文、双向调用、取消和排空边界.

using PbiUtils.Infrastructure.Storage;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用当前测试进程和临时目录, 不连接用户模型或正式宿主.</summary>
public sealed class BusinessHostTransportTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-business-host-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<BusinessHostClient> clients = [];
    private BusinessHostServer? server;
    private BusinessHostSettings settings = null!;

    /// <summary>创建隔离平台路径, 身份验证仍读取真实 OS 管道 PID 和进程启动时间.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule!.FileName;
        settings = new(root, Path.GetDirectoryName(executable)!, Path.Combine(root, "runtime"))
        {
            HostExecutable = executable,
            GuiExecutable = executable,
            CliExecutable = executable,
            BootstrapTimeout = TimeSpan.FromSeconds(5)
        };
        return Task.CompletedTask;
    }

    /// <summary>并行客户端必须获得独立上下文, 各自权限固定且结果不串答.</summary>
    [Fact]
    public async Task ConcurrentClientsKeepIndependentContextsAndCapabilities()
    {
        server = await BusinessHostServer.StartAsync(settings, (registration, _, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(Json(new
            {
                registration.ContextId,
                registration.Options.AllowWrite,
                value = value.GetProperty("value").GetInt32()
            })))));
        var connections = await Task.WhenAll(ConnectAsync(false), ConnectAsync(true));
        Assert.NotEqual(connections[0].ContextId, connections[1].ContextId);
        var results = await Task.WhenAll(connections.Select((client, index) => client.Peer.InvokeAsync("echo", Json(new { value = index }))));
        Assert.False(results[0].GetProperty("allowWrite").GetBoolean());
        Assert.True(results[1].GetProperty("allowWrite").GetBoolean());
        Assert.Equal(0, results[0].GetProperty("value").GetInt32());
        Assert.Equal(1, results[1].GetProperty("value").GetInt32());
        Assert.Equal(2, server.ClientCount);
    }

    /// <summary>后台等待 GUI 回调时, 接收循环仍处理回调答复和另一请求.</summary>
    [Fact]
    public async Task BidirectionalCallbackDoesNotBlockReader()
    {
        server = await BusinessHostServer.StartAsync(settings, (_, peer, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context(async (_, value, token) =>
                await peer.InvokeAsync("gui.pick", value, token))));
        var client = await ConnectAsync(true, (_, value, _) => Task.FromResult(value));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => client.Peer.InvokeAsync("pick", Json(new { value = index }))));
        Assert.Equal(Enumerable.Range(0, 20), results.Select(value => value.GetProperty("value").GetInt32()));
    }

    /// <summary>取消仅到达本次请求; 已开始的请求仍返回其实际排空结果.</summary>
    [Fact]
    public async Task CancellationIsScopedAndWaitsForActualResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) => Task.FromResult<IBusinessHostContext>(new Context(async (method, _, token) =>
        {
            if (method != "slow") return Json(new { intact = true });
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await finish.Task;
            return Json(new { committed = true });
        })));
        var client = await ConnectAsync(true);
        using var stop = new CancellationTokenSource();
        var pending = client.Peer.InvokeAsync("slow", Json(new { }), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.True((await client.Peer.InvokeAsync("fast", Json(new { }))).GetProperty("intact").GetBoolean());
        finish.SetResult();
        Assert.True((await pending).GetProperty("committed").GetBoolean());
    }

    /// <summary>一个连接离开不停止另一连接, 最后离开才释放后台租约.</summary>
    [Fact]
    public async Task DetachOnlyClosesOwnContextAndLastClientStopsServer()
    {
        ConcurrentBag<string> disposed = [];
        server = await BusinessHostServer.StartAsync(settings, (registration, _, _, _) => Task.FromResult<IBusinessHostContext>(
            new Context((_, value, _) => Task.FromResult(value), () => { disposed.Add(registration.ContextId); return ValueTask.CompletedTask; })));
        var first = await ConnectAsync(false);
        var second = await ConnectAsync(false);
        await first.DisposeAsync();
        Assert.Contains(first.ContextId, disposed);
        Assert.DoesNotContain(second.ContextId, disposed);
        Assert.False(server.Completion.IsCompleted);
        Assert.Equal(42, (await second.Peer.InvokeAsync("echo", Json(new { value = 42 }))).GetProperty("value").GetInt32());
        await second.DisposeAsync();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, disposed.Count);
    }

    /// <summary>后台最后关闭必须等待业务实际释放, 不能把客户端 detach 当成提交结束.</summary>
    [Fact]
    public async Task LastContextDrainRetainsOwnershipUntilActualCompletion()
    {
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) => Task.FromResult<IBusinessHostContext>(
            new Context((_, value, _) => Task.FromResult(value), async () => { draining.SetResult(); await release.Task; })));
        var client = await ConnectAsync(false);
        var detach = client.DisposeAsync().AsTask();
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.Completion.IsCompleted);
        Assert.False(detach.IsCompleted);
        await Assert.ThrowsAsync<AppException>(() => BusinessHostOwnership.AcquireAsync(settings));
        release.SetResult();
        await detach.WaitAsync(TimeSpan.FromSeconds(5));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var next = await BusinessHostOwnership.AcquireAsync(settings);
    }

    /// <summary>服务端验证真实安装路径, 不能因客户端自报 kind 获得连接.</summary>
    [Fact]
    public async Task UntrustedExecutableCannotRegisterContext()
    {
        var accepted = 0;
        settings.CliExecutable = Path.Combine(root, "different.exe");
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
        {
            Interlocked.Increment(ref accepted);
            return Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value)));
        });
        var error = await Assert.ThrowsAsync<AppException>(() => ConnectAsync(false));
        Assert.Equal("BUSINESS_HOST_IDENTITY_INVALID", error.Code);
        Assert.Equal(0, accepted);
    }

    /// <summary>进度和工作区事件必须按接收顺序应用, 异步 UI 处理不能把旧状态覆盖新状态.</summary>
    [Fact]
    public async Task EventsPreserveOrderWithoutBlockingResponses()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> observed = [];
        server = await BusinessHostServer.StartAsync(settings, (_, peer, _, _) => Task.FromResult<IBusinessHostContext>(new Context(async (_, _, token) =>
        {
            await peer.SendEventAsync("change", Json(new { value = 0 }), token);
            await peer.SendEventAsync("change", Json(new { value = 1 }), token);
            return Json(new { delivered = true });
        })));
        var client = await BusinessHostClient.ConnectAsync(settings, new("mcp", false), null, async (_, value, _) =>
        {
            var number = value.GetProperty("value").GetInt32();
            if (number == 0) { firstEntered.SetResult(); await release.Task; }
            else secondEntered.SetResult();
            observed.Enqueue(number);
            if (observed.Count == 2) finished.SetResult();
        }, default);
        clients.Add(client);
        try
        {
            Assert.True((await client.Peer.InvokeAsync("emit", Json(new { }))).GetProperty("delivered").GetBoolean());
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => secondEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([0, 1], observed);
    }

    /// <summary>同时按需连接只启动一个后台, 而不是每个客户端拥有自己的隐藏进程.</summary>
    [Fact]
    public async Task ConcurrentColdConnectionsElectExactlyOneStarter()
    {
        var launches = 0;
        settings.Launch = async token =>
        {
            Interlocked.Increment(ref launches);
            server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
                Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))), token);
        };
        var connected = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ConnectAsync(false)));
        Assert.Equal(1, launches);
        Assert.Equal(8, connected.Select(client => client.ContextId).Distinct().Count());
        Assert.Equal(8, server!.ClientCount);
    }

    /// <summary>后台取得锁到写穿身份之间不授权或误启动, 等待真实发布后才继续.</summary>
    [Fact]
    public async Task InitialPublicationWaitsForStartupInsteadOfFailingIdentity()
    {
        Directory.CreateDirectory(settings.CoordinationDirectory);
        var reserved = new FileStream(Path.Combine(settings.CoordinationDirectory, "publish.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var launches = 0;
        settings.Launch = async token =>
        {
            Interlocked.Increment(ref launches);
            server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
                Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))), token);
        };
        var connection = ConnectAsync(false);
        try
        {
            await Task.Delay(100);
            Assert.False(connection.IsCompleted);
            Assert.Equal(0, launches);
        }
        finally { reserved.Dispose(); }
        Assert.NotNull(await connection.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, launches);
    }

    /// <summary>维护预约由GUI持有至后台退出后, 新启动和注册都不能穿过重置窗口.</summary>
    [Fact]
    public async Task MaintenanceLeaseSurvivesContextDrainAndBlocksNewStartup()
    {
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))));
        var gui = await BusinessHostClient.ConnectAsync(settings, new("gui", true, "test"), null, null, default);
        clients.Add(gui);
        await using var maintenance = await gui.AcquireMaintenanceAsync();
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", Assert.Throws<AppException>(() => UserDataSessionLease.Acquire(root, exclusive: false)).Code);
        var otherInstallation = new BusinessHostSettings(root, Path.Combine(root, "other-installation"), Path.Combine(root, "other-runtime"));
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", (await Assert.ThrowsAsync<AppException>(() =>
            BusinessHostClient.ConnectAsync(otherInstallation, new("mcp", false), null, null, default))).Code);
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", (await Assert.ThrowsAsync<AppException>(() => ConnectAsync(false))).Code);
        await gui.DisposeAsync();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", (await Assert.ThrowsAsync<AppException>(() =>
            BusinessHostServer.StartAsync(settings, (_, _, _, _) => Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value)))))).Code);
        maintenance.Dispose();
        using var session = UserDataSessionLease.Acquire(root, exclusive: false);
        using var newOwner = await BusinessHostOwnership.AcquireAsync(settings);
    }

    /// <summary>客户端意外EOF后本context继续排空, 另一客户端仍能调用且未知结果不得自动重放.</summary>
    [Fact]
    public async Task UnexpectedEofDrainsOnlyOwnWorkAndPreservesOtherClient()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
        {
            var hasWork = false;
            return Task.FromResult<IBusinessHostContext>(new Context(async (method, value, _) =>
            {
                if (method != "commit") return value;
                hasWork = true;
                entered.SetResult();
                await release.Task;
                return Json(new { committed = true });
            }, async () => { if (hasWork) { await release.Task; drained.SetResult(); } }));
        });
        var first = await ConnectAsync(true);
        var second = await ConnectAsync(false);
        var committing = first.Peer.InvokeAsync("commit", Json(new { }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.Peer.DisposeAsync();
        try
        {
            Assert.Equal("BUSINESS_HOST_CONNECTION_LOST", (await Assert.ThrowsAsync<AppException>(() => committing)).Code);
            Assert.False(drained.Task.IsCompleted);
            Assert.Equal(42, (await second.Peer.InvokeAsync("echo", Json(new { value = 42 }))).GetProperty("value").GetInt32());
            Assert.False(server.Completion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>维护失败释放预约, 后续客户端成功握手时已登记且各连接继续可用, 退出一个不停止同伴.</summary>
    [Fact]
    public async Task MaintenanceRefusalReleasesReservationWithoutStoppingPeers()
    {
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))));
        var gui = await BusinessHostClient.ConnectAsync(settings, new("gui", true, "test"), null, null, default);
        clients.Add(gui);
        var mcp = await ConnectAsync(false);
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", (await Assert.ThrowsAsync<AppException>(() => gui.AcquireMaintenanceAsync())).Code);
        var third = await ConnectAsync(false);
        Assert.NotEqual(mcp.ContextId, third.ContextId);
        Assert.Equal(3, server.ClientCount);
        foreach (var client in new[] { gui, mcp, third })
            Assert.Equal(42, (await client.Peer.InvokeAsync("echo", Json(new { value = 42 }))).GetProperty("value").GetInt32());
        await third.DisposeAsync();
        clients.Remove(third);
        Assert.False(server.Completion.IsCompleted);
        foreach (var client in new[] { gui, mcp })
            Assert.Equal(43, (await client.Peer.InvokeAsync("echo", Json(new { value = 43 }))).GetProperty("value").GetInt32());
    }

    /// <summary>业务组合期间客户端离开后, 成功握手无法交付仍回滚登记、排空 context 并释放后台租约.</summary>
    [Fact]
    public async Task ClientLeavingDuringCompositionRollsBackRegistrationAndDrainsHost()
    {
        settings.BootstrapTimeout = TimeSpan.FromMinutes(1);
        var composing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server = await BusinessHostServer.StartAsync(settings, async (_, _, _, _) =>
        {
            composing.TrySetResult();
            await release.Task;
            return new Context((_, value, _) => Task.FromResult(value), () => { disposed.TrySetResult(); return ValueTask.CompletedTask; });
        });
        using var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            await BusinessHostProtocol.WriteAsync(pipe, new BusinessHostHello("hello", BusinessHostProtocol.Version, new("mcp", false)), deadline.Token);
            await composing.Task.WaitAsync(deadline.Token);
            Assert.Equal(0, server.ClientCount);
            pipe.Dispose();
        }
        finally { release.TrySetResult(); }
        await disposed.Task.WaitAsync(deadline.Token);
        await server.Completion.WaitAsync(deadline.Token);
        Assert.Equal(0, server.ClientCount);
        Assert.Null(BusinessHostOwnership.Read(settings));
    }

    /// <summary>从未连接的后台启动在有界期限后退出, 不成为无客户端常驻服务.</summary>
    [Fact]
    public async Task UnclaimedStartupExitsAndReleasesOwnership()
    {
        settings.BootstrapTimeout = TimeSpan.FromMilliseconds(80);
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(BusinessHostOwnership.Read(settings));
    }

    /// <summary>只在welcome前失联时重新发现后台, 旧连接未发送业务请求且不能重放任何已提交操作.</summary>
    /// <param name="stoppingReply">旧后台显式排空拒绝或直接EOF的两个真实退出窗口.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandshakeInterruptedByHostExitCanRediscover(bool stoppingReply)
    {
        using var ownership = await BusinessHostOwnership.AcquireAsync(settings);
        using var pipe = new NamedPipeServerStream(settings.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = pipe.WaitForConnectionAsync(deadline.Token);
        var launches = 0;
        settings.Launch = async token =>
        {
            launches++;
            server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
                Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value))), token);
        };
        var connecting = ConnectAsync(false);
        await accepting;
        Assert.Equal("hello", (await BusinessHostProtocol.ReadAsync<BusinessHostHello>(pipe, deadline.Token)).Type);
        if (stoppingReply)
            await BusinessHostProtocol.WriteAsync(pipe, new BusinessHostWelcome("welcome", 1, null,
                new("BUSINESS_HOST_STOPPING", "共享后台正在排空", 3)), deadline.Token);
        pipe.Dispose();
        ownership.Dispose();
        var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, launches);
        Assert.Equal(17, (await client.Peer.InvokeAsync("echo", Json(new { value = 17 }))).GetProperty("value").GetInt32());
    }

    /// <summary>上下文排空故障明确返回失败, 仍释放稳定后台租约且不在finally覆盖首因.</summary>
    [Fact]
    public async Task ContextDrainFailureIsReportedAndReleasesOwnership()
    {
        server = await BusinessHostServer.StartAsync(settings, (_, _, _, _) =>
            Task.FromResult<IBusinessHostContext>(new Context((_, value, _) => Task.FromResult(value),
                () => ValueTask.FromException(new IOException("isolated-dispose-failure")))));
        var client = await ConnectAsync(false);
        Assert.Equal("BUSINESS_HOST_DRAIN_FAILED", (await Assert.ThrowsAsync<AppException>(() => client.DisposeAsync().AsTask())).Code);
        clients.Remove(client);
        Assert.Equal("BUSINESS_HOST_DRAIN_FAILED", (await Assert.ThrowsAsync<AppException>(() => server.Completion)).Code);
        Assert.Null(BusinessHostOwnership.Read(settings));
        server = null;
    }

    /// <summary>后台发布新身份期间读者不得把尚未取得lifetime或旧记录视为空闲, 防止启动另一后台.</summary>
    [Fact]
    public void IdentityPublicationIsASeparateStartupBoundary()
    {
        Directory.CreateDirectory(settings.CoordinationDirectory);
        using var publishing = new FileStream(Path.Combine(settings.CoordinationDirectory, "publish.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal("BUSINESS_HOST_STARTING", Assert.Throws<AppException>(() => BusinessHostOwnership.Read(settings)).Code);
        publishing.Dispose();
        Assert.Null(BusinessHostOwnership.Read(settings));
    }

    /// <summary>暂停在后台尚未运行时也拒绝 MCP, 不能因重新启动客户端恢复准入.</summary>
    [Fact]
    public async Task PersistedPauseRejectsMcpBeforeLaunchingHost()
    {
        Directory.CreateDirectory(settings.CoordinationDirectory);
        await File.WriteAllTextAsync(Path.Combine(settings.CoordinationDirectory, "mcp-admission.json"),
            "{\"version\":1,\"acceptingConnections\":false}");
        var launches = 0;
        settings.Launch = _ => { launches++; return Task.CompletedTask; };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var failure = await Assert.ThrowsAsync<AppException>(() => BusinessHostClient.ConnectAsync(settings,
            new("mcp", false), null, null, deadline.Token));
        Assert.Equal("MCP_CONNECTIONS_PAUSED", failure.Code);
        Assert.Equal(0, launches);
    }

    /// <summary>已登记排空后迟到新调用只能被拒绝, 不能丢弃另一已提交请求的真实终态.</summary>
    [Fact]
    public async Task LateRequestDuringDrainDoesNotDropCommittedResult()
    {
        BusinessHostPeer? hostPeer = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server = await BusinessHostServer.StartAsync(settings, (_, peer, _, _) =>
        {
            hostPeer = peer;
            return Task.FromResult<IBusinessHostContext>(new Context(async (_, _, _) =>
            { entered.TrySetResult(); await finish.Task; return Json(new { committed = true }); },
                () => new ValueTask(finish.Task)));
        });
        var client = await ConnectAsync(true);
        var committed = client.Peer.InvokeAsync("committed", Json(new { }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            hostPeer!.RequestShutdown();
            var failure = await Assert.ThrowsAsync<AppException>(() => client.Peer.InvokeAsync("late", Json(new { })));
            Assert.Equal("BUSINESS_HOST_STOPPING", failure.Code);
            finish.TrySetResult();
            Assert.True((await committed.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("committed").GetBoolean());
        }
        finally { finish.TrySetResult(); try { await committed; } catch (AppException) { } }
    }

    /// <summary>隔离调用只使用当前测试 server, 不触发正式应用启动.</summary>
    private async Task<BusinessHostClient> ConnectAsync(bool allowWrite, BusinessHostRequestHandler? handler = null)
    {
        var client = await BusinessHostClient.ConnectAsync(settings, new("mcp", allowWrite), handler, null, default);
        lock (clients) clients.Add(client);
        return client;
    }

    /// <summary>构造完整 JSON 值, 避免返回已释放文档的借用元素.</summary>
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>保持真实 context 生命周期, 仅业务体使用不接触用户模型的探针.</summary>
    private sealed class Context(BusinessHostRequestHandler invoke, Func<ValueTask>? dispose = null) : IBusinessHostContext
    {
        public Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token) => invoke(method, parameters, token);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }

    /// <summary>按自有连接与 server 顺序排空, 仅删除本测试的独占临时目录.</summary>
    public async Task DisposeAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
        if (server is not null) await server.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
