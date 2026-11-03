// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostClient.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 同安装共享后台的按需启动与认证连接, 客户端退出只排空自己的上下文.

using PbiUtils.Infrastructure.Storage;
using System.Diagnostics;
using System.IO.Pipes;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>客户端只拥有自己的持久连接, 不拥有共享后台进程或其他 context 的终止权.</summary>
public sealed class BusinessHostClient : IAsyncDisposable
{
    private readonly BusinessHostSettings settings;
    private readonly BusinessHostOwner owner;
    private readonly BusinessHostRegistration registration;
    private readonly Lock sync = new();
    private Task? disposal;
    public string ContextId => Peer.ContextId;
    public BusinessHostPeer Peer { get; }

    private BusinessHostClient(BusinessHostSettings settings, BusinessHostOwner owner, BusinessHostRegistration registration, BusinessHostPeer peer)
    { this.settings = settings; this.owner = owner; this.registration = registration; Peer = peer; }

    /// <summary>连接当前安装后台, 没有运行者时在短时选主租约内隐藏启动准确 Host.exe.</summary>
    /// <param name="userRoot">正式组合根固定的应用数据根, 不接受业务参数授权.</param>
    /// <param name="options">来自真实 GUI 或 CLI 启动解析的固定能力.</param>
    /// <param name="requestHandler">GUI 受限原生回调, MCP 通常为空.</param>
    /// <param name="eventHandler">当前上下文的定向业务事件.</param>
    /// <param name="cancellationToken">仅取消本客户端启动/认证等待, 不结束已经共享的后台.</param>
    public static Task<BusinessHostClient> ConnectAsync(string userRoot, BusinessHostClientOptions options,
        BusinessHostRequestHandler? requestHandler = null, BusinessHostEventHandler? eventHandler = null,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(BusinessHostSettings.Current(userRoot), options, requestHandler, eventHandler, cancellationToken);

    internal static async Task<BusinessHostClient> ConnectAsync(BusinessHostSettings settings, BusinessHostClientOptions options,
        BusinessHostRequestHandler? requestHandler, BusinessHostEventHandler? eventHandler, CancellationToken token)
    {
        BusinessHostIdentity.ValidateOptions(options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        FileStream? startup = null;
        var launched = false;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (options.Kind == "mcp") BusinessHostMcpAdmission.RequireAllowed(settings);
                if (BusinessHostOwnership.IsMaintenanceHeld(settings) || UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw BusinessHostMaintenance.Busy();
                BusinessHostOwner? current;
                try { current = BusinessHostOwnership.Read(settings); }
                catch (AppException error) when (error.Code == "BUSINESS_HOST_STARTING")
                {
                    await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (current is not null)
                {
                    var connected = await TryConnectAsync(settings, current, options, requestHandler, eventHandler, deadline.Token).ConfigureAwait(false);
                    if (connected is not null) return connected;
                }
                else if (!launched)
                {
                    startup ??= BusinessHostOwnership.TryAcquireStartup(settings);
                    if (startup is not null)
                    {
                        try { current = BusinessHostOwnership.Read(settings); }
                        catch (AppException error) when (error.Code == "BUSINESS_HOST_STARTING") { continue; }
                        if (current is null)
                        {
                            if (options.Kind == "mcp") BusinessHostMcpAdmission.RequireAllowed(settings);
                            if (BusinessHostOwnership.IsMaintenanceHeld(settings) || UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw BusinessHostMaintenance.Busy();
                            await settings.LaunchAsync(deadline.Token).ConfigureAwait(false);
                            launched = true;
                        }
                    }
                }
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new AppException("BUSINESS_HOST_START_TIMEOUT", "共享业务后台未在限定时间就绪或仍在排空", 3); }
        finally { startup?.Dispose(); }
    }

    /// <summary>welcome前的退出竞争允许重新发现; 完成握手后业务调用故障明确返回而不自动重放.</summary>
    private static async Task<BusinessHostClient?> TryConnectAsync(BusinessHostSettings settings, BusinessHostOwner owner,
        BusinessHostClientOptions options, BusinessHostRequestHandler? requests, BusinessHostEventHandler? events, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var transferred = false;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(TimeSpan.FromMilliseconds(250));
            try { await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            BusinessHostIdentity.RequireServer(pipe.SafePipeHandle, owner, settings);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await BusinessHostProtocol.WriteAsync(pipe, new BusinessHostHello("hello", BusinessHostProtocol.Version, options), handshake.Token).ConfigureAwait(false);
            var welcome = await BusinessHostProtocol.ReadAsync<BusinessHostWelcome>(pipe, handshake.Token).ConfigureAwait(false);
            if (welcome.Type != "welcome" || welcome.Version != BusinessHostProtocol.Version) throw BusinessHostProtocol.Invalid();
            if (welcome.Error is { } failure) throw new AppException(failure.Code, failure.Message, failure.ExitCode);
            var registration = welcome.Registration ?? throw BusinessHostProtocol.Invalid();
            using var process = Process.GetCurrentProcess();
            if (registration.HostGeneration != owner.Generation || !BusinessHostProtocol.ValidId(registration.ContextId)
                || registration.Options != options || registration.ProcessId != process.Id
                || registration.ProcessStartTicks != process.StartTime.ToUniversalTime().Ticks) throw BusinessHostIdentity.Invalid();
            var peer = new BusinessHostPeer(pipe, registration.HostGeneration, registration.ContextId, requests, events);
            peer.Start();
            transferred = true;
            return new(settings, owner, registration, peer);
        }
        // 此方法尚未向调用方交付peer, 没有发送任何业务请求; 丢失welcome只需释放半成品context.
        // 仅传输关闭和明确排空可重试, 身份/协议/业务工厂错误仍立即失败.
        catch (IOException) { return null; }
        catch (AppException error) when (error.Code is "BUSINESS_HOST_STOPPING" or "BUSINESS_HOST_CONNECTION_LOST") { return null; }
        finally { if (!transferred) pipe.Dispose(); }
    }

    /// <summary>唯一 GUI 请求维护预约, 租约跨后台退出持有, 直到 GUI 完成或取消用户数据重置.</summary>
    public Task<BusinessHostMaintenanceLease> AcquireMaintenanceAsync(CancellationToken cancellationToken = default) =>
        BusinessHostMaintenance.AcquireAsync(settings, registration, Peer, cancellationToken);

    /// <summary>等待已认证的准确后台进程实际退出, 供重置确认日志等组合根资源已释放; PID 复用不误等新进程.</summary>
    public async Task WaitForHostExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != owner.StartTicks) return;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException) { /* 原准确进程已经退出. */ }
        catch (InvalidOperationException) { /* 原进程在获取句柄之前退出. */ }
        catch (System.ComponentModel.Win32Exception)
        { throw new AppException("BUSINESS_HOST_EXIT_UNVERIFIED", "无法核实共享后台是否已退出, 未执行数据重置", 3); }
    }

    /// <summary>幂等排空自己的 context, 不等待其他客户端或关闭共享后台.</summary>
    public ValueTask DisposeAsync()
    {
        lock (sync) return new(disposal ??= Peer.DetachAsync());
    }
}
