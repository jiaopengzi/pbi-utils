// FilePath    : pbi-utils\scripts\tests\ScriptTests.Release.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将隔离发布归档和 Git 标签行为纳入正式 C# 测试门禁.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

/// <summary>验证发布上下文和归档内容.</summary>
public sealed class ScriptReleaseTests
{
    /// <summary>在独立 PowerShell 中验证发布说明和归档一致性, 核对退出码及完成标记.</summary>
    [Fact]
    public async Task ReleaseArchiveBehavior()
    {
        var script = Path.Combine(ScriptTestRepository.Root(), "scripts", "tests", "release-automation-tests.ps1");
        var (code, output, error) = await CliTests.RunWithTimeout("pwsh", CliTests.ScriptCollectionTimeout, "-NoProfile", "-File", script);
        Assert.True(code == 0, output + error);
        Assert.Contains("PASS release context, notes and exact ZIP archive", output);
    }
}

/// <summary>用独立 Git 仓库验证版本准备和已提交版本.</summary>
public sealed class ScriptReleaseAliasPreparationTests
{
    /// <summary>验证版本准备、重复调用和已提交版本拒绝的场景, 子进程失败时使测试失败.</summary>
    [Fact]
    public Task PreparationAndCommittedVersion() => ReleaseAliasTest.RunAsync("Prepare");
}

/// <summary>用独立 Git 仓库和本地 bare origin 验证真实发布引用.</summary>
[Collection(nameof(ScriptReleaseAliasPublicationTests))]
public sealed class ScriptReleaseAliasPublicationTests
{
    /// <summary>验证预览、发布成功及远端拒绝场景, 子进程失败时使测试失败.</summary>
    [Fact]
    public Task PublicationAndRemoteFailures() => ReleaseAliasTest.RunAsync("Publish");
}

/// <summary>用独立 Git 仓库验证分支和标签规则.</summary>
public sealed class ScriptReleaseAliasTagRuleTests
{
    /// <summary>验证分支和标签无效时不修改 Git 引用, 子进程失败时使测试失败.</summary>
    [Fact]
    public Task InvalidBranchesAndTags() => ReleaseAliasTest.RunAsync("TagRules");
}

/// <summary>用独立 Git 仓库验证工作区与发布说明.</summary>
public sealed class ScriptReleaseAliasInputTests
{
    /// <summary>验证脏工作区和无效发布说明的拒绝场景, 子进程失败时使测试失败.</summary>
    [Fact]
    public Task DirtyWorktreesAndInvalidChangelogs() => ReleaseAliasTest.RunAsync("CleanInputs");
}

/// <summary>在相互隔离的 PowerShell 进程中执行别名测试组.</summary>
internal static class ReleaseAliasTest
{
    /// <summary>启动指定分组, 核对真实退出码和完成标记; 超时或断言失败时抛错.</summary>
    /// <param name="scenario">测试脚本支持的独立场景组名称.</param>
    internal static async Task RunAsync(string scenario)
    {
        // Git 别名反复启动 Git、Bash 和 PowerShell, 各组使用脚本合集总预算.
        var script = Path.Combine(ScriptTestRepository.Root(), "scripts", "tests", "release-alias-tests.ps1");
        var (code, output, error) = await CliTests.RunWithTimeout("pwsh", CliTests.ScriptCollectionTimeout, "-NoProfile", "-File", script, "-Scenario", scenario);
        Assert.True(code == 0, output + error);
        Assert.Contains("PASS release alias:", output);
    }
}
