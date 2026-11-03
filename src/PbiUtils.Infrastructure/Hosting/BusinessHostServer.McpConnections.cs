// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostServer.McpConnections.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享后台权威管理 MCP 准入、准确客户端身份和排空状态, 不提供进程终止代理.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

public sealed partial class BusinessHostServer
{
    private readonly Dictionary<string, McpConnection> mcpConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BusinessHostPeer> guiConnections = new(StringComparer.Ordinal);

    /// <summary>在注册门闩中固定已认证身份与进程句柄, 防止退出后 PID 复用改变目标.</summary>
    private void RegisterManagement(BusinessHostRegistration registration, BusinessHostPeer peer)
    {
        if (registration.Options.Kind == "gui") { guiConnections.Add(registration.ContextId, peer); return; }
        if (registration.Options.Kind != "mcp") return;
        // 没有 GUI 轮询时也回收上一批已退出的准确进程句柄, 避免长连同伴下短连接无限累积.
        PruneExitedConnections();
        Process? process = null;
        try
        {
            process = Process.GetProcessById(registration.ProcessId);
            _ = process.SafeHandle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != registration.ProcessStartTicks
                || !string.Equals(process.MainModule?.FileName, Path.GetFullPath(settings.CliExecutable), StringComparison.OrdinalIgnoreCase))
                throw BusinessHostIdentity.Invalid();
            mcpConnections.Add(registration.ContextId, new(registration, peer, process));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or AppException)
        { process?.Dispose(); throw BusinessHostIdentity.Invalid(); }
    }

    /// <summary>仅仍在接收请求的认证 GUI 有管理权, 保存过的 facade 在断开后失效.</summary>
    private void RequireManager(string guiId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closing || !guiConnections.TryGetValue(guiId, out var peer) || !peer.IsAccepting)
            throw new AppException("MCP_MANAGEMENT_UNAVAILABLE", "当前桌面连接已失效, 无法管理 MCP", 3);
    }

    /// <summary>状态先变为排空并关新请求门闩; 业务取消在 Host 注册锁外通知.</summary>
    private BusinessHostPeer? MarkDraining(string connectionId)
    {
        if (!mcpConnections.TryGetValue(connectionId, out var entry) || entry.State != "connected") return null;
        entry.State = "draining";
        entry.Peer.StopAccepting();
        return entry.Peer;
    }

    /// <summary>快照仅移除已核实退出的精确句柄, 不能把 IPC 关闭或未知读取当作进程释放.</summary>
    private void PruneExitedConnections()
    {
        foreach (var entry in mcpConnections.Values.ToArray())
        {
            try
            {
                if (entry.State != "exiting" || !entry.Process.HasExited) continue;
                mcpConnections.Remove(entry.Registration.ContextId);
                entry.Process.Dispose();
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            { throw new AppException("MCP_EXIT_UNVERIFIED", "无法核实 MCP 客户端是否已退出", 3); }
        }
    }

    /// <summary>返回当前安全身份与生命周期, 不把读取进程失败当作已经退出.</summary>
    private McpConnectionsSnapshot Snapshot()
    {
        PruneExitedConnections();
        return new(acceptingMcp, mcpConnections.Values.Select(entry => new McpConnectionInfo(
            entry.Registration.ContextId, entry.Registration.ProcessId,
            new DateTimeOffset(entry.Registration.ProcessStartTicks, TimeSpan.Zero), entry.ConnectedAt,
            entry.ClientName, entry.ClientVersion, entry.Registration.Options.AllowWrite, entry.State, entry.Peer.ActiveRequests)).ToArray());
    }

    /// <summary>检查调用者后获取权威快照, 不读取其他上下文的业务状态.</summary>
    private Task<McpConnectionsSnapshot> ListMcpAsync(string guiId, CancellationToken token)
    {
        lock (sync) { RequireManager(guiId, token); return Task.FromResult(Snapshot()); }
    }

    /// <summary>N32 身份只命中本 Host 的 MCP; 重复请求幂等, 不接受 PID 或外部进程标识.</summary>
    private Task<McpDisconnectResult> DisconnectMcpAsync(string guiId, string connectionId, CancellationToken token)
    {
        BusinessHostPeer? peer;
        bool accepted;
        lock (sync)
        {
            RequireManager(guiId, token);
            if (!BusinessHostProtocol.ValidId(connectionId)) throw new AppException("INVALID_ARGUMENT", "MCP 连接标识无效");
            accepted = mcpConnections.ContainsKey(connectionId);
            peer = MarkDraining(connectionId);
        }
        peer?.RequestShutdown();
        return Task.FromResult(new McpDisconnectResult(accepted));
    }

    /// <summary>持久化先于准入切换, 与注册共用门闩; 取消提交后不撤回排空意图.</summary>
    private Task<McpConnectionsSnapshot> SetMcpAdmissionAsync(string guiId, bool accepting, CancellationToken token)
    {
        BusinessHostPeer[] draining;
        lock (sync)
        {
            RequireManager(guiId, token);
            BusinessHostMcpAdmission.Write(settings, accepting);
            acceptingMcp = accepting;
            draining = accepting ? [] : [.. mcpConnections.Keys.Select(MarkDraining).OfType<BusinessHostPeer>()];
        }
        foreach (var peer in draining) peer.RequestShutdown();
        lock (sync) return Task.FromResult(Snapshot());
    }

    /// <summary>SDK 元数据仅可修改当前 MCP 的展示字段, 无权限、PID、目标或管理参数.</summary>
    private Task<JsonElement> UpdateClientInfoAsync(BusinessHostRegistration registration, JsonElement parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (registration.Options.Kind != "mcp") throw new AppException("BUSINESS_HOST_METHOD_NOT_ALLOWED", "此连接不接受 MCP 元数据", 3);
        if (parameters.ValueKind != JsonValueKind.Object) throw BusinessHostProtocol.Invalid();
        var values = parameters.EnumerateObject().ToArray();
        if (values.Length != 2 || values.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || !parameters.TryGetProperty("clientName", out var name) || !parameters.TryGetProperty("clientVersion", out var version)) throw BusinessHostProtocol.Invalid();
        var clientName = ReadClientText(name);
        var clientVersion = ReadClientText(version);
        lock (sync)
        {
            if (!mcpConnections.TryGetValue(registration.ContextId, out var entry) || entry.State != "connected")
                throw new AppException("BUSINESS_HOST_STOPPING", "当前 MCP 连接正在排空", 3);
            entry.ClientName = clientName;
            entry.ClientVersion = clientVersion;
        }
        return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
    }

    /// <summary>严格预算的不可信纯文本元数据; 非字符串、控制字符和超长内容明确拒绝.</summary>
    private static string? ReadClientText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw BusinessHostProtocol.Invalid();
        var text = value.GetString()!;
        if (text.Length > 128 || text.Any(char.IsControl)) throw BusinessHostProtocol.Invalid();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>保存认证注册、精确句柄和传输 owner; 展示字段不参与身份或写权限判定.</summary>
    private sealed class McpConnection(BusinessHostRegistration registration, BusinessHostPeer peer, Process process)
    {
        internal BusinessHostRegistration Registration { get; } = registration;
        internal BusinessHostPeer Peer { get; } = peer;
        internal Process Process { get; } = process;
        internal DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        internal string State { get; set; } = "connected";
        internal string? ClientName { get; set; }
        internal string? ClientVersion { get; set; }
    }

    /// <summary>不可通过管道参数创建的 GUI 专属能力, 每次调用重新验证原上下文仍有效.</summary>
    private sealed class McpController(BusinessHostServer owner, string guiId) : IBusinessHostMcpConnections
    {
        public Task<McpConnectionsSnapshot> ListAsync(CancellationToken token = default) => owner.ListMcpAsync(guiId, token);
        public Task<McpDisconnectResult> DisconnectAsync(string connectionId, CancellationToken token = default) => owner.DisconnectMcpAsync(guiId, connectionId, token);
        public Task<McpConnectionsSnapshot> SetAdmissionAsync(bool acceptingConnections, CancellationToken token = default) => owner.SetMcpAdmissionAsync(guiId, acceptingConnections, token);
    }
}
