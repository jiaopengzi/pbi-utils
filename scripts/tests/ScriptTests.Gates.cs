// FilePath    : pbi-utils\scripts\tests\ScriptTests.Gates.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将真实 PowerShell 发布失败传播与清理错误契约纳入正式 CLI 测试门禁.

using PbiUtils.Cli.Tests;
using Xunit;

namespace PbiUtils.ScriptTests;

public sealed partial class ScriptTests
{
    /// <summary>隔离执行开发注册编排, 验证最终阶段、范围与权限失败传播, 不写系统注册目录.</summary>
    [Fact]
    public async Task ExternalToolRegistrationFollowsSuccessfulDevelopmentDelivery()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/external-tool-workflow-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS external tool workflow ordering, scope and failure boundaries", Out);
    }

    /// <summary>执行独立真实入口子进程, 枚举或清理失败必须阻止成功且保留主错误的退出码和阶段.</summary>
    [Fact]
    public async Task GateFailuresPreserveFirstErrorAndNeverReportSuccess()
    {
        var (Code, Out, Error) = await CliTests.Run("pwsh", "-NoProfile", "-File", Path.Combine(Root(), "scripts/tests/gate-failure-tests.ps1"));
        Assert.True(Code == 0, Out + Error);
        Assert.Contains("PASS 发布枚举与清理错误传播、首个退出码及阶段保持契约", Out);
    }
}
