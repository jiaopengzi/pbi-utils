// FilePath    : pbi-utils\scripts\tests\ScriptInstallerTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在开发与终端 PowerShell 中验证安装依赖和文件事务行为, 不执行真实系统安装.

using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        var (code, output, error) = await RunInstallerScriptAsync(shell, script, scenario);
        Assert.True(code == 0, output + error);
        Assert.Contains(completed, output);
    }

    /// <summary>在无窗口 CP1252 子进程中执行原始 BOM 脚本, 确认中文终态不依赖机器区域和控制台代码页.</summary>
    /// <param name="shell">仍需真实执行的 PowerShell 7 或 Windows PowerShell 5.1.</param>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task InstallerGateOutputUsesUtf8OnWesternConsole(string shell)
    {
        var (code, output, error) = await RunInstallerScriptAsync(shell, "installer-gate-tests.ps1", null, 1252);
        Assert.True(code == 0, output + error);
        Assert.Contains("PASS 安装阶段在完整载荷释放前执行只读门禁", output);
        Assert.DoesNotContain("\uFFFD", output + error);
    }

    /// <summary>通过测试专用宿主执行原始脚本, 在子进程内约定 UTF-8 通道, 保留源 BOM、脚本目录和解释器身份.</summary>
    /// <param name="shell">实际测试解释器, 不替换为其他 PowerShell.</param>
    /// <param name="script">scripts/tests 中原始测试脚本的文件名.</param>
    /// <param name="scenario">原样传递给目标脚本的可选场景.</param>
    /// <param name="initialCodePage">仅回归测试使用的子进程初始代码页, 0 表示保留解释器默认值.</param>
    /// <returns>真实退出码、标准输出和标准错误, 超时仅终止本次子进程树并抛出.</returns>
    private static async Task<(int Code, string Output, string Error)> RunInstallerScriptAsync(string shell, string script, string? scenario, int initialCodePage = 0)
    {
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 与 NSIS 相同: 不让开发宿主的 PS7 模块路径污染系统 PS5.1, 只改变子进程环境.
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(Root(), "scripts", "tests", "installer-test-host.ps1"),
            "-TestScript", Path.Combine(Root(), "scripts", "tests", script)
        }) start.ArgumentList.Add(argument);
        if (scenario is not null)
        {
            start.ArgumentList.Add("-Scenario");
            start.ArgumentList.Add(scenario);
        }
        if (initialCodePage != 0)
        {
            start.ArgumentList.Add("-InitialCodePage");
            start.ArgumentList.Add(initialCodePage.ToString(CultureInfo.InvariantCulture));
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
