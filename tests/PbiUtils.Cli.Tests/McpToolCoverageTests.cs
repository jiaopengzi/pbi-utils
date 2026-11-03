// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpToolCoverageTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用显式全工具用例验证发现完整性、只读业务和服务端权限, 文件写入限独占临时目录.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Cli.Mcp;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>自动门禁不启动桌面; 桌面业务同一清单由 mcp-background-smoke -AllTools 通过真实 IPC 验收.</summary>
public sealed class McpToolCoverageTests : IDisposable
{
    private static readonly string[] Categories = ["success", "write-denied", "input-rejected", "prerequisite-rejected"];
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-coverage-" + Guid.NewGuid().ToString("N"));

    /// <summary>从仓库机器契约读取显式用例; 未找到仓库或 JSON 无效时让测试失败.</summary>
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var item in ReadCases()) data.Add(item.GetProperty("name").GetString()!, item.GetRawText());
        return data;
    }

    /// <summary>返回独立 JSON 用例, 文档关闭后内容继续有效.</summary>
    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Contract("mcp-tool-cases.json")));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone())];
    }

    /// <summary>新增、移除、重名工具和权限变化必须更新人工维护的用例, 不从产品标记推导测试预期.</summary>
    [Fact]
    public async Task ManifestExactlyMatchesDiscoveryAndPermissions()
    {
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        var cases = ReadCases();
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(runtime.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal), cases.Select(item => item.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        foreach (var item in cases)
        {
            var tool = runtime.Tools.Single(tool => tool.Name == item.GetProperty("name").GetString());
            Assert.Equal(item.GetProperty("readOnly").GetBoolean(), tool.Annotations!.ReadOnlyHint);
            Assert.Contains(item.GetProperty("category").GetString(), Categories);
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("note").GetString()));
            if (item.GetProperty("category").GetString() == "success")
            {
                Assert.Empty(item.GetProperty("errorCode").GetString()!);
                Assert.True(item.GetProperty("resultKind").GetString() == "array" || item.GetProperty("fields").GetArrayLength() > 0);
            }
            else Assert.NotEmpty(item.GetProperty("errorCode").GetString()!);
        }
    }

    /// <summary>每个实际写工具必须关联独立正向用例; 执行脚本进一步核验 TRX 中的精确方法和理论展开.</summary>
    [Fact]
    public async Task PositiveWriteManifestExactlyMatchesWritableDiscovery()
    {
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Contract("mcp-write-cases.json")));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var names = cases.Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(runtime.Tools.Where(tool => tool.Annotations!.ReadOnlyHint != true).Select(tool => tool.Name).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
        foreach (var item in cases)
        {
            Assert.False(item.GetProperty("readOnly").GetBoolean());
            Assert.NotEmpty(item.GetProperty("testMethod").GetString()!);
            Assert.NotEmpty(item.GetProperty("expectedCases").EnumerateArray());
            Assert.NotEmpty(item.GetProperty("assertions").EnumerateArray());
            foreach (var assertion in item.GetProperty("assertions").EnumerateArray()) Assert.NotEmpty(assertion.GetString()!);
            Assert.NotEmpty(item.GetProperty("validationLevel").GetString()!);
        }
    }

    /// <summary>每工具独立呈现结果; 核心执行真实读取, 桌面验证包装拒绝, 全部写入口验证无法从参数提权.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryToolHasAnExecutableSafetyCase(string name, string caseJson)
    {
        var item = JsonSerializer.Deserialize<JsonElement>(caseJson);
        await using var runtime = new McpBusinessRuntime(false, Path.Combine(root, "user"), NullLoggerFactory.Instance);
        var fixtureRoot = await CreateFixtureAsync();
        var before = Directory.GetFiles(fixtureRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        var arguments = item.GetProperty("arguments").Deserialize<Dictionary<string, JsonElement>>()!;
        if (arguments.TryGetValue("templateBaseline", out var templateBaseline) && templateBaseline.GetString() == "$templateBaseline")
        {
            var templates = Success(await CallAsync(runtime, "scaffold_templates", new { })).GetProperty("templates");
            arguments["templateBaseline"] = templates.EnumerateArray().Single(template => template.GetProperty("id").GetString() == "builtin:blank").GetProperty("baseline");
        }
        foreach (var key in arguments.Keys.ToArray())
        {
            if (arguments[key].ValueKind != JsonValueKind.String) continue;
            if (arguments[key].GetString() == "$fixture") arguments[key] = JsonSerializer.SerializeToElement(Path.Combine(fixtureRoot, "test.pbip"));
            if (arguments[key].GetString() == "$empty") arguments[key] = JsonSerializer.SerializeToElement(Path.Combine(root, "empty"));
        }
        var expected = item.GetProperty("errorCode").GetString();
        if (name.StartsWith("desktop_", StringComparison.Ordinal) && item.GetProperty("readOnly").GetBoolean())
        {
            arguments = new() { ["unexpected"] = JsonSerializer.SerializeToElement(true) };
            expected = "INVALID_ARGUMENT";
        }
        var result = await CallAsync(runtime, name, arguments);
        if (!string.IsNullOrEmpty(expected)) AssertError(result, expected);
        else
        {
            Assert.False(result.IsError, result.StructuredContent.ToString());
            Assert.NotNull(result.StructuredContent);
            var value = result.StructuredContent.Value;
            Assert.Equal(item.GetProperty("resultKind").GetString() == "array" ? JsonValueKind.Array : JsonValueKind.Object, value.ValueKind);
            foreach (var field in item.GetProperty("fields").EnumerateArray())
                Assert.True(value.TryGetProperty(field.GetString()!, out _), $"{name}: missing {field}");
            foreach (var field in item.GetProperty("equals").EnumerateObject())
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(field.Value.GetRawText()), JsonNode.Parse(value.GetProperty(field.Name).GetRawText())), $"{name}: incorrect {field.Name}");
            foreach (var field in item.GetProperty("nonEmpty").EnumerateArray())
            {
                var content = value.GetProperty(field.GetString()!);
                Assert.True(content.ValueKind == JsonValueKind.Array ? content.GetArrayLength() > 0 : !string.IsNullOrWhiteSpace(content.GetString()));
            }
            if (name == "measure_list")
            {
                var measure = Assert.Single(value.GetProperty("measures").EnumerateArray());
                Assert.Equal("Amount", measure.GetProperty("name").GetString());
                Assert.Equal("Sales", measure.GetProperty("table").GetString());
                Assert.Equal("1", measure.GetProperty("expression").GetString());
            }
        }
        if (!item.GetProperty("readOnly").GetBoolean())
        {
            arguments["allowWrite"] = JsonSerializer.SerializeToElement(true);
            arguments["confirm"] = JsonSerializer.SerializeToElement(true);
            AssertError(await CallAsync(runtime, name, arguments), "MCP_WRITE_DISABLED");
            Assert.False(Directory.Exists(Path.Combine(root, "user")));
        }
        Assert.Equal(before.Count, Directory.GetFiles(fixtureRoot, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, content) in before) Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    /// <summary>本地函数完整 CRUD 使用隔离库, 过期基线和缺少删除确认不能改变已保存内容.</summary>
    [Fact]
    public async Task LocalUdfLifecyclePreservesBaselineAndDeleteConfirmation()
    {
        await using var runtime = new McpBusinessRuntime(true, root, NullLoggerFactory.Instance);
        var definition = new { schemaVersion = 1, name = "Local.McpCoverage", version = "1.0.0", expression = "() => 1", description = "test", dependencies = Array.Empty<object>() };
        var created = Success(await CallAsync(runtime, "udf_local_create", new { definition }));
        var id = created.GetProperty("id").GetString();
        var baseline = created.GetProperty("baseline").GetString();
        var updatedDefinition = definition with { expression = "() => 2" };
        AssertError(await CallAsync(runtime, "udf_local_update", new { id, baseline = new string('0', 64), definition = updatedDefinition }), "UDF_BASELINE_CHANGED");
        var updated = Success(await CallAsync(runtime, "udf_local_update", new { id, baseline, definition = updatedDefinition }));
        var nextBaseline = updated.GetProperty("baseline").GetString();
        Assert.NotEqual(baseline, nextBaseline);
        AssertError(await CallAsync(runtime, "udf_local_delete", new { id, baseline = nextBaseline }), "CONFIRMATION_REQUIRED");
        var read = Success(await CallAsync(runtime, "udf_local_show", new { id }));
        Assert.Equal("() => 2", read.GetProperty("definition").GetProperty("expression").GetString());
        AssertError(await CallAsync(runtime, "udf_local_delete", new { id, baseline, confirm = true }), "UDF_BASELINE_CHANGED");
        Success(await CallAsync(runtime, "udf_local_delete", new { id, baseline = nextBaseline, confirm = true }));
        Assert.Empty(Success(await CallAsync(runtime, "udf_local_list", new { })).GetProperty("items").EnumerateArray());
    }

    /// <summary>登记来源不联网; 更新保持 ID, 旧基线与缺少确认均不能删除配置.</summary>
    [Fact]
    public async Task SourceLifecycleRequiresCurrentBaselineAndConfirmation()
    {
        await using var runtime = new McpBusinessRuntime(true, root, NullLoggerFactory.Instance);
        const string baseUrl = "https://github.com/example/functions";
        var created = Success(await CallAsync(runtime, "udf_source_save", new { name = "Test", baseUrl }));
        var id = created.GetProperty("id").GetString();
        var baseline = created.GetProperty("baseline").GetString();
        var updated = Success(await CallAsync(runtime, "udf_source_save", new { id, baseline, name = "Renamed", baseUrl }));
        Assert.Equal(id, updated.GetProperty("id").GetString());
        var current = updated.GetProperty("baseline").GetString();
        Assert.NotEqual(baseline, current);
        AssertError(await CallAsync(runtime, "udf_source_delete", new { id, baseline = current }), "CONFIRMATION_REQUIRED");
        AssertError(await CallAsync(runtime, "udf_source_delete", new { id, baseline, confirm = true }), "UDF_SOURCE_CHANGED");
        Success(await CallAsync(runtime, "udf_source_delete", new { id, baseline = current, confirm = true }));
        Assert.DoesNotContain(Success(await CallAsync(runtime, "udf_sources", new { })).GetProperty("sources").EnumerateArray(), source => source.GetProperty("id").GetString() == id);
    }

    /// <summary>副本编辑需要精确批准且只能应用一次, 原始夹具文件保持逐字不变.</summary>
    [Fact]
    public async Task CopyChangeRequiresExactOneTimeApprovalAndPreservesSource()
    {
        await using var runtime = new McpBusinessRuntime(true, Path.Combine(root, "user"), NullLoggerFactory.Instance);
        var fixtureRoot = await CreateFixtureAsync();
        var original = await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "model", "model.bim"));
        var plan = Success(await CallAsync(runtime, "change_plan", new
        {
            projectPath = Path.Combine(fixtureRoot, "test.pbip"),
            outputPath = Path.Combine(root, "copy"),
            operation = "measure.set",
            objectName = "Amount",
            property = "description",
            value = "MCP copy test",
            table = "Sales"
        }));
        var planId = plan.GetProperty("id").GetString();
        var approve = plan.GetProperty("digest").GetString();
        AssertError(await CallAsync(runtime, "change_apply", new { planId, approve = "wrong" }), "APPROVAL_MISMATCH");
        var result = Success(await CallAsync(runtime, "change_apply", new { planId, approve }));
        var read = Success(await CallAsync(runtime, "measure_list", new { project = result.GetProperty("projectPath").GetString() }));
        Assert.Contains(read.GetProperty("measures").EnumerateArray(), measure => measure.GetProperty("description").GetString() == "MCP copy test");
        AssertError(await CallAsync(runtime, "change_apply", new { planId, approve }), "PLAN_NOT_FOUND");
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "model", "model.bim")));
    }

    /// <summary>示例生成、增量和清理限临时目录; 删除需要独立确认并保留无关文件.</summary>
    [Fact]
    public async Task SampleLifecyclePreservesUnrelatedFilesAndRequiresConfirmation()
    {
        await using var runtime = new McpBusinessRuntime(true, Path.Combine(root, "user"), NullLoggerFactory.Instance);
        var output = Path.Combine(root, "sample");
        var options = new { locale = "en-us", productCount = 1, storeCount = 1, inventoryCycle = 5, startDate = "2024-01-01", endDate = "2024-03-02", seed = 42 };
        Success(await CallAsync(runtime, "sample_generate", new { output, options }));
        Assert.True(Success(await CallAsync(runtime, "sample_inspect", new { output })).GetProperty("hasData").GetBoolean());
        Success(await CallAsync(runtime, "sample_append", new { output, options = options with { startDate = "2024-04-01", endDate = "2024-06-01" } }));
        var retained = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(retained, "unrelated");
        AssertError(await CallAsync(runtime, "sample_clear", new { output }), "CONFIRMATION_REQUIRED");
        Assert.True(Success(await CallAsync(runtime, "sample_inspect", new { output })).GetProperty("hasData").GetBoolean());
        Success(await CallAsync(runtime, "sample_clear", new { output, confirm = true }));
        Assert.False(Success(await CallAsync(runtime, "sample_inspect", new { output })).GetProperty("hasData").GetBoolean());
        Assert.Equal("unrelated", await File.ReadAllTextAsync(retained));
    }

    /// <summary>将固定 PBIP JSON 夹具写入测试独占目录, 返回目录; I/O 失败直接使测试失败.</summary>
    private async Task<string> CreateFixtureAsync()
    {
        var fixtureRoot = Path.Combine(root, "fixture");
        var files = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(Contract("mcp-fixture.json")))!;
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(fixtureRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content.GetRawText());
        }
        return fixtureRoot;
    }

    /// <summary>只调用运行时公开分发边界, 让参数序列化和授权与产品一致.</summary>
    private static ValueTask<CallToolResult> CallAsync(McpBusinessRuntime runtime, string name, object arguments) => runtime.CallAsync(new CallToolRequestParams
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(arguments).Deserialize<Dictionary<string, JsonElement>>()
    }, CancellationToken.None);

    /// <summary>验证业务成功后取结构化内容, 任何失败保留原始测试诊断.</summary>
    private static JsonElement Success(CallToolResult result)
    {
        Assert.False(result.IsError, result.StructuredContent.ToString());
        return result.StructuredContent!.Value;
    }

    /// <summary>负向用例必须匹配明确错误码, 不将任意异常视为通过.</summary>
    private static void AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError, result.StructuredContent.ToString());
        Assert.Equal(code, result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>从测试程序集向上寻找当前仓库契约, 不读取个人历史证据.</summary>
    private static string Contract(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "contracts", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("MCP test contract not found", name);
    }

    /// <summary>仅删除测试独占的随机临时根, 不触碰真实应用目录或用户模型.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
