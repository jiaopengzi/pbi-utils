// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostServer.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为同安装认证客户端组合独立上下文, 最后一个上下文排空后结束共享后台.

using PbiUtils.Infrastructure.Storage;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>只拥有传输和上下文生命周期, 业务白名单、模型授权和提交仍由 context 所有者裁决.</summary>
public sealed partial class BusinessHostServer : IAsyncDisposable
{
    private readonly BusinessHostSettings settings;
    private readonly BusinessHostContextFactory factory;
    private readonly BusinessHostOwnership ownership;
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenRegistration cancellation;
    private readonly Lock sync = new();
    private readonly Dictionary<string, Task> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BusinessHostPeer> peers = new(StringComparer.Ordinal);
    private readonly List<Exception> cleanupFailures = [];
    private readonly Task running;
    private bool closing;
    private bool attached;
    private int clientCount;
    private bool acceptingMcp;

    public Task Completion => running;
    /// <summary>已完成认证和业务组合的 context 数量, 必须在成功 welcome 可见前登记.</summary>
    public int ClientCount { get { lock (sync) return clientCount; } }

    /// <summary>取得真实后台租约后启动多连接接受循环, 无客户启动有界等待后退出.</summary>
    private BusinessHostServer(BusinessHostSettings settings, BusinessHostContextFactory factory, BusinessHostOwnership ownership, CancellationToken token)
    {
        this.settings = settings;
        this.factory = factory;
        this.ownership = ownership;
        acceptingMcp = BusinessHostMcpAdmission.Read(settings);
        cancellation = token.Register(RequestStop);
        running = RunAsync();
        _ = ObserveBootstrapAsync();
    }

    /// <summary>启动当前安装共享后台; 调用方等待 Completion 后再释放后台日志等组合根资源.</summary>
    public static Task<BusinessHostServer> StartAsync(string userRoot, BusinessHostContextFactory factory, CancellationToken cancellationToken = default) =>
        StartAsync(BusinessHostSettings.Current(userRoot), factory, cancellationToken);

    internal static async Task<BusinessHostServer> StartAsync(BusinessHostSettings settings, BusinessHostContextFactory factory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BusinessHostOwnership.IsMaintenanceHeld(settings) || UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw BusinessHostMaintenance.Busy();
        var ownership = await BusinessHostOwnership.AcquireAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            if (BusinessHostOwnership.IsMaintenanceHeld(settings) || UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw BusinessHostMaintenance.Busy();
            return new BusinessHostServer(settings, factory, ownership, cancellationToken);
        }
        catch { ownership.Dispose(); throw; }
    }

