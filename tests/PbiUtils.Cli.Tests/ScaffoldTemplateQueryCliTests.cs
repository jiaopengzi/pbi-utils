// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ScaffoldTemplateQueryCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实 CLI 的脚手架请求错误映射及不创建目标的行为.

using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>代表性请求经过真实 CLI 和 Host, 验证跨进程错误映射及无文件提交.</summary>
[Collection("CLI User Data")]
public sealed class ScaffoldTemplateQueryCliTests : IDisposable
{
    private readonly ScaffoldRequestFixture request = new();

    /// <summary>验证真实进程的 JSON 错误码和退出码, 并确认失败不创建目标目录.</summary>
    /// <param name="section">生成请求时覆盖的字段.</param>
    /// <param name="value">该字段的 JSON 值, null 表示省略字段.</param>
    /// <param name="exitCode">实际 CLI 应返回的退出码.</param>
    /// <param name="errorCode">JSON 业务错误码.</param>
    [Theory]
    [InlineData("templateQueries", null, 3, "INVALID_SCAFFOLD_PLAN")]
    [InlineData("templateQueries", "[\"hour24\"]", 2, "INVALID_SCAFFOLD_REQUEST")]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"description\":\"\",\"displayFolder\":\"\"}]", 2, "INVALID_ARGUMENT")]
    public async Task RequestFailureReachesRealCli(string section, string? value, int exitCode, string errorCode)
    {
        var (Code, Out, _) = await CliTests.Run(CliTests.Executable, await request.CreateArgumentsAsync(section, value));
        Assert.Equal(exitCode, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(request.Output));
    }

    /// <summary>释放本测试的独占请求目录.</summary>
    public void Dispose() => request.Dispose();
}
