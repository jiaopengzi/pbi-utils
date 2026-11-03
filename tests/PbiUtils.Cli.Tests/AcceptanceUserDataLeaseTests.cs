// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\AcceptanceUserDataLeaseTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证测试协调租约的独占、超时和跨进程释放, 不访问真实用户数据.

using System.Diagnostics;
using PbiUtils.Testing;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>使用独占临时锁路径验证实际文件句柄, 不与产品验收争用.</summary>
public sealed class AcceptanceUserDataLeaseTests
{
    /// <summary>持有者阻止第二次进入, 释放后同一路径可再用; 超时不能变成无锁成功.</summary>
    [Fact]
    public void ContentionTimesOutAndReleaseAllowsNextOwner()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbi-lease-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new AcceptanceUserDataLease(path, TimeSpan.Zero))
                Assert.Throws<TimeoutException>(() => new AcceptanceUserDataLease(path, TimeSpan.FromMilliseconds(80)));
            using var next = new AcceptanceUserDataLease(path, TimeSpan.Zero);
        }
        finally { File.Delete(path); }
    }

    /// <summary>PowerShell 子进程与测试运行器竞争相同文件, 父级释放后才能进入.</summary>
    [Fact]
    public async Task PowerShellProcessWaitsForOwnerAndThenAcquires()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbi-lease-test-" + Guid.NewGuid().ToString("N"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "run.ps1"))) directory = directory.Parent;
        var source = Path.Combine(directory!.FullName, "scripts", "tests", "AcceptanceUserDataLease.cs");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PBI_TEST_LEASE_SOURCE"] = source;
        start.Environment["PBI_TEST_LEASE_PATH"] = path;
        foreach (var argument in new[] { "-NoProfile", "-Command", "Add-Type -Path $env:PBI_TEST_LEASE_SOURCE; Write-Output 'ready'; $lease = [PbiUtils.Testing.AcceptanceUserDataLease]::new($env:PBI_TEST_LEASE_PATH, [TimeSpan]::FromSeconds(10)); try { Write-Output 'acquired' } finally { $lease.Dispose() }" }) start.ArgumentList.Add(argument);
        using var owner = new AcceptanceUserDataLease(path, TimeSpan.Zero);
        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var acquired = child.StandardOutput.ReadLineAsync();
            Assert.NotSame(acquired, await Task.WhenAny(acquired, Task.Delay(150)));
            owner.Dispose();
            Assert.Equal("acquired", await acquired.WaitAsync(TimeSpan.FromSeconds(15)));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode);
            Assert.Equal("", await errors);
        }
        finally
        {
            owner.Dispose();
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
            File.Delete(path);
        }
    }
}
