// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostIdentity.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以操作系统管道两端 PID、启动时间和准确安装路径认证共享后台通信.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>后台持有稳定租约时的发现记录, 本身不能代替管道身份或文件锁.</summary>
internal sealed record BusinessHostOwner(int ProcessId, long StartTicks, string Generation);

/// <summary>只授权已由 OS 实际核验的正式进程, 不接受进程名、客户端昵称或自报 PID.</summary>
internal static partial class BusinessHostIdentity
{
    /// <summary>生成当前后台的不可跨重启身份.</summary>
    internal static BusinessHostOwner Current()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"));
    }

    /// <summary>核验发现记录仍对应当前安装实际进程; 失败不能被当成已认证空闲.</summary>
    internal static bool IsLive(BusinessHostOwner owner, BusinessHostSettings settings) =>
        Matches(owner.ProcessId, owner.StartTicks, settings.HostExecutable);

    /// <summary>连接后核验实际服务端 PID、启动时间及准确 exe 路径.</summary>
    internal static void RequireServer(SafePipeHandle pipe, BusinessHostOwner owner, BusinessHostSettings settings)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid) || pid != owner.ProcessId || !IsLive(owner, settings)) throw Invalid();
    }

    /// <summary>按握手 kind 匹配实际正式 exe, 返回 OS 进程身份, 写能力由该可信启动入口冻结.</summary>
    internal static (int ProcessId, long StartTicks) RequireClient(SafePipeHandle pipe, BusinessHostClientOptions options, BusinessHostSettings settings)
    {
        ValidateOptions(options);
        if (!GetNamedPipeClientProcessId(pipe, out var pid)) throw Invalid();
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var expected = options.Kind == "gui" ? settings.GuiExecutable : settings.CliExecutable;
            var started = process.StartTime.ToUniversalTime().Ticks;
            if (!Matches(process.Id, started, expected)) throw Invalid();
            return (process.Id, started);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or OverflowException) { throw Invalid(); }
    }

    /// <summary>只允许真实产品入口与有限版本说明, GUI 权限不通过 MCP 请求模拟.</summary>
    internal static void ValidateOptions(BusinessHostClientOptions options)
    {
        if (options.Kind is not ("gui" or "mcp" or "cli") || options.WebViewVersion?.Length > 128
            || options.Kind != "gui" && (options.WebViewVersion is not null || options.ExternalToolTarget is not null) || options.Kind == "gui" && !options.AllowWrite)
            throw new AppException("BUSINESS_HOST_CLIENT_INVALID", "共享后台客户端能力无效", 3);
        options.ExternalToolTarget?.Validate();
    }

    /// <summary>读取实际模块和启动时间; 不可读取、已退出和 PID 复用一律拒绝.</summary>
    private static bool Matches(int pid, long started, string expected)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started
                && string.Equals(process.MainModule?.FileName, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>身份错误不包含原始路径、进程信息或异常正文.</summary>
    internal static AppException Invalid() => new("BUSINESS_HOST_IDENTITY_INVALID", "无法核验共享后台或客户端的安装身份", 3);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
