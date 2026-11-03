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

    /// <summary>对照同解释器的直接 -File 执行, 核验脚本作用域、显式退出、原生残留退出码及异常均不被宿主改写.</summary>
    /// <param name="shell">真实 PowerShell 7 或 Windows PowerShell 5.1.</param>
    /// <param name="scenario">只在本次唯一临时文件中执行的作用域或退出边界.</param>
    [Theory]
    [InlineData("pwsh", "scope")]
    [InlineData("powershell.exe", "scope")]
    [InlineData("pwsh", "native-last")]
    [InlineData("powershell.exe", "native-last")]
    [InlineData("pwsh", "handled-native")]
    [InlineData("powershell.exe", "handled-native")]
    [InlineData("pwsh", "explicit-success")]
    [InlineData("powershell.exe", "explicit-success")]
    [InlineData("pwsh", "explicit-failure")]
    [InlineData("powershell.exe", "explicit-failure")]
    [InlineData("pwsh", "exception")]
    [InlineData("powershell.exe", "exception")]
    public async Task InstallerHostPreservesFileExecutionSemantics(string shell, string scenario)
    {
        var source = scenario switch
        {
            "scope" => """
                Set-StrictMode -Version Latest
                if ([string]::IsNullOrEmpty($PSScriptRoot)) { throw 'missing script directory' }
                $scenario = @{ Directory = 'PASS isolated script scope' }
                Write-Output $scenario.Directory
                """,
            "native-last" => "& $env:ComSpec /d /c 'exit 47'",
            "handled-native" => "& $env:ComSpec /d /c 'exit 47'; Write-Output 'PASS handled native failure'",
            "explicit-success" => "& $env:ComSpec /d /c 'exit 47'; exit 0",
            "explicit-failure" => "exit 47",
            "exception" => "throw 'EXPECTED installer host exception'",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var fixture = Path.Combine(Path.GetTempPath(), $"pbi-utils-installer-host-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(fixture, source, new UTF8Encoding(true));
            var direct = CreateInstallerProcessStart(shell);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixture }) direct.ArgumentList.Add(argument);
            var (expectedCode, expectedOutput, expectedError) = await RunInstallerProcessAsync(direct);
            var (actualCode, actualOutput, actualError) = await RunInstallerScriptAsync(shell, fixture, null, 1252);
            Assert.Equal(expectedCode, actualCode);
            Assert.Equal(expectedOutput, actualOutput);
            Assert.DoesNotContain("\uFFFD", actualOutput + actualError);
            if (scenario == "exception")
            {
                Assert.Contains("EXPECTED installer host exception", expectedError);
                Assert.Contains("EXPECTED installer host exception", actualError);
            }
        }
        finally { File.Delete(fixture); }
    }

    /// <summary>通过测试专用宿主执行原始脚本, 在子进程内约定 UTF-8 通道, 保留源 BOM、脚本目录和解释器身份.</summary>
    /// <param name="shell">实际测试解释器, 不替换为其他 PowerShell.</param>
    /// <param name="script">scripts/tests 中原始测试脚本的文件名, 或本次独立临时夹具的完整路径.</param>
    /// <param name="scenario">原样传递给目标脚本的可选场景.</param>
    /// <param name="initialCodePage">仅回归测试使用的子进程初始代码页, 0 表示保留解释器默认值.</param>
    /// <returns>真实退出码、标准输出和标准错误, 超时仅终止本次子进程树并抛出.</returns>
    private static async Task<(int Code, string Output, string Error)> RunInstallerScriptAsync(string shell, string script, string? scenario, int initialCodePage = 0)
    {
        var start = CreateInstallerProcessStart(shell);
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(Root(), "scripts", "tests", "installer-test-host.ps1"),
            "-TestScript", Path.IsPathRooted(script) ? script : Path.Combine(Root(), "scripts", "tests", script)
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
        return await RunInstallerProcessAsync(start);
    }

    /// <summary>为宿主和直接 -File 基线建立相同的无窗口读流, 仅清除子进程的开发模块搜索路径.</summary>
    /// <param name="shell">实际执行文件, 保持所选解释器身份.</param>
    /// <returns>尚未启动的进程参数, 两个输出通道按 UTF-8 独立捕获.</returns>
    private static ProcessStartInfo CreateInstallerProcessStart(string shell)
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
        return start;
    }

    /// <summary>执行本测试创建的准确进程并分别排空两个流, 超时只终止该进程树并传播原失败.</summary>
    /// <param name="start">当前测试拥有的独立进程启动参数.</param>
    /// <returns>真实退出码、标准输出与标准错误; 启动或超时错误直接抛出.</returns>
    private static async Task<(int Code, string Output, string Error)> RunInstallerProcessAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
