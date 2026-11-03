// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpJournalCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实人工 CLI 账本查询和核销参数边界, 不创建或核销用户执行记录.

using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>仅执行只读查询和前置参数拒绝; 有效核销状态机由隔离账本测试覆盖.</summary>
[Collection("CLI User Data")]
public sealed class McpJournalCliTests
{
    /// <summary>人工查询保留常规 CLI 单文档 JSON 契约, 不进入 stdio MCP 协议.</summary>
    [Fact]
    public async Task ExecutionsUsesNormalCliJsonEnvelope()
    {
        var (code, output, error) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "mcp", "executions", "--json");
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("mcp executions", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("data").GetProperty("executions").ValueKind);
    }

    /// <summary>缺少独立人工确认时不读取证据或修改账本.</summary>
    [Fact]
    public async Task ResolveRequiresExplicitManualConfirmation()
    {
        var (code, output, _) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location, "mcp", "resolve", "--json");
        Assert.Equal(2, code);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("CONFIRMATION_REQUIRED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>空证据文件不能用于解除未知提交阻塞, 不创建任何伪造执行记录.</summary>
    [Fact]
    public async Task ResolveRejectsEmptyEvidenceBeforeReadingAnExecution()
    {
        var evidence = Path.Combine(Path.GetTempPath(), "pbi-mcp-evidence-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(evidence, "");
            var (code, output, _) = await CliTests.Run("dotnet", typeof(PbiUtils.Cli.Program).Assembly.Location,
                "mcp", "resolve", "--execution", Guid.NewGuid().ToString("N"), "--expected-record-digest", new string('0', 64),
                "--outcome", "verified-not-applied", "--evidence", evidence, "--confirm", "--json");
            Assert.Equal(2, code);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("INVALID_EVIDENCE", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { if (File.Exists(evidence)) File.Delete(evidence); }
    }
}
