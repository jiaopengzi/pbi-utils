// FilePath    : pbi-utils\scripts\tests\ScriptTests.TestResults.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将 C# 测试回执及真实执行的隔离脚本契约接入正式 CLI 测试项目.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

public sealed partial class ScriptTests
{
    /// <summary>验证本轮权威报告, 零执行、结构化错误、正当跳过和原生失败优先边界.</summary>
    [Fact]
    public async Task TestResultsRequireAuthoritativeExecution()
    {
        var (code, output, error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/test-results-tests.ps1"));
        Assert.True(code == 0, output + error);
        Assert.Contains("PASS C# 测试回执与实际执行门禁", output);
    }
}
