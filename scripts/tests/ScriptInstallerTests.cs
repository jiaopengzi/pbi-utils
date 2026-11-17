// FilePath    : pbi-utils\scripts\tests\ScriptInstallerTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在开发与终端 PowerShell 中验证安装依赖和文件事务行为, 不执行真实系统安装.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

/// <summary>在两个 PowerShell 解释器中验证安装前置条件及进程输出.</summary>
public sealed class ScriptInstallerTests
{
    /// <summary>使用两个真实解释器运行隔离安装测试, 确认退出码与实际完成标记, 不改机器执行策略.</summary>
    /// <param name="shell">开发 PowerShell 或 Windows 自带 PowerShell.</param>
    /// <param name="script">只操作隔离目录、唯一测试注册键和模拟外部适配的行为测试.</param>
    /// <param name="completed">成功终态标记, 防止脚本未执行即返回成功.</param>
    [Theory]
    [InlineData("pwsh", "installer-worker-tests.ps1", "PASS installer prerequisite worker")]
    [InlineData("powershell.exe", "installer-worker-tests.ps1", "PASS installer prerequisite worker")]
    [InlineData("pwsh", "installer-gate-tests.ps1", "PASS 安装阶段在完整载荷释放前执行只读门禁")]
    [InlineData("powershell.exe", "installer-gate-tests.ps1", "PASS 安装阶段在完整载荷释放前执行只读门禁")]
    [InlineData("pwsh", "installer-prerequisites-tests.ps1", "Installer prerequisites tests passed:")]
    [InlineData("powershell.exe", "installer-prerequisites-tests.ps1", "Installer prerequisites tests passed:")]
    [InlineData("pwsh", "installer-external-tools-tests.ps1", "PASS external tool registration, ownership, rollback and privilege boundaries")]
    [InlineData("powershell.exe", "installer-external-tools-tests.ps1", "PASS external tool registration, ownership, rollback and privilege boundaries")]
    public Task InstallerBehaviorInBothPowerShells(string shell, string script, string completed) =>
        InstallerScriptTestProcess.AssertCompletedAsync(shell, script, completed);

    /// <summary>在无窗口 CP1252 子进程中执行原始 BOM 脚本, 确认中文终态不依赖机器区域和控制台代码页.</summary>
    /// <param name="shell">仍需真实执行的 PowerShell 7 或 Windows PowerShell 5.1.</param>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public async Task InstallerGateOutputUsesUtf8OnWesternConsole(string shell)
    {
        var (code, output, error) = await InstallerScriptTestProcess.RunScriptAsync(shell, "installer-gate-tests.ps1", null, 1252);
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
            var direct = InstallerScriptTestProcess.CreateProcessStart(shell);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixture }) direct.ArgumentList.Add(argument);
            var (expectedCode, expectedOutput, expectedError) = await InstallerScriptTestProcess.RunProcessAsync(direct);
            var (actualCode, actualOutput, actualError) = await InstallerScriptTestProcess.RunScriptAsync(shell, fixture, null, 1252);
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
}

/// <summary>各安装脚本在独立的子进程中执行, 事务按唯一临时根加锁.</summary>
[Collection(nameof(InstallerApplicationTests))]
public sealed class InstallerApplicationTests
{
    /// <summary>在每个解释器中验证安装、升级、回滚和租约, 以实际退出码及终态判定成功.</summary>
    /// <param name="shell">用于执行事务的真实 PowerShell 解释器.</param>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public Task ApplicationTransactions(string shell) =>
        InstallerScriptTestProcess.AssertCompletedAsync(shell, "installer-application-tests.ps1",
            "PASS installer application transactions, upgrades, rollback, ownership and leases", "Transactions");
}

/// <summary>维护入口原生参数只由系统 Windows PowerShell 验证一次.</summary>
public sealed class InstallerEntryOptionsTests
{
    /// <summary>以实际进程退出码及终态验证 Windows PowerShell 5.1 的 -File 参数边界.</summary>
    [Fact]
    public Task WindowsPowerShellEntryOptions() =>
        InstallerScriptTestProcess.AssertCompletedAsync("pwsh", "installer-application-tests.ps1",
            "PASS installer Windows PowerShell entry options", "EntryOptions");
}

/// <summary>安装外壳测试只使用唯一注册键和临时快捷方式目录.</summary>
public sealed class InstallerShellTests
{
    /// <summary>在每个解释器中验证安装外壳归属、冲突和回滚, 以实际退出码及终态判定成功.</summary>
    /// <param name="shell">用于执行外壳事务的真实 PowerShell 解释器.</param>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell.exe")]
    public Task ShellTransactions(string shell) =>
        InstallerScriptTestProcess.AssertCompletedAsync(shell, "installer-shell-tests.ps1",
            "PASS installer shell conflicts, ownership and rollback");
}

