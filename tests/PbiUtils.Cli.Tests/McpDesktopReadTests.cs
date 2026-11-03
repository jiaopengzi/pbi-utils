// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpDesktopReadTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP 桌面按需读取契约, 避免默认返回完整模型或放宽其他工具输入.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Cli.Mcp;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

public sealed class McpDesktopReadTests
{
    /// <summary>只读快照入口公开有界读取选项, 其他业务不能误接收这些参数.</summary>
    [Fact]
    public async Task SnapshotToolsExposeBoundedReadOptionsOnly()
    {
        await using var runtime = new McpBusinessRuntime(false, Path.GetTempPath(), NullLoggerFactory.Instance);
        foreach (var name in new[] { "desktop_project_inspect", "desktop_configuration_context" })
        {
            var properties = runtime.Tools.Single(tool => tool.Name == name).InputSchema.GetProperty("properties");
            Assert.True(properties.TryGetProperty("read", out var read));
            Assert.Equal(100, read.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32());
        }
        Assert.False(runtime.Tools.Single(tool => tool.Name == "desktop_app_info").InputSchema.GetProperty("properties").TryGetProperty("read", out _));
    }

    /// <summary>连接摘要保留基线/身份/保存事实, 不返回原始表列和度量表达式.</summary>
    [Fact]
    public void SummaryKeepsIdentityAndSaveFactsWithoutDefinitions()
    {
        var source = Snapshot();
        var result = new McpDesktopRead().Apply(source);
        var summary = Assert.IsType<JsonElement>(result.Value);
        Assert.Equal("p", summary.GetProperty("projectId").GetString());
        Assert.Equal("b", summary.GetProperty("baselineId").GetString());
        Assert.False(summary.GetProperty("saveResult").GetProperty("fileSaved").GetBoolean());
        Assert.Equal(7, result.ConnectionRevision);
        Assert.False(summary.GetProperty("model").TryGetProperty("measures", out _));
        Assert.DoesNotContain("SECRET_EXPRESSION", summary.GetRawText());
        Assert.Contains("SECRET_EXPRESSION", Assert.IsType<JsonElement>(source.Value).GetRawText());
    }

    /// <summary>分页保留原顺序和总数, 显式表达式开关生效, 基线过期不能混合不同模型页面.</summary>
    [Fact]
    public void PagesRespectIdentityAndExpressionOptIn()
    {
        var first = Assert.IsType<JsonElement>(new McpDesktopRead("measures", Limit: 1).Apply(Snapshot()).Value);
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        Assert.False(first.GetProperty("items")[0].TryGetProperty("expression", out _));
        var second = Assert.IsType<JsonElement>(new McpDesktopRead("measures", 1, 1, IncludeExpressions: true, BaselineId: "b", ProjectId: "p").Apply(Snapshot()).Value);
        Assert.Equal("Second", second.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("2", second.GetProperty("items")[0].GetProperty("expression").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
        Assert.Equal("STALE_BASELINE", Assert.Throws<AppException>(() => new McpDesktopRead("measures", BaselineId: "stale").Apply(Snapshot())).Code);
        Assert.Equal("STALE_BASELINE", Assert.Throws<AppException>(() => new McpDesktopRead("measures", ProjectId: "other").Apply(Snapshot())).Code);
    }

    /// <summary>大表的表摘要不回传全部列, 列使用相同目标基线独立分页, 错误表名明确拒绝.</summary>
    [Fact]
    public void TableAndColumnPagesAreIndependent()
    {
        var tables = Assert.IsType<JsonElement>(new McpDesktopRead("tables").Apply(Snapshot()).Value);
        Assert.Equal(1, tables.GetProperty("items")[0].GetProperty("columnCount").GetInt32());
        Assert.False(tables.GetProperty("items")[0].TryGetProperty("columns", out _));
        var columns = Assert.IsType<JsonElement>(new McpDesktopRead("columns", Table: "Sales").Apply(Snapshot()).Value);
        Assert.Equal("Amount", columns.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("INVALID_ARGUMENT", Assert.Throws<AppException>(() => new McpDesktopRead("measures", Table: "missing").Apply(Snapshot())).Code);
    }

    /// <summary>失败和未知提交不参与投影, 原始业务结果/分类/观察代际都保留.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FailedOrUncertainResultsRemainUnchanged(bool failed, bool uncertain)
    {
        var source = Snapshot() with { IsError = failed, NeedsVerification = uncertain, ErrorCode = "SAVE_FAILED" };
        Assert.Same(source, new McpDesktopRead().Apply(source));
    }

    /// <summary>参数在启动宿主前严格拒绝, 不让 schema 成为唯一校验.</summary>
    [Theory]
    [InlineData("{\"read\":{\"offset\":1}}")]
    [InlineData("{\"read\":{\"limit\":101}}")]
    [InlineData("{\"read\":{\"section\":\"columns\"}}")]
    [InlineData("{\"read\":{\"section\":\"configuration\"}}")]
    [InlineData("{\"read\":{\"includeExpressions\":\"yes\"}}")]
    [InlineData("{\"read\":{\"unknown\":true}}")]
    public void InvalidReadOptionsFailBeforeDispatch(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("INVALID_ARGUMENT", Assert.Throws<AppException>(() => McpDesktopRead.Parse("project.inspect", document.RootElement)).Code);
    }

    /// <summary>不可压缩的单项定义超限明确失败; 不返回截断源码或静默成功.</summary>
    [Fact]
    public void OversizedDefinitionIsNotTruncated()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Assert.IsType<JsonElement>(Snapshot().Value).GetRawText())!;
        node["model"]!["measures"]![0]!["expression"] = new string('x', McpDesktopRead.MaximumBytes);
        var source = Snapshot() with { Value = JsonSerializer.SerializeToElement(node) };
        Assert.Equal("OUTPUT_TOO_LARGE", Assert.Throws<AppException>(() => new McpDesktopRead("measures", IncludeExpressions: true).Apply(source)).Code);
        Assert.False(new McpDesktopRead().Apply(source).IsError);
    }

    /// <summary>构造具有独立目标和真实保存状态形状的最小快照, 不依赖桌面进程.</summary>
    private static McpDesktopResult Snapshot() => new(JsonSerializer.SerializeToElement(new
    {
        projectId = "p",
        baselineId = "b",
        projectPath = "model://fixture",
        connection = new { instanceId = "instance", filePath = "C:/fixture.pbix" },
        saveResult = new { modelSaved = true, fileSaved = false, desktopSynchronized = true },
        summary = new { tableCount = 1, measureCount = 2 },
        model = new
        {
            name = "Model",
            compatibilityLevel = 1606,
            culture = "zh-CN",
            relationshipCount = 0,
            roleCount = 0,
            tables = new[] { new { name = "Sales", columns = new[] { new { name = "Amount", kind = "Data" } } } },
            measures = new[] { new { name = "First", table = "Sales", expression = "SECRET_EXPRESSION" }, new { name = "Second", table = "Sales", expression = "2" } },
            functions = Array.Empty<object>()
        }
    }), false, null, false, 7);
}
