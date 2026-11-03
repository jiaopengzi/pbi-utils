// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostPeer.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 认证后单上下文的双向请求、定向事件、取消和串行发送生命周期.

using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>一条已认证持久管道只对应一个上下文; 每个请求相关和取消均不能跨上下文.</summary>
public sealed partial class BusinessHostPeer : IAsyncDisposable
{
    private const int MaximumPending = 128;
    private readonly Stream stream;
    private readonly SemaphoreSlim sender = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Incoming> incoming = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource detachRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock lifecycle = new();
    private BusinessHostRequestHandler? requestHandler;
    private readonly BusinessHostEventHandler? eventHandler;
    private Task? reader;
    private Task eventTail = Task.CompletedTask;
    private Task? disposal;
    private bool accepting = true;
    private bool transportClosed;
    private bool receivedDetach;
    private bool receivedDetachResult;

    public string ContextId { get; }
    public string HostGeneration { get; }
    /// <summary>实际接收循环结束; 在途业务的排空由 context owner 另行等待.</summary>
    public Task Completion => reader ?? throw new InvalidOperationException("后台通道尚未启动");
    /// <summary>后台确认业务排空的终态; 意外 EOF 或排空失败明确失败, 不推定修改未提交.</summary>
    public Task DetachCompletion => detached.Task;
    internal int ActiveRequests => incoming.Count;
    internal bool IsAccepting { get { lock (lifecycle) return accepting && !transportClosed; } }
    internal Task DetachRequested => detachRequested.Task;

    /// <summary>接收已认证流与不可变上下文; welcome 发送后才启动 reader.</summary>
    internal BusinessHostPeer(Stream stream, string generation, string contextId,
        BusinessHostRequestHandler? requestHandler, BusinessHostEventHandler? eventHandler)
    {
        this.stream = stream;
        HostGeneration = generation;
        ContextId = contextId;
        this.requestHandler = requestHandler;
        this.eventHandler = eventHandler;
    }

    /// <summary>仅在握手成功后启动一次, factory 可以在此之前提供 context 分发器.</summary>
    internal void Start(BusinessHostRequestHandler? handler = null)
    {
        lock (lifecycle)
        {
            if (reader is not null) throw new InvalidOperationException("后台通道已启动");
            if (handler is not null) requestHandler = handler;
            reader = ReadLoopAsync();
        }
    }

