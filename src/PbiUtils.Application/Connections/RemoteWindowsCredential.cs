// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\RemoteWindowsCredential.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 持有宿主校验并授权的临时 Windows 凭据, 不序列化或持久化密码.

using System.Security;
using System.Runtime.InteropServices;
using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>仅在后端内存中使用的凭据; 调用者拥有并释放每个独立副本.</summary>
public sealed class RemoteWindowsCredential : IDisposable
{
    private readonly Lock sync = new();
    private readonly string userName;
    private readonly SecureString password;
    private bool disposed;

    /// <summary>校验 Windows 身份和非空密码, 复制密码以隔离调用者的释放.</summary>
    /// <param name="userName">域或服务器限定的用户身份, 也支持 UPN.</param>
    /// <param name="password">本次授权输入的密码, 不允许空密码远端登录.</param>
    /// <exception cref="AppException">身份格式或密码不合法.</exception>
    public RemoteWindowsCredential(string userName, SecureString password)
    {
        var name = NormalizeUserName(userName);
        if (password is null || password.Length is < 1 or > 1024)
            throw new AppException("INVALID_WINDOWS_PASSWORD", "指定 Windows 账号必须提供非空密码; 空密码本地账号无法用于默认策略下的远端登录");
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            for (var index = 0; index < password.Length; index++)
                if (Marshal.ReadInt16(pointer, index * 2) == 0) throw new AppException("INVALID_WINDOWS_PASSWORD", "Windows 密码不能包含空字符");
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
        this.userName = name;
        this.password = password.Copy();
        this.password.MakeReadOnly();
    }

    /// <summary>验证并规范化目标服务器或可信域的用户身份, 不读取密码或联网.</summary>
    /// <param name="userName">服务器限定、域限定或 UPN 身份.</param>
    /// <returns>已去除首尾空白的身份.</returns>
    public static string NormalizeUserName(string userName)
    {
        var name = userName?.Trim() ?? "";
        if (name.Length is < 1 or > 256 || name.Any(c => char.IsControl(c) || c is ';' or '=')
            || !(name.Split('\\') is [{ Length: > 0 }, { Length: > 0 }] || name.Split('@') is [{ Length: > 0 }, { Length: > 0 }]))
            throw new AppException("INVALID_WINDOWS_ACCOUNT", "Windows 账号需为 目标服务器名\\用户、域\\用户 或 用户@域");
        return name;
    }

    /// <summary>创建连接目标独立拥有的副本; 已释放的凭据不能再使用.</summary>
    /// <returns>需要由调用者释放的独立凭据.</returns>
    public RemoteWindowsCredential Copy() => Use((name, secret) => new RemoteWindowsCredential(name, secret));

    /// <summary>在受控后端回调内读取凭据, 回调期间不能被并发释放.</summary>
    /// <param name="action">仅供 SDK 建立连接使用的回调, 不得记录或持久化参数.</param>
    /// <returns>后端回调的返回值.</returns>
    public T Use<T>(Func<string, SecureString, T> action)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return action(userName, password);
        }
    }

    /// <summary>对象显示不包含账号或密码.</summary>
    public override string ToString() => "Native Windows credential";

    /// <summary>释放本对象拥有的密码副本; 重复释放安全.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            password.Dispose();
        }
    }
}
