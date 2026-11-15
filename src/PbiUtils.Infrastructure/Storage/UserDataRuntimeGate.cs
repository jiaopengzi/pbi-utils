// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Storage\UserDataRuntimeGate.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 协调 runtime 锁文件的打开与卸载删除, 数据根移除时仍有同用户跨进程保护.

using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Storage;

/// <summary>命名互斥锁只保护同步文件打开或卸载临界区, 不得跨线程或 await 持有.</summary>
internal sealed class UserDataRuntimeGate : IDisposable
{
    private readonly Mutex mutex;

    private UserDataRuntimeGate(Mutex mutex) => this.mutex = mutex;

    /// <summary>进入同用户各 Windows 会话共享的数据根临界区, 并防止占用时无限等待.</summary>
    /// <param name="root">用于共同租约的本地用户数据根.</param>
    /// <returns>调用者须在取得互斥锁的线程释放句柄.</returns>
    /// <exception cref="AppException">数据根无法及时进入维护临界区.</exception>
    internal static UserDataRuntimeGate Enter(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutex = new Mutex(false, "pbi-utils-user-data-" + identity,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                    throw new AppException("BUSINESS_HOST_MAINTENANCE", "本地数据正在维护, 暂不接受新的应用会话", 3);
            }
            // 上一线程异常退出时系统已把互斥锁交给当前线程; 本轮仍会重新核验文件.
            catch (AbandonedMutexException) { }
            return new(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    /// <summary>释放当前同步区间, 让其他进程重新尝试进入数据根.</summary>
    public void Dispose()
    {
        try { mutex.ReleaseMutex(); }
        finally { mutex.Dispose(); }
    }
}