    /// <summary>发起当前上下文固定业务/回调; 取消通知对端后等待真实结果, 不自动重放未知提交.</summary>
    /// <param name="method">由上层白名单允许的方法, 不解释为 shell、反射或任意执行.</param>
    /// <param name="parameters">完整业务参数, 具体预算由上层入口再次核验.</param>
    /// <param name="token">准备取消; 已发送请求仍等待对端实际终态.</param>
    /// <returns>完整 JSON 结果; 失联不推定未提交.</returns>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RequireMethod(method, parameters);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            if (!accepting || transportClosed) throw ConnectionLost();
            if (pending.Count >= MaximumPending) throw new AppException("BUSY", "共享后台在途请求过多", 3);
            pending[id] = completion;
        }
        try
        {
            await SendAsync(new("call", HostGeneration, ContextId, id, method, parameters), token).ConfigureAwait(false);
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellation.TrySetResult());
            if (await Task.WhenAny(completion.Task, cancellation.Task).ConfigureAwait(false) == cancellation.Task && !completion.Task.IsCompleted)
                await SendAsync(new("cancel", HostGeneration, ContextId, id), CancellationToken.None).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>向当前连接发布定向事件, 不广播其他上下文值; 发送失败明确结束传输.</summary>
    public Task SendEventAsync(string name, JsonElement parameters, CancellationToken token = default)
    {
        RequireMethod(name, parameters);
        return SendAsync(new("event", HostGeneration, ContextId, Method: name, Data: parameters), token);
    }

    /// <summary>唯一发送门闩避免 header/body 交错; detach 写入遇远端关闭时可保留 reader 消费其已发送终态.</summary>
    /// <param name="preserveIncomingOnClosedWrite">仅用于无业务副作用的 detach 控制帧, 不把写失败当成排空成功.</param>
    private async Task SendAsync(BusinessHostFrame frame, CancellationToken token, bool preserveIncomingOnClosedWrite = false)
    {
        token.ThrowIfCancellationRequested();
        await sender.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (lifecycle) if (transportClosed) throw ConnectionLost();
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await BusinessHostProtocol.WriteAsync(stream, frame, deadline.Token).ConfigureAwait(false);
        }
        catch (IOException) when (preserveIncomingOnClosedWrite)
        {
            // 远端可能已发送 detached 并关闭管道, 当前 reader 还没消费缓冲帧.
            // 不抢先取消读取; 调用方仍等待明确终态或 reader 对真正 EOF 的失败判断.
            throw ConnectionLost();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            FailTransport();
            throw ConnectionLost();
        }
        finally { sender.Release(); }
    }

    /// <summary>客户端停止自身新调用并请求排空; ack 只代表本 context, 不停止其他客户端.</summary>
    internal async Task DetachAsync()
    {
        bool send;
        lock (lifecycle)
        {
            if (transportClosed && !receivedDetachResult) return;
            accepting = false;
            send = !receivedDetachResult;
        }
        try
        {
            var closedWrite = false;
            if (send)
            {
                try { await SendAsync(new("detach", HostGeneration, ContextId), CancellationToken.None, preserveIncomingOnClosedWrite: true).ConfigureAwait(false); }
                catch (AppException error) when (error.Code == "BUSINESS_HOST_CONNECTION_LOST")
                { closedWrite = true; }
            }
            if (closedWrite)
            {
                // 仅控制帧写失败后的入站尾帧观察有界; 超时只结束本连接, Host owner 继续排空业务.
                try { await detached.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (TimeoutException) { throw ConnectionLost(); }
            }
            else await detached.Task.ConfigureAwait(false);
        }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>宿主取消准备并开始关闭本 context; 仍允许在途结果和控制回复通过.</summary>
    internal void RequestShutdown()
    {
        StopAccepting();
        foreach (var request in incoming.Values) request.Cancel();
        detachRequested.TrySetResult();
    }

    /// <summary>仅关闭新请求门闩, 不在 Host 注册锁内调用业务取消回调.</summary>
    internal void StopAccepting() { lock (lifecycle) accepting = false; }

    /// <summary>在 context 已排空后等待被接收的处理任务全部结束, 不根据客户端断开推定完成.</summary>
    internal async Task DrainAsync()
    {
        while (!incoming.IsEmpty)
        {
            var tasks = incoming.Values.Select(value => value.Completion.Task).ToArray();
            if (tasks.Length == 0) break;
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>实际 context 释放后发唯一 detach 完成信号; 对端已断开时只结束传输.</summary>
    internal async Task CompleteDetachAsync(BusinessHostFailure? failure = null)
    {
        bool send;
        lock (lifecycle) send = !transportClosed;
        if (send)
        {
            try { await SendAsync(new("detached", HostGeneration, ContextId, Error: failure), CancellationToken.None).ConfigureAwait(false); }
            catch (AppException error) when (error.Code == "BUSINESS_HOST_CONNECTION_LOST") { /* 对端已离开, 业务和回执已经排空. */ }
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>终止传输并请求取消本连接的准备, 保留在途业务至实际 Drain 完成.</summary>
    private void FailTransport()
    {
        lock (lifecycle)
        {
            if (transportClosed) return;
            transportClosed = true;
            accepting = false;
        }
        lifetime.Cancel();
        stream.Dispose();
        foreach (var completion in pending.Values) completion.TrySetException(ConnectionLost());
        foreach (var request in incoming.Values) request.Cancel();
        detached.TrySetException(ConnectionLost());
        detachRequested.TrySetResult();
    }

    /// <summary>幂等关闭自身流并等待 reader, 不释放另一端业务对象或强杀后台.</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new(disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        FailTransport();
        if (reader is not null) await reader.ConfigureAwait(false);
    }

    /// <summary>固定方法名和完整 JSON 必须有效, 不允许未定义参数进入传输.</summary>
    private static void RequireMethod(string method, JsonElement data)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 128 || data.ValueKind == JsonValueKind.Undefined) throw BusinessHostProtocol.Invalid();
    }

    internal static AppException ConnectionLost() => new("BUSINESS_HOST_CONNECTION_LOST", "共享后台连接已中断; 在途修改可能需要核验, 不要自动重放", 4);

    /// <summary>请求令牌与完成事实独立, 取消不提前释放业务.</summary>
    private sealed class Incoming : IDisposable
    {
        private readonly Lock sync = new();
        private bool disposed;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel() { lock (sync) if (!disposed) Cancellation.Cancel(); }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; Cancellation.Dispose(); } }
    }
}
