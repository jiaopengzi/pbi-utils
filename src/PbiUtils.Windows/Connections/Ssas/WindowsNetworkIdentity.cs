// FilePath    : pbi-utils\src\PbiUtils.Windows\Connections\Ssas\WindowsNetworkIdentity.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在明确的 Windows 出站凭据上下文内运行同步 SSAS SDK, 不把密码放入连接串.

using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Application;

namespace PbiUtils.Windows.Connections.Ssas;

/// <summary>指定身份只改变本次调用的出站凭据, 不切换 UI 进程或保存登录令牌.</summary>
public static partial class WindowsNetworkIdentity
{
    /// <summary>将完整 SDK 连接、读取和释放包在原生网络身份内, 结束后恢复调用方身份.</summary>
    /// <param name="credential">原生窗口或当前用户 DPAPI 恢复的凭据; null 使用当前身份.</param>
    /// <param name="logger">已有宿主的日志工具.</param>
    /// <param name="action">同步只读 SDK 操作, 不应跨出此范围保留连接或身份.</param>
    /// <returns>只读操作的结果; 创建上下文不代表目标服务器已认证成功.</returns>
    public static T Run<T>(RemoteWindowsCredential? credential, ILogger logger, Func<T> action)
    {
        if (credential is null) return action();
        using var operation = BusinessLog.Begin(logger, "PowerBi.WindowsNetworkIdentity");
        try
        {
            using var token = credential.Use((name, password) =>
            {
                var parts = name.Split('\\');
                var user = parts.Length == 2 ? parts[1] : name;
                var domain = parts.Length == 2 ? parts[0] : null;
                var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
                try
                {
                    BusinessLog.WindowsNetworkPreparing(logger);
                    // NEW_CREDENTIALS 只准备出站身份, 目标服务器将在 SSPI 握手中验证账号密码.
                    if (!LogonUserW(user, domain, pointer, 9, 3, out var handle))
                    {
                        var error = Marshal.GetLastWin32Error(); handle?.Dispose();
                        throw new Win32Exception(error);
                    }
                    return handle;
                }
                finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
            });
            T result;
            try
            {
                result = WindowsIdentity.RunImpersonated(token, () =>
            {
                BusinessLog.WindowsNetworkContext(logger, ReadImpersonationLevel);
                return action();
            });
            }
            finally { BusinessLog.WindowsNetworkStage(logger, "CallerContextRestored"); }
            operation.Complete();
            return result;
        }
        catch (Exception error)
        {
            operation.Fail(error);
            if (error is PbiUtils.Core.AppException or OperationCanceledException) throw;
            SsasConnection.LogFailure(logger, "WindowsOutboundContext", error);
            throw SsasConnection.Failure("Windows 网络身份上下文", error);
        }
    }

    /// <summary>仅由启用 Debug 的日志封装调用, 读取完身份级别立即释放身份对象.</summary>
    private static TokenImpersonationLevel? ReadImpersonationLevel()
    {
        using var identity = WindowsIdentity.GetCurrent(true);
        return identity?.ImpersonationLevel;
    }

    /// <summary>使用 Windows 标准提供方创建独立网络登录上下文, 密码参数指向有界临时内存.</summary>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUserW(string userName, string? domain, IntPtr password, int logonType, int provider, out SafeAccessTokenHandle token);
}
