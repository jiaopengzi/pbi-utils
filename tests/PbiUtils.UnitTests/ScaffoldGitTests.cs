// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldGitTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Git 能力及有界进程的真实输出排空, 超时和取消清理.

using System.Diagnostics;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>测试仅在隔离目录启动自有进程, 不修改用户 Git 配置.</summary>
public sealed class ScaffoldGitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-git-" + Guid.NewGuid().ToString("N"));

    /// <summary>未安装 Git 返回固定原因, 不泄露可执行文件位置.</summary>
    [Fact]
    public async Task MissingExecutableIsUnavailableWithSafeReason()
    {
        var result = await new ScaffoldGit(Path.Combine(root, "missing-git.exe")).ProbeAsync(default);
        Assert.False(result.Available);
        Assert.Equal("GIT_NOT_FOUND", result.Reason);
    }

    /// <summary>成功退出但不是 Git 的版本输出不能被误判为可用.</summary>
    [Fact]
    public async Task NonGitVersionOutputFailsProbe()
    {
        var result = await new ScaffoldGit("pwsh").ProbeAsync(default);
        Assert.False(result.Available);
        Assert.Equal("GIT_PROBE_FAILED", result.Reason);
    }

    /// <summary>已取消的探测不启动程序, 取消仍作为取消返回.</summary>
    [Fact]
    public async Task CancelledProbeDoesNotBecomeMissingGit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScaffoldGit().ProbeAsync(cancellation.Token));
    }

    /// <summary>真实 Git 仅初始化空仓库, 禁用用户模板及 hook, 不暂存或提交文件.</summary>
    [Fact]
    public async Task RealGitProbeAndInitializationKeepWorkingTreeUnstaged()
    {
        Directory.CreateDirectory(root);
        var git = new ScaffoldGit();
        Assert.True((await git.ProbeAsync(default)).Available);
        await File.WriteAllTextAsync(Path.Combine(root, "project.txt"), "keep");
        Assert.True(await git.InitializeAsync(root, default));
        Assert.True(File.Exists(Path.Combine(root, ".git", "HEAD")));
        Assert.False(File.Exists(Path.Combine(root, ".git", "index")));
        Assert.False(Directory.Exists(Path.Combine(root, ".git", "hooks")));
        Assert.False(Directory.Exists(Path.Combine(root, ".git", "refs", "heads", "main")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(root, "project.txt")));
    }

    /// <summary>两个管道同时超过缓冲区容量仍可退出, 只保留有界标准输出.</summary>
    [Fact]
    public async Task ProcessDrainsLargeOutputAndReturnsNonzeroExit()
    {
        var result = await GitProcess.RunAsync(PowerShell("$text = 'x' * 8192; 1..32 | ForEach-Object { [Console]::Out.Write($text); [Console]::Error.Write($text) }; exit 7"), TimeSpan.FromSeconds(15), default);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(4096, result.StandardOutput.Length);
        Assert.All(result.StandardOutput, character => Assert.Equal('x', character));
    }

    /// <summary>超时后等待自有进程退出, 不留下后台循环或管道任务.</summary>
    [Fact]
    public async Task TimedOutProcessIsTerminatedBeforeReturning()
    {
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "timeout.pid");
        await Assert.ThrowsAsync<TimeoutException>(() => GitProcess.RunAsync(SleepingProcess(pidFile), TimeSpan.FromSeconds(2), default));
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    /// <summary>运行中取消先排空并结束准确 PID, 再向调用者传播取消.</summary>
    [Fact]
    public async Task CancelledProcessIsTerminatedBeforeReturning()
    {
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "cancel.pid");
        using var cancellation = new CancellationTokenSource();
        var running = GitProcess.RunAsync(SleepingProcess(pidFile), TimeSpan.FromSeconds(20), cancellation.Token);
        for (var attempt = 0; !File.Exists(pidFile) && attempt < 200; attempt++) await Task.Delay(25);
        Assert.True(File.Exists(pidFile));
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    /// <summary>宿主固定参数启动测试脚本, 不依赖进程外的 shell 执行.</summary>
    private static ProcessStartInfo PowerShell(string script) => new("pwsh")
    {
        ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    /// <summary>写出准确 PID 后休眠, 供超时和取消测试核实自有进程终止.</summary>
    private static ProcessStartInfo SleepingProcess(string pidFile) => PowerShell("[IO.File]::WriteAllText('" + pidFile.Replace("'", "''", StringComparison.Ordinal) + "', $PID.ToString()); Start-Sleep -Seconds 60");

    /// <summary>测试结束仅删除本实例创建的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
