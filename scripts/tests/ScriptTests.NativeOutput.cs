// FilePath    : pbi-utils\scripts\tests\ScriptTests.NativeOutput.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证正常 pnpm 脚本回显不被误作错误, 其他原生诊断、顺序和退出码保持.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

public sealed partial class ScriptTests
{
    /// <summary>仅当前 pnpm 包脚本的精确回显可作为普通信息, 不改其他工具或未知文本的 stderr.</summary>
    /// <param name="scenario">已知回显、失败、未知命令、其他工具、损坏配置或配置读取取消的实际进程场景.</param>
    /// <param name="expectedCode">原生命令必须透传的退出码.</param>
    /// <param name="information">是否有足够依据将两条 pnpm 回显显示为普通信息.</param>
    [Theory]
    [InlineData("success", 0, true)]
    [InlineData("failure", 47, true)]
    [InlineData("unknown", 0, false)]
    [InlineData("other", 0, false)]
    [InlineData("invalid", 47, false)]
    [InlineData("cancel", 130, false)]
    public async Task PnpmCommandEchoKeepsNativeDiagnostics(string scenario, int expectedCode, bool information)
    {
        var (code, output, error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts", "tests", "native-command-echo-tests.ps1"), "-Scenario", scenario);
        Assert.Equal(expectedCode, code);
        if (scenario == "cancel")
        {
            Assert.DoesNotContain("OUT-before", output + error);
            Assert.Contains("130", error);
            return;
        }
        var commands = new[] { "$ node scripts/check-styles.ts", "$ vue-tsc --noEmit" };
        foreach (var command in commands)
        {
            Assert.Contains(command, information ? output : error);
            Assert.DoesNotContain(command, information ? error : output);
        }
        foreach (var diagnostic in new[] { "real native stderr diagnostic 1", "real native stderr diagnostic 2", "$ unknown stderr text" })
        {
            Assert.Contains(diagnostic, error);
            Assert.DoesNotContain(diagnostic, output);
        }
        Assert.True(output.IndexOf("OUT-before", StringComparison.Ordinal) < output.IndexOf("OUT-after", StringComparison.Ordinal));
        Assert.True(error.IndexOf("diagnostic 1", StringComparison.Ordinal) < error.IndexOf("diagnostic 2", StringComparison.Ordinal));
        if (information) Assert.True(output.IndexOf(commands[0], StringComparison.Ordinal) < output.IndexOf(commands[1], StringComparison.Ordinal));
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
    }
}
