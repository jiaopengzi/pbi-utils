// FilePath    : pbi-utils\scripts\tests\ScriptWorkflowTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 让拥有独立进程与临时目录的开发、前端和发布测试分别调度.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

/// <summary>验证独立临时仓库中的开发编排与运行产物.</summary>
public sealed class ScriptDevelopmentWorkflowTests
{
    /// <summary>在独立临时仓库验证 clean、无工具帮助、编排和环境恢复.</summary>
    [Fact]
    public async Task SafetyAndOrchestration()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/contracts.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 脚本安全与编排契约", Out);
    }

    /// <summary>独立 FDD 夹具固定开发 apphost 同目录闭包、依赖冲突及已有输出占用拒绝.</summary>
    [Fact]
    public async Task DevelopmentOutputsKeepRealEntrypointsAndValidateDependencies()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/dev-output-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 开发输出同目录入口、FDD闭包、冲突与占用拒绝", Out);
    }
}

/// <summary>验证独立前端夹具的静态检查复用边界.</summary>
public sealed class ScriptFrontendWorkflowTests
{
    /// <summary>隔离验证同轮前端检查复用、输入变化失效、独立命令及失败取消的交付边界.</summary>
    [Fact]
    public async Task FrontendCheckReuseAndInvalidation()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/frontend-check-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 同轮前端检查复用与失效契约", Out);
    }
}

/// <summary>验证独立发布夹具的共享闭包、精确产物与交付失败边界.</summary>
public sealed class ScriptPublishWorkflowTests
{
    /// <summary>独立合成发布并注入失败、占用和链接, 固定编译及前端顺序与精确产物集合.</summary>
    [Fact]
    public async Task CleanPublishAndManifest()
    {
        // 该脚本执行 26 轮合成构建及完整发布保护检查, 总预算独立于单条 CLI 的 30 秒限制.
        var (Code, Out, Error) = await CliTests.RunWithTimeout("pwsh", TimeSpan.FromMinutes(1), "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/publish-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS C# 先编译、前端后打包、干净发布与失败保护契约", Out);
    }

    /// <summary>验证两种部署的共享框架闭包、精确清单、受控替代、冲突拒绝及独立编译输出保护.</summary>
    [Fact]
    public async Task SharedPublishRuntimeAndConflicts()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(ScriptTestRepository.Root(), "scripts/tests/shared-publish-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 共享发布双部署模式、框架闭包、精确清单、冲突拒绝及独立编译输出保护", Out);
    }
}
