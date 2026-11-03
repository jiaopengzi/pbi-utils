// FilePath    : pbi-utils\scripts\tests\ScriptTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 PowerShell 进程验证入口参数, 输出与安全契约.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

/// <summary>固定 xUnit 框架运行真实 PowerShell 入口与安全测试.</summary>
public sealed partial class ScriptTests
{
    /// <summary>按仓库标记定位, 不依赖测试调用目录.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "run.ps1"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库");
    }

    /// <summary>启动真正脚本入口, 传递独立参数数组.</summary>
    private static Task<(int Code, string Out, string Error)> Run(params string[] arguments) =>
        CliTests.Run("pwsh", ["-NoProfile", "-File", Path.Combine(Root(), "run.ps1"), .. arguments]);

    /// <summary>全部不适用参数、未知动作和缺少值返回 2.</summary>
    /// <param name="arguments">非法参数组合.</param>
    [Theory]
    [InlineData("-NonInteractive")]
    [InlineData("-Choice", "")]
    [InlineData("-Choice", "unknown")]
    [InlineData("-Choice", "help", "-Scope", "Core")]
    [InlineData("-Choice", "testCore", "-Scope", "Core")]
    [InlineData("-Choice", "setupC#", "-Configuration", "Debug")]
    [InlineData("-Choice", "buildVue", "-Scope", "Core")]
    [InlineData("-Choice", "testVue", "-Configuration", "Debug")]
    [InlineData("-Choice", "buildC#", "-Configuration")]
    [InlineData("-Choice", "setupAll", "-Scope", "-Unknown")]
    [InlineData("-Choice", "hello", "-Name")]
    [InlineData("-Choice", "publish", "-Runtime", "win-x64")]
    [InlineData("-List", "-Choice", "help")]
    [InlineData("-Help", "-Json")]
    [InlineData("-Choice", "help", "-Unknown")]
    [InlineData("-Choice", "buildC#", "-AfterCompile", "callback")]
    [InlineData("-Choice", "all", "-DotNetMode", "both")]
    [InlineData("-Choice", "buildC#", "-DotNetMode")]
    [InlineData("-Choice", "testC#", "-DotNetMode", "without-dotnet")]
    [InlineData("-Choice", "testAll", "-DotNetMode", "with-dotnet")]
    [InlineData("-Choice", "buildVue", "-DotNetMode", "with-dotnet")]
    [InlineData("-Choice", "package", "-Scope", "All")]
    [InlineData("-Choice", "release", "-Configuration", "Debug")]
    public async Task InvalidParameters(params string[] arguments) => Assert.Equal(2, (await Run(arguments)).Code);

    /// <summary>数字别名和名称来自同一注册表, 无需构建工具.</summary>
    [Fact]
    public async Task HelpAliasesAndRegistry()
    {
        var (Code, Out, Error) = await Run("-List");
        var help = await Run("-Choice", "0");
        var named = await Run("-Choice", "HELP");
        Assert.Equal(0, Code);
        Assert.Equal(Out, help.Out);
        Assert.Equal(help.Out, named.Out);
        Assert.Contains("19  all", named.Out);
        Assert.Contains("20  package", named.Out);
        Assert.Contains("21  release", named.Out);
        var ids = MenuIdRegex().Matches(named.Out)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Enumerable.Range(0, 22), ids);
    }

    /// <summary>下层脚本同样拒绝未知参数和重复 Scope, 不暴露内部调度参数.</summary>
    [Fact]
    public async Task LowerScriptsEnforceContract()
    {
        foreach (var script in new[] { "setup", "build", "test", "check", "format", "clean", "dev", "package" })
        {
            var (Code, _, _) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts", script + ".ps1"), "-Unknown");
            Assert.Equal(2, Code);
        }
        Assert.Equal(2, (await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/test.ps1"), "-Scope", "Core", "-Scope", "All")).Code);
        Assert.Equal(2, (await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/build.ps1"), "-AfterCompile", "callback")).Code);
    }

    /// <summary>已移除动作及越界编号拒绝执行, 不再保留产品命令包装层.</summary>
    /// <param name="action">已移除动作名称或越界编号.</param>
    [Theory]
    [InlineData("setup")]
    [InlineData("build")]
    [InlineData("testCore")]
    [InlineData("format")]
    [InlineData("check")]
    [InlineData("hello")]
    [InlineData("agentHello")]
    [InlineData("doctor")]
    [InlineData("publish")]
    [InlineData("-1")]
    [InlineData("22")]
    [InlineData("28")]
    public async Task RetiredActions(string action)
    {
        var (Code, Out, _) = await Run("-Choice", action);
        Assert.Equal(2, Code);
        Assert.Empty(Out);
    }

    /// <summary>在独立临时仓库验证 clean、无工具帮助、编排和环境恢复.</summary>
    [Fact]
    public async Task SafetyAndOrchestration()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/contracts.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 脚本安全与编排契约", Out);
    }

    /// <summary>隔离验证同轮前端检查复用、输入变化失效、独立命令及失败取消的交付边界.</summary>
    [Fact]
    public async Task FrontendCheckReuseAndInvalidation()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/frontend-check-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 同轮前端检查复用与失效契约", Out);
    }

    /// <summary>独立合成发布并注入失败、占用和链接, 固定编译及前端顺序与精确产物集合.</summary>
    [Fact]
    public async Task CleanPublishAndManifest()
    {
        // 该脚本执行 26 轮合成构建及完整发布保护检查, 总预算独立于单条 CLI 的 30 秒限制.
        var (Code, Out, Error) = await CliTests.RunWithTimeout("pwsh", TimeSpan.FromMinutes(1), "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/publish-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS C# 先编译、前端后打包、干净发布与失败保护契约", Out);
    }

    /// <summary>验证两种部署的共享框架闭包、精确清单、受控替代、冲突拒绝及独立编译输出保护.</summary>
    [Fact]
    public async Task SharedPublishRuntimeAndConflicts()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/shared-publish-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 共享发布双部署模式、框架闭包、精确清单、冲突拒绝及独立编译输出保护", Out);
    }

    /// <summary>独立 FDD 夹具固定开发 apphost 同目录闭包、依赖冲突及已有输出占用拒绝.</summary>
    [Fact]
    public async Task DevelopmentOutputsKeepRealEntrypointsAndValidateDependencies()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/dev-output-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 开发输出同目录入口、FDD闭包、冲突与占用拒绝", Out);
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
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/encoding.ps1"),
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

    /// <summary>编译生成菜单编号提取规则, 返回共享缓存实例.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^\s*(\d+)  \w")]
    private static partial System.Text.RegularExpressions.Regex MenuIdRegex();
}
