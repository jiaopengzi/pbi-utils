// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostMaintenance.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 由准确 GUI 持有外置维护租约, 跨后台排空退出阻止自动重启抢占用户数据重置.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>仅当前 GUI 拥有维护句柄; 必须在浏览器/日志释放和重置完成或取消后释放.</summary>
public sealed class BusinessHostMaintenanceLease : IAsyncDisposable, IDisposable
{
    private readonly FileStream handle;
    private readonly FileStream dataAdmission;
    private int disposed;
    internal BusinessHostMaintenanceLease(FileStream handle, FileStream dataAdmission)
    { this.handle = handle; this.dataAdmission = dataAdmission; }
    /// <summary>释放维护预约, 不删除稳定锁文件或自动重启任何进程.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { handle.Dispose(); }
        finally { dataAdmission.Dispose(); }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

internal sealed record BusinessHostMaintenanceRecord(string ContextId, string HostGeneration, int ProcessId, long ProcessStartTicks, string Nonce);

/// <summary>锁在 GUI 进程, 许可核验在后台, 所有新注册与启动都遵守同一真实文件锁.</summary>
internal static class BusinessHostMaintenance
{
    /// <summary>先预约再原子核验唯一 GUI, 失败或取消释放预约且不结束其他客户端.</summary>
    internal static async Task<BusinessHostMaintenanceLease> AcquireAsync(BusinessHostSettings settings,
        BusinessHostRegistration registration, BusinessHostPeer peer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (registration.Options.Kind != "gui") throw new AppException("BUSINESS_HOST_MAINTENANCE_DENIED", "只有可信桌面窗口可以维护本地数据", 3);
        var handle = BusinessHostOwnership.TryAcquireMaintenance(settings) ?? throw Busy();
        FileStream? dataAdmission = null;
        try
        {
            dataAdmission = UserDataSessionLease.ReserveMaintenance(settings.UserRoot);
            var record = new BusinessHostMaintenanceRecord(registration.ContextId, registration.HostGeneration,
                registration.ProcessId, registration.ProcessStartTicks, Guid.NewGuid().ToString("N"));
            handle.SetLength(0);
            JsonSerializer.Serialize(handle, record, BusinessHostProtocol.Json);
            handle.Flush(true);
            var result = await peer.InvokeAsync("$host.maintenance", JsonSerializer.SerializeToElement(new { nonce = record.Nonce }), token).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("acquired", out var acquired) || acquired.ValueKind != JsonValueKind.True)
                throw BusinessHostProtocol.Invalid();
            token.ThrowIfCancellationRequested();
            return new(handle, dataAdmission);
        }
        catch
        {
            try { handle.Dispose(); }
            finally { dataAdmission?.Dispose(); }
            throw;
        }
    }

    /// <summary>在服务端注册门闩内核对真实维护文件和准确 GUI 进程/上下文, nonce 不单独授权.</summary>
    internal static void Verify(BusinessHostSettings settings, BusinessHostRegistration registration, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1
            || !parameters.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String
            || !BusinessHostProtocol.ValidId(nonce.GetString()) || !BusinessHostOwnership.IsMaintenanceHeld(settings)
            || !UserDataSessionLease.IsMaintenanceReserved(settings.UserRoot)) throw Busy();
        try
        {
            using var stream = new FileStream(Path.Combine(settings.CoordinationDirectory, "maintenance.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is <= 0 or > 4096) throw Busy();
            var record = JsonSerializer.Deserialize<BusinessHostMaintenanceRecord>(stream, BusinessHostProtocol.Json);
            if (record is null || record.ContextId != registration.ContextId || record.HostGeneration != registration.HostGeneration
                || record.ProcessId != registration.ProcessId || record.ProcessStartTicks != registration.ProcessStartTicks
                || record.Nonce != nonce.GetString()) throw Busy();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { throw Busy(); }
    }

    internal static AppException Busy() => new("BUSINESS_HOST_MAINTENANCE", "本地数据维护中或仍有其他客户端, 请先正常关闭其他客户端", 3);
}
