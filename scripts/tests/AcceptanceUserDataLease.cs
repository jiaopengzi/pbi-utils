// FilePath    : pbi-utils\scripts\tests\AcceptanceUserDataLease.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅供测试使用的跨进程协调租约, 不替代产品用户数据锁.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PbiUtils.Testing;

/// <summary>协调同用户的验收进程, 不访问产品回执内容或修改产品权限.</summary>
public sealed class AcceptanceUserDataLease : IDisposable
{
    private readonly FileStream lease;
    /// <summary>默认等待最多五分钟, 让先前验收完成子进程排空和清理.</summary>
    public AcceptanceUserDataLease() : this(DefaultPath, TimeSpan.FromMinutes(5)) { }

    /// <summary>所有仓库副本共用当前用户临时目录下的协调文件.</summary>
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "pbi-utils-acceptance", "user-data.lock");

    /// <summary>等待取得独占测试租约; 超时或非竞争 I/O 错误不得静默继续.</summary>
    /// <param name="path">测试协调路径; 专项测试可以使用独占路径.</param>
    /// <param name="timeout">最长等待时间.</param>
    public AcceptanceUserDataLease(string path, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                // 只等待真实共享锁竞争, 目录/权限/磁盘错误不得伪装为其他测试繁忙.
                if (elapsed.Elapsed >= timeout) throw new TimeoutException("另一项验收仍在使用用户数据; 未启动本轮产品操作", error);
                var remaining = timeout - elapsed.Elapsed;
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, 50)));
            }
        }
    }

    /// <summary>释放租约, 保留协调文件以防删除重建造成两个锁身份.</summary>
    public void Dispose() => lease.Dispose();
}
