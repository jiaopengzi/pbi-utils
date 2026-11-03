// FilePath    : pbi-utils\scripts\tests\ScriptInstallerTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在开发与终端 PowerShell 中验证安装依赖和文件事务行为, 不执行真实系统安装.

using System.Diagnostics;
using Xunit;

namespace PbiUtils.ScriptTests;

public sealed partial class ScriptTests
{
    /// <summary>使用两个真实解释器运行隔离安装测试, 确认退出码与实际完成标记, 不改机器执行策略.</summary>
    /// <param name="shell">开发 PowerShell 或 Windows 自带 PowerShell.</param>
    /// <param name="script">只操作隔离目录、唯一测试注册键和模拟外部适配的行为测试.</param>
    /// <param name="completed">成功终态标记, 防止脚本未执行即返回成功.</param>
    /// <param name="scenario">可选隔离场景, 将耗时的打包保护与许可刷新分别执行并保留单项超时边界.</param>
    [Theory]
    [InlineData("pwsh", "installer-worker-tests.ps1", "PASS installer prerequisite worker", null)]
    [InlineData("powershell.exe", "installer-worker-tests.ps1", "PASS installer prerequisite worker", null)]
    [InlineData("pwsh", "installer-gate-tests.ps1", "PASS 安装阶段在完整载荷释放前执行只读门禁", null)]
    [InlineData("powershell.exe", "installer-gate-tests.ps1", "PASS 安装阶段在完整载荷释放前执行只读门禁", null)]
    [InlineData("pwsh", "installer-prerequisites-tests.ps1", "Installer prerequisites tests passed:", null)]
    [InlineData("powershell.exe", "installer-prerequisites-tests.ps1", "Installer prerequisites tests passed:", null)]
    [InlineData("pwsh", "installer-application-tests.ps1", "PASS installer application transactions, upgrades, rollback, ownership and leases", null)]
    [InlineData("powershell.exe", "installer-application-tests.ps1", "PASS installer application transactions, upgrades, rollback, ownership and leases", null)]
    [InlineData("pwsh", "installer-shell-tests.ps1", "PASS installer shell conflicts, ownership and rollback", null)]
    [InlineData("powershell.exe", "installer-shell-tests.ps1", "PASS installer shell conflicts, ownership and rollback", null)]
    [InlineData("pwsh", "installer-external-tools-tests.ps1", "PASS external tool registration, ownership, rollback and privilege boundaries", null)]
    [InlineData("powershell.exe", "installer-external-tools-tests.ps1", "PASS external tool registration, ownership, rollback and privilege boundaries", null)]
    [InlineData("pwsh", "installer-package-tests.ps1", "PASS installer package snapshot identity and SDK boundaries", "Package")]
    [InlineData("pwsh", "installer-package-tests.ps1", "PASS installer compiler failure, lock release and cleanup failure preservation", "PackageFailure")]
    [InlineData("pwsh", "installer-package-tests.ps1", "PASS installer current license refresh and source isolation", "LicenseRefresh")]
    [InlineData("pwsh", "installer-package-tests.ps1", "PASS installer license input drift, missing sources and concurrent edits rejected", "LicenseRefreshFailure")]
    [InlineData("pwsh", "license-bundle-tests.ps1", "License bundle tests passed.", null)]
    [InlineData("pwsh", "license-bundle-validation-tests.ps1", "License bundle validation tests passed.", null)]
    public async Task InstallerBehaviorInBothPowerShells(string shell, string script, string completed, string? scenario)
    {
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // 与 NSIS 相同: 不让开发宿主的 PS7 模块路径污染系统 PS5.1, 只改变子进程环境.
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Root(), "scripts", "tests", script) }) start.ArgumentList.Add(argument);
        if (scenario is not null)
        {
            start.ArgumentList.Add("-Scenario");
            start.ArgumentList.Add(scenario);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(true); throw; }
        var actualOutput = await output;
        Assert.True(process.ExitCode == 0, actualOutput + await error);
        Assert.Contains(completed, actualOutput);
    }
}