/// <summary>安装包测试在独立临时仓库中验证编译边界和失败保护.</summary>
public sealed class InstallerPackageTests
{
    /// <summary>每个场景使用独立子进程和超时, 验证快照、编译及首错传播.</summary>
    /// <param name="scenario">原脚本接受的准确打包场景.</param>
    /// <param name="completed">脚本完成场景后输出的标记.</param>
    [Theory]
    [InlineData("Package", "PASS installer package snapshot identity and SDK boundaries")]
    [InlineData("PackageFailure", "PASS installer compiler failure, lock release and cleanup failure preservation")]
    [InlineData("DeferredPackage", "PASS installer deferred release package delivery and discard")]
    public Task PackageTransactions(string scenario, string completed) =>
        InstallerScriptTestProcess.AssertCompletedAsync("pwsh", "installer-package-tests.ps1", completed, scenario);
}

/// <summary>许可验证、打包许可刷新和损坏输入场景各使用独立临时目录.</summary>
public sealed class InstallerLicenseTests
{
    /// <summary>验证许可刷新和分发内容, 要求每个子进程实际执行完对应场景.</summary>
    /// <param name="script">独立许可验证脚本.</param>
    /// <param name="completed">脚本完成场景后输出的标记.</param>
    /// <param name="scenario">原脚本接受的可选许可刷新场景.</param>
    [Theory]
    [InlineData("installer-package-tests.ps1", "PASS installer current license refresh and source isolation", "LicenseRefresh")]
    [InlineData("installer-package-tests.ps1", "PASS installer license input drift, missing sources and concurrent edits rejected", "LicenseRefreshFailure")]
    [InlineData("license-bundle-tests.ps1", "License bundle tests passed.", null)]
    [InlineData("license-bundle-validation-tests.ps1", "License bundle validation tests passed.", null)]
    [InlineData("license-source-git-tests.ps1", "License source Git tests passed.", null)]
    public Task PackageLicenses(string script, string completed, string? scenario) =>
        InstallerScriptTestProcess.AssertCompletedAsync("pwsh", script, completed, scenario);
}

/// <summary>为独立安装测试保留真实解释器、各自超时及 stdout/stderr, 不共享进程环境.</summary>
internal static class InstallerScriptTestProcess
{
    /// <summary>验证脚本实际成功完成, 退出非零时保留两个诊断流.</summary>
    /// <param name="shell">实际执行的 PowerShell 解释器.</param>
    /// <param name="script">scripts/tests 中的测试脚本文件名.</param>
    /// <param name="completed">确认执行到结束的标记.</param>
    /// <param name="scenario">传递给目标脚本的可选场景.</param>
    internal static async Task AssertCompletedAsync(string shell, string script, string completed, string? scenario = null)
    {
        var (code, output, error) = await RunScriptAsync(shell, script, scenario);
        Assert.True(code == 0, output + error);
        Assert.Contains(completed, output);
    }

    /// <summary>通过测试专用宿主执行原始脚本, 在子进程内约定 UTF-8 通道, 保留源 BOM、脚本目录和解释器身份.</summary>
    /// <param name="shell">实际测试解释器, 不替换为其他 PowerShell.</param>
    /// <param name="script">scripts/tests 中原始测试脚本的文件名, 或本次独立临时夹具的完整路径.</param>
    /// <param name="scenario">原样传递给目标脚本的可选场景.</param>
    /// <param name="initialCodePage">仅回归测试使用的子进程初始代码页, 0 表示保留解释器默认值.</param>
    /// <returns>真实退出码、标准输出和标准错误, 超时仅终止本次子进程树并抛出.</returns>
    internal static async Task<(int Code, string Output, string Error)> RunScriptAsync(string shell, string script, string? scenario, int initialCodePage = 0)
    {
        var start = CreateProcessStart(shell);
        var testsDirectory = Path.Combine(ScriptTestRepository.Root(), "scripts", "tests");
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(testsDirectory, "installer-test-host.ps1"),
            "-TestScript", Path.IsPathRooted(script) ? script : Path.Combine(testsDirectory, script)
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
        return await RunProcessAsync(start);
    }

    /// <summary>为宿主和直接 -File 基线建立相同的无窗口读流, 仅清除子进程的开发模块搜索路径.</summary>
    /// <param name="shell">实际执行文件, 保持所选解释器身份.</param>
    /// <returns>尚未启动的进程参数, 两个输出通道按 UTF-8 独立捕获.</returns>
    internal static ProcessStartInfo CreateProcessStart(string shell)
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
    internal static async Task<(int Code, string Output, string Error)> RunProcessAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(CliTests.ScriptCollectionTimeout); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
