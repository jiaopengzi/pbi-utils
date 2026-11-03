// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpSchemaTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP 发布 schema 的参数域、条件禁止及真实只读输入边界.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Cli.Mcp;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>仅使用隔离应用目录和本地格式化, 不连接或修改模型.</summary>
public sealed class McpSchemaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-schema-" + Guid.NewGuid().ToString("N"));

    /// <summary>并集转换不得覆盖已有组合约束, 本地引用路径、额外字段开关和业务字面 JSON 保持不变.</summary>
    [Fact]
    public void CompositionReferencesAndLiteralDataRemainUnchanged()
    {
        using var source = JsonDocument.Parse("""
            {
                "type": ["object", "null"],
                "anyOf": [{"required": ["item"]}, {"type": "null"}],
                "allOf": [{"not": {"const": false}}],
                "$defs": {"nullable": {"type": ["string", "null"]}, "blocked": false},
                "properties": {
                    "item": {"$ref": "#/$defs/nullable"},
                    "blocked": {"$ref": "#/$defs/blocked"},
                    "type": {"enum": [false, {"type": ["integer", "null"]}]},
                    "free": true
                },
                "additionalProperties": false,
                "default": {"type": ["integer", "null"], "properties": {"forbidden": false}},
                "examples": [false, {"anyOf": [true]}]
            }
            """);
        var original = source.RootElement;
        var result = McpInputSchema.CreatePortable(original);
        foreach (var key in new[] { "anyOf", "default", "examples" })
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty(key).GetRawText()), JsonNode.Parse(result.GetProperty(key).GetRawText())));
        Assert.Equal(2, result.GetProperty("allOf").GetArrayLength());
        Assert.False(result.GetProperty("allOf")[0].GetProperty("not").GetProperty("const").GetBoolean());
        Assert.Equal(["object", "null"], Types(result.GetProperty("allOf")[1]));
        Assert.Equal("#/$defs/nullable", result.GetProperty("properties").GetProperty("item").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/blocked", result.GetProperty("properties").GetProperty("blocked").GetProperty("$ref").GetString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty("properties").GetProperty("type").GetRawText()), JsonNode.Parse(result.GetProperty("properties").GetProperty("type").GetRawText())));
        Assert.False(result.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["null", "boolean", "object", "array", "number", "string"], Types(result.GetProperty("properties").GetProperty("free")));
        Assert.Empty(result.GetProperty("$defs").GetProperty("blocked").GetProperty("not").EnumerateObject());
        Assert.Equal(JsonValueKind.False, original.GetProperty("$defs").GetProperty("blocked").ValueKind);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(result.GetRawText()), JsonNode.Parse(McpInputSchema.CreatePortable(result).GetRawText())));
    }

    /// <summary>数组、条件和属性名等标准子 schema 位置都保留恒真/恒假含义, 不遗留裸 schema 布尔值.</summary>
    [Theory]
    [InlineData("items")]
    [InlineData("contains")]
    [InlineData("propertyNames")]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("contentSchema")]
    public void BooleanSubschemasRemainAlwaysFalse(string keyword)
    {
        var original = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [keyword] = false });
        var result = McpInputSchema.CreatePortable(original);
        Assert.Empty(result.GetProperty(keyword).GetProperty("not").EnumerateObject());
    }

    /// <summary>生成器的数字字符串及可空数组通过显式并集公开, 必填项和额外字段限制保持.</summary>
    [Fact]
    public async Task GeneratedSchemasPreserveNumericStringsNullAndRequiredFields()
    {
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        var schema = runtime.Tools.Single(tool => tool.Name == "calendar_preview").InputSchema;
        var options = schema.GetProperty("properties").GetProperty("options").GetProperty("properties");
        Assert.Equal(["string", "integer"], Types(options.GetProperty("startYear")));
        Assert.Equal(["array", "null"], Types(options.GetProperty("workdays")));
        Assert.Equal(["string", "integer"], Types(options.GetProperty("workdays").GetProperty("items")));
        var format = runtime.Tools.Single(tool => tool.Name == "format_code").InputSchema;
        Assert.False(format.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["language", "text"], format.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>条件禁止字段仍为恒假 schema, 任意 JSON 草稿基础域保持完整, 原 bridge 契约不被修改.</summary>
    [Fact]
    public async Task DesktopSchemasPreserveForbiddenFieldsAndAnyJsonDraftValue()
    {
        var original = McpDesktopCatalog.All.Single(operation => operation.Method == "scaffold.updateDraft").InputSchema.GetRawText();
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        var udf = runtime.Tools.Single(tool => tool.Name == "desktop_udf_catalog").InputSchema;
        var forbidden = udf.GetProperty("properties").GetProperty("parameters").GetProperty("allOf")[0].GetProperty("then").GetProperty("properties").GetProperty("query");
        Assert.Equal(JsonValueKind.Object, forbidden.ValueKind);
        Assert.Empty(forbidden.GetProperty("not").EnumerateObject());
        var draft = runtime.Tools.Single(tool => tool.Name == "desktop_scaffold_updateDraft").InputSchema;
        Assert.Equal(["null", "boolean", "object", "array", "number", "string"], Types(draft.GetProperty("properties").GetProperty("parameters").GetProperty("properties").GetProperty("value")));
        Assert.Equal(original, McpDesktopCatalog.All.Single(operation => operation.Method == "scaffold.updateDraft").InputSchema.GetRawText());
        Assert.False(runtime.Tools.Single(tool => tool.Name == "desktop_scaffold_updateDraft").Annotations!.ReadOnlyHint);
    }

    /// <summary>发布表示变化不放宽实际反序列化, null/缺字段/未知字段/错类型仍拒绝; 失败后正常调用可继续.</summary>
    [Theory]
    [InlineData("{\"language\":\"dax\"}")]
    [InlineData("{\"language\":\"dax\",\"text\":null}")]
    [InlineData("{\"language\":\"dax\",\"text\":42}")]
    [InlineData("{\"language\":\"dax\",\"text\":\"1\",\"allowWrite\":true}")]
    public async Task InvalidInputsRemainRejectedAndDoNotEnableWrites(string json)
    {
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        var invalid = await runtime.CallAsync(new CallToolRequestParams { Name = "format_code", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }, CancellationToken.None);
        Assert.True(invalid.IsError);
        Assert.Equal("INVALID_ARGUMENT", invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        var denied = await runtime.CallAsync(new CallToolRequestParams { Name = "desktop_model_edit", Arguments = new Dictionary<string, JsonElement>() }, CancellationToken.None);
        Assert.Equal("MCP_WRITE_DISABLED", denied.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        var valid = await runtime.CallAsync(new CallToolRequestParams { Name = "format_code", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"language\":\"dax\",\"text\":\"SUM(Sales[Amount])\"}") }, CancellationToken.None);
        Assert.False(valid.IsError);
        Assert.Equal("SUM ( Sales[Amount] )", valid.StructuredContent!.Value.GetProperty("text").GetString());
    }

    /// <summary>读取发布的单类型并集; 缺少公开类型由断言报告.</summary>
    private static IEnumerable<string?> Types(JsonElement schema) => schema.GetProperty("anyOf").EnumerateArray().Select(branch => branch.GetProperty("type").GetString());

    /// <summary>仅回收本用例独占临时目录, 不触碰实际用户配置或模型文件.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
