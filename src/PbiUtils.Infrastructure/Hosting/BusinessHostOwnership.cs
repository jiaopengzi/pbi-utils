// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostOwnership.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享后台的稳定单例租约和短时启动选主, 不排斥 GUI 或多个 MCP 客户端.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>只有真实后台持有 lifetime 锁; 必须在所有客户端业务和资源排空后释放.</summary>
internal sealed class BusinessHostOwnership : IDisposable
{
    private readonly FileStream handle;
    internal BusinessHostOwner Owner { get; }

    private BusinessHostOwnership(FileStream handle, BusinessHostOwner owner) { this.handle = handle; Owner = owner; }

    /// <summary>先独占身份发布再取得后台单例, 写穿原子元数据; 不复用由启动客户端持有的start锁.</summary>
    internal static async Task<BusinessHostOwnership> AcquireAsync(BusinessHostSettings settings, CancellationToken token = default)
    {
        FileStream? publication;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while ((publication = TryAcquireFile(settings, "publish.lock")) is null)
            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
        using var publishing = publication;
        token.ThrowIfCancellationRequested();
        var stream = TryAcquireFile(settings, "host.lock")
            ?? throw new AppException("BUSINESS_HOST_ALREADY_RUNNING", "共享业务后台正在运行或排空", 3);
        try
        {
            var owner = BusinessHostIdentity.Current();
            UserDataStore.Write(Path.Combine(settings.CoordinationDirectory, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(owner, BusinessHostProtocol.Json));
            return new(stream, owner);
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>获取短时启动选主, 竞争只返回 null; 权限和磁盘错误明确失败.</summary>
    internal static FileStream? TryAcquireStartup(BusinessHostSettings settings) => TryAcquireFile(settings, "start.lock");

    /// <summary>维护租约与用户数据根分离, GUI 关闭后台后仍能阻止重新启动.</summary>
    internal static FileStream? TryAcquireMaintenance(BusinessHostSettings settings) => TryAcquireFile(settings, "maintenance.lock");

    /// <summary>只读探测彼此共享, 仅与真实维护写句柄互斥; 不依赖陈旧元数据或将另一探测误判为维护.</summary>
    internal static bool IsMaintenanceHeld(BusinessHostSettings settings)
    {
        using var probe = TryAcquireFile(settings, "maintenance.lock", FileAccess.Read);
        return probe is null;
    }

    /// <summary>只有 lifetime 文件实际被持有才读取元数据, 陈旧文件不阻止新启动也不授予权限.</summary>
    internal static BusinessHostOwner? Read(BusinessHostSettings settings)
    {
        using var publishing = TryAcquireFile(settings, "publish.lock")
            ?? throw new AppException("BUSINESS_HOST_STARTING", "共享后台正在发布启动身份", 3);
        using (var probe = TryAcquireFile(settings, "host.lock")) if (probe is not null) return null;
        try
        {
            var path = Path.Combine(settings.CoordinationDirectory, "owner.json");
            UserDataStore.Check(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 4096) throw BusinessHostIdentity.Invalid();
            var owner = JsonSerializer.Deserialize<BusinessHostOwner>(stream, BusinessHostProtocol.Json);
            if (owner is null || !BusinessHostProtocol.ValidId(owner.Generation) || !BusinessHostIdentity.IsLive(owner, settings)) throw BusinessHostIdentity.Invalid();
            return owner;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or AppException)
        {
            using var probe = TryAcquireFile(settings, "host.lock");
            if (probe is not null) return null;
            throw BusinessHostIdentity.Invalid();
        }
    }

    /// <summary>稳定文件不删除; 只有明确共享冲突表示占用, 其他错误不能当成空闲.</summary>
    /// <param name="settings">当前安装固定的协调目录.</param>
    /// <param name="name">内部固定锁文件名.</param>
    /// <param name="access">真实租约默认读写, 维护探测只读且彼此兼容.</param>
    /// <returns>调用方负责释放的句柄, 共享冲突返回 null; 其他 I/O 或权限失败抛出明确错误.</returns>
    private static FileStream? TryAcquireFile(BusinessHostSettings settings, string name, FileAccess access = FileAccess.ReadWrite)
    {
        try
        {
            UserDataStore.Check(settings.CoordinationDirectory);
            Directory.CreateDirectory(settings.CoordinationDirectory);
            var path = Path.Combine(settings.CoordinationDirectory, name);
            UserDataStore.Check(path);
            return new(path, FileMode.OpenOrCreate, access, FileShare.Read);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("BUSINESS_HOST_LOCK_FAILED", "无法访问共享后台协调租约", 3); }
    }

    /// <summary>仅释放自身文件句柄, 不删除稳定发现文件或操作其他进程.</summary>
    public void Dispose() => handle.Dispose();
}
