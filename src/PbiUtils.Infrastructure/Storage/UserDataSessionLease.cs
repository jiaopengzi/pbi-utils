// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Storage\UserDataSessionLease.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以进程生命周期句柄协调多宿主用户数据访问和完整重置, 不保存用户数据或跨线程持有互斥体.

using PbiUtils.Infrastructure.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Storage;

/// <summary>普通日志宿主共享会话, 重置独占同一根; 进程崩溃后操作系统自动释放文件句柄.</summary>
internal static class UserDataSessionLease
{
    /// <summary>固定临时协调文件仅含空内容; root 只用于计算身份, 不写入文件或日志.</summary>
    /// <param name="root">已规范化的宿主数据根.</param>
    /// <param name="exclusive">重置为 true, 正常宿主为 false.</param>
    /// <returns>调用者必须在日志释放后释放的会话句柄.</returns>
    /// <exception cref="AppException">另一个宿主或重置占用当前根, 不结束其他进程.</exception>
    internal static FileStream Acquire(string root, bool exclusive)
    {
        // 预约与新session准入必须原子互斥: 检查后释放再取session会让重置窗口被新日志宿主抢占.
        using var admission = exclusive ? null : OpenAdmission(root, reserve: false);
        var path = CoordinationPath(root, "session-");
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
        catch (IOException) { throw new AppException("USER_DATA_BUSY", "其他 pbi-utils 实例正在使用或重置用户数据, 请先正常关闭其他实例", 3); }
    }

    /// <summary>可信GUI跨后台排空及重置持有预约; 已有会话继续排空, 所有入口的新日志/session均被拒绝.</summary>
    /// <param name="root">宿主固定用户数据根.</param>
    /// <returns>只由维护拥有者在取消或重置结束后释放的外置句柄.</returns>
    internal static FileStream ReserveMaintenance(string root) => OpenAdmission(root, reserve: true);

    /// <summary>仅按实际独占句柄判断预约, 不以陈旧文件或元数据禁止启动.</summary>
    internal static bool IsMaintenanceReserved(string root)
    {
        try { using var probe = OpenAdmission(root, reserve: false); return false; }
        catch (AppException error) when (error.Code == "BUSINESS_HOST_MAINTENANCE") { return true; }
    }

    /// <summary>普通准入共享只读句柄, 维护排他句柄; 实际共享冲突之外的I/O失败保持失败.</summary>
    private static FileStream OpenAdmission(string root, bool reserve)
    {
        var path = CoordinationPath(root, "admission-");
        try
        {
            return new(path, FileMode.OpenOrCreate, reserve ? FileAccess.ReadWrite : FileAccess.Read,
                reserve ? FileShare.None : FileShare.Read);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new AppException("BUSINESS_HOST_MAINTENANCE", "本地数据正在维护, 暂不接受新的应用会话", 3); }
    }

    /// <summary>协调文件始终在用户数据树外, 路径只用于摘要且不写入记录.</summary>
    private static string CoordinationPath(string root, string prefix)
    {
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        var directory = RuntimeEnvironment.RuntimeCoordinationDirectory();
        UserDataStore.Check(directory);
        Directory.CreateDirectory(directory);
        UserDataStore.Check(directory);
        var path = Path.Combine(directory, prefix + identity + ".lock");
        UserDataStore.Check(path);
        return path;
    }
}
