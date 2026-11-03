// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostSettings.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 由组合根固定后台协调目录、正式执行路径和受限启动参数.

using PbiUtils.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>平台配置不从管道请求接收; 内部构造用于隔离真实文件锁和 OS 身份验证.</summary>
internal sealed class BusinessHostSettings
{
    internal string UserRoot { get; }
    internal string Installation { get; }
    internal string CoordinationDirectory { get; }
    internal string PipeName { get; }
    internal string HostExecutable { get; set; }
    internal string GuiExecutable { get; set; }
    internal string CliExecutable { get; set; }
    internal TimeSpan BootstrapTimeout { get; set; } = TimeSpan.FromSeconds(45);
    internal Func<CancellationToken, Task>? Launch { get; set; }

    /// <summary>按用户数据根与安装摘要隔离后台发现; 不将原始业务路径写入协调文件名.</summary>
    internal BusinessHostSettings(string userRoot, string installation, string? coordinationDirectory = null)
    {
        UserRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userRoot));
        Installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(UserRoot.ToUpperInvariant() + "\n" + Installation.ToUpperInvariant())));
        CoordinationDirectory = coordinationDirectory ?? Path.Combine(RuntimeEnvironment.RuntimeCoordinationDirectory(), "host-" + identity);
        PipeName = "pbi-utils-business-" + identity;
        HostExecutable = Path.Combine(Installation, "PbiUtils.Host.exe");
        GuiExecutable = Path.Combine(Installation, "PbiUtils.Desktop.exe");
        CliExecutable = Path.Combine(Installation, "pbi-utils.exe");
    }

    /// <summary>创建当前安装固定配置, 不接受调用方指定 exe 或任意命令行.</summary>
    internal static BusinessHostSettings Current(string root) => new(root, AppContext.BaseDirectory);

    /// <summary>经 Windows 隐藏启动准确安装副本, 不继承 MCP 的标准流句柄或让启动客户端拥有后台终止权.</summary>
    /// <param name="token">启动前取消检查; 已启动的共享后台不归本客户端终止.</param>
    /// <returns>启动请求完成; 缺少固定后台或系统启动失败返回明确错误.</returns>
    internal async Task LaunchAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Launch is not null) { await Launch(token).ConfigureAwait(false); return; }
        if (!File.Exists(HostExecutable)) throw new AppException("BUSINESS_HOST_REQUIRED", "当前安装缺少共享业务后台, 请使用完整发布包", 3);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(HostExecutable)
            {
                // 只执行安装目录中固定的无参数 EXE, 不构造 shell 命令; Windows shell 隔离父客户端 stdio.
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Installation
            }) ?? throw new AppException("BUSINESS_HOST_START_FAILED", "无法启动共享业务后台", 3);
        }
        catch (System.ComponentModel.Win32Exception) { throw new AppException("BUSINESS_HOST_START_FAILED", "无法启动共享业务后台", 3); }
    }
}
