// FilePath    : pbi-utils\scripts\tests\ScriptTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 PowerShell 进程验证入口参数, 输出与安全契约.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

/// <summary>验证 PowerShell 命令入口的参数、退出码与编码契约.</summary>
public sealed partial class ScriptTests
{
    /// <summary>启动真正脚本入口, 传递独立参数数组.</summary>
    private static Task<(int Code, string Out, string Error)> Run(params string[] arguments) =>
        CliTests.Run("pwsh", ["-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "run.ps1"), .. arguments]);

    /// <summary>在独立子进程中执行全部参数矩阵, 逐次调用真实根入口和独立动作脚本.</summary>
    [Fact]
    public async Task EntryRejectionMatrix()
    {
        var (Code, Out, Error) = await CliTests.RunWithTimeout("pwsh", CliTests.ScriptCollectionTimeout, "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/command-entry-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS command entry rejections: 25 invalid arguments, 12 retired actions, 11 lower entries, 12 lane arguments", Out);
    }

    /// <summary>管道停止与控制台 Ctrl+C 同样跳过 catch; 每个入口脚本仍须在自身 finally 中以 130 结束, 并行通道写回取消结果.</summary>
    [Fact]
    public async Task StoppedEntriesReportCancellation()
    {
        var (Code, Out, Error) = await CliTests.RunWithTimeout("pwsh", CliTests.ScriptCollectionTimeout, "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/entry-stop-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 10 个入口脚本在管道停止时以 130 结束, 并行通道写回取消结果", Out);
    }

    /// <summary>单独执行 pwsh -File 时, 保留操作系统传入的空参数和根入口退出码.</summary>
    [Fact]
    public async Task EmptyChoiceRejectsFromNewProcess() => Assert.Equal(2, (await Run("-Choice", "")).Code);

    /// <summary>数字别名和名称来自同一注册表, 无需构建工具.</summary>
    [Fact]
    public async Task HelpAliasesAndRegistry()
    {
        var (Code, Out, _) = await Run("-List");
        Assert.Equal(0, Code);
        var entries = MenuEntryRegex().Matches(Out);
        var helpEntry = Assert.Single(entries, entry => entry.Groups[2].Value == "help");
        var help = await Run("-Choice", helpEntry.Groups[1].Value);
        var named = await Run("-Choice", "HELP");
        Assert.Equal(0, help.Code);
        Assert.Equal(0, named.Code);
        Assert.Equal(Out, help.Out);
        Assert.Equal(help.Out, named.Out);
        var ids = entries.Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Enumerable.Range(0, entries.Count), ids);
    }

    /// <summary>独立启动下层脚本进程, 确认重复参数的拒绝码经 pwsh -File 返回.</summary>
    [Fact]
    public async Task LowerScriptRejectsFromNewProcess() =>
        Assert.Equal(2, (await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/test.ps1"), "-Scope", "Core", "-Scope", "All")).Code);

    /// <summary>独立启动根入口进程, 确认旧动作在实际 stdout 无输出并返回 2.</summary>
    [Fact]
    public async Task RetiredActionRejectsFromNewProcess()
    {
        var (Code, Out, _) = await Run("-Choice", "doctor");
        Assert.Equal(2, Code);
        Assert.Empty(Out);
    }

    /// <summary>旧代码页与 UTF-8 下均保留中文、ANSI 箭头、输出通道、退出码和调用者编码.</summary>
    /// <param name="scenario">成功、原生失败、取消或参数失败场景.</param>
    /// <param name="codePage">调用者初始控制台代码页.</param>
    /// <param name="exitCode">预期透传的退出码.</param>
    [Theory]
    [InlineData("success", 936, 0)]
    [InlineData("failure", 936, 47)]
    [InlineData("cancel", 936, 130)]
    [InlineData("invalid", 936, 2)]
    [InlineData("success", 65001, 0)]
    [InlineData("failure", 65001, 47)]
    [InlineData("cancel", 65001, 130)]
    [InlineData("invalid", 65001, 2)]
    public async Task NativeOutputEncoding(string scenario, int codePage, int exitCode)
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/encoding.ps1"),
            "-Scenario", scenario, "-CodePage", codePage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Code == exitCode, Out + Error);
        Assert.Contains("ENCODING_RESTORED", Error);
        Assert.DoesNotContain("\uFFFD", Out + Error);
        if (scenario == "invalid") return;
        Assert.Contains("已成功生成。0 个警告。", Out);
        Assert.Contains("0 个错误", Out);
        Assert.Contains("2 个警告", Out);
        Assert.Contains("所有项目均是最新的，无法还原。", Out);
        Assert.Contains("\u001b[32m➜\u001b[39m Local: 世界", Out);
        Assert.DoesNotContain("已成功生成。0 个警告。", Error);
        Assert.DoesNotContain("Local: 世界", Error);
        Assert.Contains("诊断：中文错误流", Error);
        Assert.Contains("Build succeeded. 中文原始错误流", Error);
        Assert.Contains("无法还原依赖: 中文原始诊断", Error);
        Assert.DoesNotContain("诊断：中文错误流", Out);
        Assert.DoesNotContain("Build succeeded. 中文原始错误流", Out);
        Assert.DoesNotContain("无需还原", Out + Error);
        var plainOutput = Out.Replace("\u001b[32m➜\u001b[39m", "➜", StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", plainOutput, StringComparison.Ordinal);
    }

    /// <summary>编译生成菜单编号与动作名的提取规则, 返回共享缓存实例.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^\s*(\d+)  (\S+)")]
    private static partial System.Text.RegularExpressions.Regex MenuEntryRegex();
}
