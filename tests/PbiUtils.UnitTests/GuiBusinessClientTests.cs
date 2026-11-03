// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GuiBusinessClientTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 经真实隔离命名管道核验 GUI 远程业务、回调与维护重置边界.

using PbiUtils.Desktop.Bridge;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Desktop;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用测试进程自身身份和独占临时根, 不启动产品或连接任何模型.</summary>
public sealed class GuiBusinessClientTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-gui-client-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<JsonElement> messages = [];
    private readonly UiDialogBroker dialogs;
    private BusinessHostSettings settings = null!;
    private BusinessHostServer? server;
    private GuiBusinessClient? client;
    private int resets;

    public GuiBusinessClientTests() => dialogs = new(Send);

    /// <summary>身份认证仍核验真实 OS PID/启动时间, 仅安装路径替换为本测试进程.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule!.FileName;
        settings = new(root, Path.GetDirectoryName(executable)!, Path.Combine(root, "runtime"))
        { HostExecutable = executable, GuiExecutable = executable, CliExecutable = executable, BootstrapTimeout = TimeSpan.FromSeconds(5) };
        return Task.CompletedTask;
    }

    /// <summary>完成结果明确携带重置事实, 无须等异步事件才能保留维护预约.</summary>
    [Fact]
    public async Task ResetCompletionRetainsMaintenanceWithoutAnEvent()
    {
        await ConnectAsync((_, _) => (_, _, _) => Task.FromResult(Json(new { resetScheduled = true })));
        await client!.ReceiveAsync(OriginPolicy.Production, Request("reset", "app.resetUserData"), 0);
        Assert.Equal(1, resets);
        Assert.True(BusinessHostOwnership.IsMaintenanceHeld(settings));
        await using var lease = client.TakeMaintenanceLease();
        Assert.NotNull(lease);
    }

    /// <summary>第二次重置被拒绝时不得释放第一请求正在持有的维护预约.</summary>
    [Fact]
    public async Task DuplicateResetCannotReleaseFirstRequestsMaintenance()
    {
        var entered = Signal();
        var release = Signal();
        await ConnectAsync((_, _) => async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Json(new { resetScheduled = false });
        });
        var first = client!.ReceiveAsync(OriginPolicy.Production, Request("first", "app.resetUserData"), 0);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.ReceiveAsync(OriginPolicy.Production, Request("second", "app.resetUserData"), 0);
            Assert.True(BusinessHostOwnership.IsMaintenanceHeld(settings));
            Assert.Contains(messages, value => value.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "BUSY");
        }
        finally { release.TrySetResult(); await first; }
        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
    }

    /// <summary>受限原生回调必须同时绑定活动请求和一次性身份, 已完成请求不能再调前台.</summary>
    [Fact]
    public async Task NativeCallbackIsBoundToActiveRequestAndSingleUseIdentity()
    {
        var callbacks = 0;
        BusinessHostPeer? hostPeer = null;
        var call = new GuiHostCallback(0, "pick", new string('a', 64), "selectProject", Json(new { }));
        await ConnectAsync((_, peer) =>
        {
            hostPeer = peer;
            return async (_, _, token) =>
            {
                Assert.Equal("chosen.pbip", (await peer.InvokeAsync("gui.callback", Json(call), token)).GetProperty("path").GetString());
                Assert.Equal("STALE_GUI_CALLBACK", (await Assert.ThrowsAsync<AppException>(() => peer.InvokeAsync("gui.callback", Json(call), token))).Code);
                return Json(new { resetScheduled = false });
            };
        }, (_, _) => { Interlocked.Increment(ref callbacks); return Task.FromResult(Json(new GuiPathSelection("chosen.pbip"))); });
        await client!.ReceiveAsync(OriginPolicy.Production, Request("pick", "project.select"), 0);
        Assert.Equal(1, callbacks);
        Assert.Equal("STALE_GUI_CALLBACK", (await Assert.ThrowsAsync<AppException>(() => hostPeer!.InvokeAsync("gui.callback", Json(call with { CallbackId = new string('b', 64) })))).Code);
        Assert.Equal(1, callbacks);
    }

    /// <summary>页面导航立即撤销旧请求原生回调并丢弃迟到业务消息.</summary>
    [Fact]
    public async Task NavigationRejectsLateCallbacksAndResults()
    {
        var entered = Signal();
        var release = Signal();
        var delivered = Signal();
        var callbacks = 0;
        await ConnectAsync((_, peer) => async (method, _, token) =>
        {
            if (method == "gui.invalidate") return Json(new { completed = true });
            entered.TrySetResult();
            await release.Task;
            var callback = new GuiHostCallback(0, "old", new string('c', 64), "selectProject", Json(new { }));
            Assert.Equal("STALE_GUI_CALLBACK", (await Assert.ThrowsAsync<AppException>(() => peer.InvokeAsync("gui.callback", Json(callback), token))).Code);
            await peer.SendEventAsync("bridge.message", Json(new GuiHostMessage(0, Json(new { protocolVersion = 1, id = "old", result = 42 }))), token);
            return Json(new { resetScheduled = false });
        }, (_, _) => { Interlocked.Increment(ref callbacks); return Task.FromResult(Json(new { })); }, () => delivered.TrySetResult());
        var pending = client!.ReceiveAsync(OriginPolicy.Production, Request("old", "project.current"), 0);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.InvalidateDocumentAsync();
        }
        finally { release.TrySetResult(); }
        await pending;
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(messages);
        Assert.Equal(0, callbacks);
    }

    /// <summary>非可信来源不触发后台, 本地关窗提示回复也不代理到业务上下文.</summary>
    [Fact]
    public async Task OriginAndLocalDialogCannotBecomeRemoteBusinessCalls()
    {
        var calls = 0;
        await ConnectAsync((_, _) => (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Json(new { resetScheduled = false })); });
        await client!.ReceiveAsync("https://untrusted.example", Request("bad", "project.current"), 0);
        var answer = dialogs.ShowAsync(new("confirm", "close"), default);
        var dialog = messages.Single().GetProperty("uiDialog").GetProperty("id").GetString();
        await client.ReceiveAsync(OriginPolicy.Production, Json(new { protocolVersion = 1, id = "answer", method = "ui.dialogRespond", @params = new { dialogId = dialog, action = "confirm", values = new { } } }).GetRawText(), 0);
        Assert.Equal("confirm", (await answer).Action);
        Assert.Equal(0, calls);
    }

    /// <summary>原始 WebView 回调捕获的旧代际不能在同源导航后排队升级为新文档请求.</summary>
    [Fact]
    public async Task PreviouslyQueuedPageMessageIsNotAdoptedAfterNavigation()
    {
        var calls = 0;
        await ConnectAsync((_, _) => (method, _, _) =>
        {
            if (method == "gui.invoke") Interlocked.Increment(ref calls);
            return Task.FromResult(Json(new { completed = true }));
        });
        var queuedEpoch = client!.DocumentEpoch;
        await client.InvalidateDocumentAsync();
        await client.ReceiveAsync(OriginPolicy.Production, Request("queued", "project.disconnect"), queuedEpoch);
        Assert.Equal(0, calls);
        Assert.Empty(messages);
    }

    /// <summary>UI 排队后的重置调度拒绝必须被原请求观察, 撤销后台意图并释放预约.</summary>
    [Fact]
    public async Task QueuedResetSchedulingFailureIsObservedAndReleasesMaintenance()
    {
        var entered = Signal();
        var rejected = Signal();
        var cancelled = false;
        await ConnectAsync((_, _) => (method, _, _) =>
        {
            if (method == "gui.cancelReset") cancelled = true;
            return Task.FromResult(Json(new { resetScheduled = true }));
        }, scheduleReset: async () => { entered.TrySetResult(); await rejected.Task; });
        var pending = client!.ReceiveAsync(OriginPolicy.Production, Request("reset", "app.resetUserData"), 0);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.True(BusinessHostOwnership.IsMaintenanceHeld(settings));
        }
        finally { rejected.TrySetException(new AppException("BUSY", "窗口正在关闭", 3)); }
        await pending;
        Assert.True(cancelled);
        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
        Assert.Contains(messages, value => value.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "BUSY");
    }

    /// <summary>组合产品适配与真实隔离管道, 仅业务用例由测试探针代替.</summary>
    private async Task ConnectAsync(Func<BusinessHostRegistration, BusinessHostPeer, BusinessHostRequestHandler> factory,
        Func<GuiHostCallback, CancellationToken, Task<JsonElement>>? callback = null, Action? eventDelivered = null, Func<Task>? scheduleReset = null)
    {
        server = await BusinessHostServer.StartAsync(settings, (registration, peer, _, _) => Task.FromResult<IBusinessHostContext>(new Context(factory(registration, peer))));
        client = await GuiBusinessClient.ConnectAsync(new OriginPolicy(false, null), (message, _) => Send(message), callback ?? ((_, _) => Task.FromResult(Json(new { }))), dialogs,
            scheduleReset ?? (() => { Interlocked.Increment(ref resets); return Task.CompletedTask; }), (requestHandler, eventHandler) => BusinessHostClient.ConnectAsync(settings, new("gui", true, "test"), requestHandler,
                async (name, value, token) => { await eventHandler(name, value, token); eventDelivered?.Invoke(); }, default));
    }

    private void Send(string value) => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(value));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Json<T>(T value) => GuiHostJson.Element(value);
    private static string Request(string id, string method) => Json(new { protocolVersion = 1, id, method, @params = new { } }).GetRawText();
    private sealed class Context(BusinessHostRequestHandler invoke) : IBusinessHostContext
    {
        public Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token) => invoke(method, parameters, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>只排空自有临时通道, 保证维护票据释放后删除测试目录.</summary>
    public async Task DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
        if (server is not null) await server.DisposeAsync();
        dialogs.Dispose();
        Directory.Delete(root, true);
    }
}
