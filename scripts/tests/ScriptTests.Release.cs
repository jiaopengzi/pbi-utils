// FilePath    : pbi-utils\scripts\tests\ScriptTests.Release.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将隔离发布归档和 Git 标签行为纳入正式 C# 测试门禁.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

public sealed partial class ScriptTests
{
    /// <summary>在独立 PowerShell 中执行真实发布边界测试, 要求退出码和终态标记同时满足.</summary>
    /// <param name="script">只使用隔离文件或本地 bare Git 远端的测试脚本.</param>
    /// <param name="completed">防止未执行测试即返回成功的精确标记.</param>
    [Theory]
    [InlineData("release-automation-tests.ps1", "PASS release context, notes and exact ZIP archive")]
    [InlineData("release-alias-tests.ps1", "PASS release alias")]
    public async Task ReleaseAutomationBehavior(string script, string completed)
    {
        // 别名合集包含多次独立 PowerShell/Git 场景, 本机已实测超过普通 CLI 的 30 秒预算.
        var timeout = script == "release-alias-tests.ps1" ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30);
        var (code, output, error) = await CliTests.RunWithTimeout("pwsh", timeout, "-NoProfile", "-File", Path.Combine(Root(), "scripts", "tests", script));
        Assert.True(code == 0, output + error);
        Assert.Contains(completed, output);
    }
}