    /// <summary>每个接受连接先登记生命周期再并行认证, 不把业务等待放在接受循环内.</summary>
    private async Task RunAsync()
    {
        Exception? firstFailure = null;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(settings.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false); }
                catch { pipe.Dispose(); throw; }
                lock (sync)
                {
                    if (closing) { pipe.Dispose(); break; }
                    var id = Guid.NewGuid().ToString("N");
                    connections.Add(id, HandleConnectionAsync(id, pipe));
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { firstFailure = error; }
        try
        {
            RequestStop();
            Task[] draining;
            lock (sync) draining = [.. connections.Values];
            await Task.WhenAll(draining).ConfigureAwait(false);
        }
        catch (Exception error) { firstFailure ??= error; }
        // 独立释放剩余资源, 不从finally抛错覆盖接受循环或排空的首因.
        try { ownership.Dispose(); }
        catch (Exception error) { firstFailure ??= error; }
        try { cancellation.Dispose(); }
        catch (Exception error) { firstFailure ??= error; }
        lock (sync) foreach (var entry in mcpConnections.Values) entry.Process.Dispose();
        lock (sync) if (cleanupFailures.Count > 0)
            firstFailure ??= new AppException("BUSINESS_HOST_DRAIN_FAILED", "共享后台业务排空或资源释放失败, 请检查业务日志", 4);
        if (firstFailure is not null) ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    /// <summary>认证成功后每连接独立组合, EOF/关闭只排空该 context; 失败不移用另一客户端业务.</summary>
    private async Task HandleConnectionAsync(string connectionId, NamedPipeServerStream pipe)
    {
        await Task.Yield();
        BusinessHostPeer? peer = null;
        IBusinessHostContext? context = null;
        var registered = false;
        BusinessHostRegistration? registration = null;
        BusinessHostFailure? cleanupFailure = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var hello = await BusinessHostProtocol.ReadAsync<BusinessHostHello>(pipe, handshake.Token).ConfigureAwait(false);
            if (hello.Type != "hello" || hello.Version != BusinessHostProtocol.Version) throw BusinessHostProtocol.Invalid();
            var (processId, startTicks) = BusinessHostIdentity.RequireClient(pipe.SafePipeHandle, hello.Options, settings);
            registration = new BusinessHostRegistration(Guid.NewGuid().ToString("N"), ownership.Owner.Generation,
                hello.Options, processId, startTicks);
            lock (sync)
            {
                if (closing) throw new AppException("BUSINESS_HOST_STOPPING", "共享后台正在排空", 3);
                if (BusinessHostOwnership.IsMaintenanceHeld(settings) || UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw BusinessHostMaintenance.Busy();
                if (registration.Options.Kind == "mcp" && !acceptingMcp) throw BusinessHostMcpAdmission.Paused();
                peer = new(pipe, registration.HostGeneration, registration.ContextId, null, null);
                peers.Add(connectionId, peer);
                RegisterManagement(registration, peer);
            }
            context = await factory(registration, peer, handshake.Token,
                registration.Options.Kind == "gui" ? new McpController(this, registration.ContextId) : null).ConfigureAwait(false);
            var ownedContext = context;
            // 先发布本机生命周期事实, 再发送成功握手; 客户端收到 welcome 后即可依赖已登记状态.
            // 与关停/首次启动超时共用临界区, 写入失败仍由 finally 按 registered 回滚并排空.
            lock (sync)
            {
                handshake.Token.ThrowIfCancellationRequested();
                if (closing) throw new AppException("BUSINESS_HOST_STOPPING", "共享后台正在排空", 3);
                if (registration.Options.Kind == "mcp" && (!acceptingMcp || !peer.IsAccepting)) throw BusinessHostMcpAdmission.Paused();
                registered = true;
                clientCount++;
                attached = true;
            }
            await BusinessHostProtocol.WriteAsync(pipe, new BusinessHostWelcome("welcome", BusinessHostProtocol.Version, registration), handshake.Token).ConfigureAwait(false);
            peer.Start((method, parameters, token) => method switch
            {
                "$host.maintenance" => ValidateMaintenanceAsync(registration, parameters, token),
                "$host.mcpClientInfo" => UpdateClientInfoAsync(registration, parameters, token),
                _ => ownedContext.InvokeAsync(method, parameters, token)
            });
            await peer.DetachRequested.ConfigureAwait(false);
        }
        catch (Exception error) when (error is AppException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!registered)
            {
                var failure = error is AppException app ? new BusinessHostFailure(app.Code, app.Message, app.ExitCode)
                    : new("BUSINESS_HOST_CONNECTION_LOST", "共享后台握手未完成", 3);
                using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await BusinessHostProtocol.WriteAsync(pipe, new BusinessHostWelcome("welcome", BusinessHostProtocol.Version, null, failure), delivery.Token).ConfigureAwait(false); }
                catch (Exception deliveryError) when (deliveryError is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception error) { lock (sync) cleanupFailures.Add(error); }
        finally
        {
            lock (sync) if (registration is not null) MarkDraining(registration.ContextId);
            peer?.RequestShutdown();
            try { if (context is not null) await context.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                cleanupFailure = new("BUSINESS_HOST_DRAIN_FAILED", "当前后台上下文未正常排空, 请检查业务日志", 4);
                lock (sync) cleanupFailures.Add(error);
            }
            if (peer is not null)
            {
                await peer.DrainAsync().ConfigureAwait(false);
                await peer.CompleteDetachAsync(cleanupFailure).ConfigureAwait(false);
            }
            else pipe.Dispose();
            lock (sync)
            {
                if (registration is not null)
                {
                    guiConnections.Remove(registration.ContextId);
                    if (mcpConnections.TryGetValue(registration.ContextId, out var entry))
                    {
                        if (registered) entry.State = "exiting";
                        else { mcpConnections.Remove(registration.ContextId); entry.Process.Dispose(); }
                    }
                }
                peers.Remove(connectionId);
                connections.Remove(connectionId);
                if (registered) clientCount--;
                if (attached && connections.Count == 0) RequestStop();
            }
        }
    }

    /// <summary>维护锁由准确 GUI 持有, 检查与注册计数在同一锁内, 不能忽略未完成握手或排空.</summary>
    private Task<JsonElement> ValidateMaintenanceAsync(BusinessHostRegistration registration, JsonElement parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (closing || registration.Options.Kind != "gui" || connections.Count != 1 || clientCount != 1)
                throw BusinessHostMaintenance.Busy();
            BusinessHostMaintenance.Verify(settings, registration, parameters);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { acquired = true }, BusinessHostProtocol.Json));
        }
    }

    /// <summary>无客户端的失败启动不会永久常驻; 已连接后使用引用与排空生命周期.</summary>
    private async Task ObserveBootstrapAsync()
    {
        try
        {
            await Task.Delay(settings.BootstrapTimeout, stopping.Token).ConfigureAwait(false);
            lock (sync) if (!attached) RequestStop();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    /// <summary>原子停止接受并向现有 peer 请求准备取消, 不强杀或释放在途业务资源.</summary>
    private void RequestStop()
    {
        lock (sync)
        {
            if (closing) return;
            closing = true;
            stopping.Cancel();
            foreach (var peer in peers.Values) peer.RequestShutdown();
        }
    }

    /// <summary>仅组合根关闭时调用, 等待所有 context 真正排空.</summary>
    public async ValueTask DisposeAsync()
    {
        RequestStop();
        await running.ConfigureAwait(false);
    }
}
