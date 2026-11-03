// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\FormatCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立 CLI 格式化与移除内嵌 AI 后的命令边界回归.

using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>保留共享本地格式化行为, 旧 AI 命令不得触发凭据或网络访问.</summary>
public sealed class FormatCliTests
{
    /// <summary>已移除 AI 子命令统一返回参数错误, 不加载提供方或密钥.</summary>
    [Theory]
    [InlineData("providers")]
    [InlineData("settings")]
    [InlineData("chat")]
    [InlineData("credential")]
    public async Task RemovedAiCommandsAreRejected(string action)
    {
        var (code, output, error) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "ai", action, "--json");
        Assert.Equal(2, code);
        Assert.Empty(error);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>真实独立进程执行共享格式化, stdout 恰好一个文档且没有执行代码.</summary>
    [Theory]
    [InlineData("dax", "SUM(Sales[Amount])", "SUM ( Sales[Amount] )")]
    [InlineData("m", "let X=1 in X", "let\n    X = 1\nin\n    X")]
    public async Task LocalFormatWorksWithoutModelOrCredentials(string language, string text, string expected)
    {
        var (code, output, error) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "format", language, "--text", text, "--json");
        Assert.Equal(0, code); Assert.Empty(error);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(expected, document.RootElement.GetProperty("data").GetProperty("text").GetString());
        Assert.False(document.RootElement.GetProperty("data").GetProperty("executed").GetBoolean());
    }

    /// <summary>帮助展示当前 MCP 和格式化入口, 不包含旧 AI 配置/聊天入口.</summary>
    [Fact]
    public async Task HelpDocumentsMcpAndLocalFormatting()
    {
        var (code, output, _) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "--help");
        Assert.Equal(0, code);
        Assert.Contains("pbi-utils mcp serve [--allow-write]", output);
        Assert.Contains("format dax|m", output);
        Assert.DoesNotContain("pbi-utils ai", output);
    }

    /// <summary>格式化的未知语言、未知选项和缺值按参数错误拒绝.</summary>
    [Theory]
    [InlineData("sql", "--text", "x")]
    [InlineData("dax", "--path", "x")]
    [InlineData("dax", "--text", "--json")]
    public async Task LocalFormatRejectsInvalidArguments(string language, string option, string value)
    {
        var (code, output, _) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "format", language, option, value, "--json");
        Assert.Equal(2, code);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }
}
