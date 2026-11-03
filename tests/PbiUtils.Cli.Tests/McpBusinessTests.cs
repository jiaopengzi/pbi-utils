// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpBusinessTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在独占临时工程验证真实 MCP 脚手架创建、日期配置、精确摘要和过期基线拒绝.

using PbiUtils.Infrastructure.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>只修改本测试创建的 PBIP, 不连接用户 Desktop 或业务数据源.</summary>
[Collection("CLI User Data")]
public sealed class McpBusinessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-business-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> scaffoldDigests = [];

    /// <summary>创建测试专属父目录.</summary>
    public McpBusinessTests() => Directory.CreateDirectory(root);

    /// <summary>同后台其他客户端即使取得摘要也不能采用或丢弃本客户端预览, 原客户端仍可正常发布.</summary>
    [Fact]
    public async Task ScaffoldPlansBelongToThePreparingClient()
    {
        await using var owner = new McpWireClient("--allow-write");
        await using var other = new McpWireClient("--allow-write");
        var templates = await ToolAsync(owner, "scaffold_templates", new { });
        var template = templates.GetProperty("templates").EnumerateArray().Single(value => value.GetProperty("id").GetString() == "builtin:blank");
        var request = new { schemaVersion = 1, templateId = "builtin:blank", templateBaseline = template.GetProperty("baseline").GetString(), projectName = "OwnedPlan", locale = "zh-cn" };
        var output = Path.Combine(root, "OwnedPlan");
        var plan = await ToolAsync(owner, "scaffold_plan", new { request, output });
        var approve = plan.GetProperty("digest").GetString()!;
        scaffoldDigests.Add(approve);
        Assert.Equal("PLAN_NOT_FOUND", await ToolErrorAsync(other, "scaffold_create", new { request, output, approve }));
        Assert.Equal("INVALID_SCAFFOLD_PLAN", await ToolErrorAsync(other, "scaffold_discard", new { digest = approve }));
        Assert.Equal("INVALID_SCAFFOLD_PLAN", await ToolErrorAsync(owner, "scaffold_discard", new { digest = new string('0', 64) }));
        Assert.False(Directory.Exists(output));
        var created = await ToolAsync(owner, "scaffold_create", new { request, output, approve });
        Assert.True(created.GetProperty("projectCreated").GetBoolean());
    }

    /// <summary>非法格式与合法未知摘要必须明确拒绝, 不以 discarded=false 伪装成成功或访问其他计划.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("../other")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task DiscardRejectsInvalidOrUnknownDigestBeforePlanAccess(string digest)
    {
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance);
        var result = await runtime.CallAsync(new() { Name = "scaffold_discard", Arguments = new Dictionary<string, JsonElement> { ["digest"] = JsonSerializer.SerializeToElement(digest) } }, default);
        Assert.True(result.IsError);
        Assert.Equal("INVALID_SCAFFOLD_PLAN", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>真实 MCP 进程创建工程后添加日期表; 预览无副作用, 错摘要和重复提交不得写入.</summary>
    [Fact]
    public async Task CreatesProjectAndAppliesCalendarWithExactOneTimePlan()
    {
        await using var client = new McpWireClient("--allow-write");
        await client.DiscoverAsync();
        var created = await CreateProjectAsync(client, "CalendarProject");
        var project = created.GetProperty("projectPath").GetString()!;
        var before = await ToolAsync(client, "project_inspect", new { project });
        var baseline = before.GetProperty("baselineId").GetString()!;
        var plan = await ToolAsync(client, "configuration_plan", new { project, baseline, request = new { calendar = new { kind = "simple", startYear = 2024, endYear = 2025, tableName = "McpCalendar" } } });
        var unchanged = await ToolAsync(client, "project_inspect", new { project });
        Assert.Equal(baseline, unchanged.GetProperty("baselineId").GetString());
        var planId = plan.GetProperty("planId").GetString()!;
        var approve = plan.GetProperty("digest").GetString()!;
        var wrong = await ToolErrorAsync(client, "configuration_apply", new { planId, approve = "wrong" });
        Assert.Equal("APPROVAL_MISMATCH", wrong);
        await ToolAsync(client, "configuration_apply", new { planId, approve });
        var after = await ToolAsync(client, "project_inspect", new { project });
        Assert.NotEqual(baseline, after.GetProperty("baselineId").GetString());
        Assert.Contains(after.GetProperty("model").GetProperty("tables").EnumerateArray(), table => table.GetProperty("name").GetString() == "McpCalendar");
        Assert.Equal("PLAN_NOT_FOUND", await ToolErrorAsync(client, "configuration_apply", new { planId, approve }));
        Assert.Equal(0, await client.CloseAsync());
    }

    /// <summary>预览完成后用户改变原文件时禁止覆盖; 失败计划也不能再次应用.</summary>
    [Fact]
    public async Task ConfigurationRejectsExternalChangesAndConsumesFailedPlan()
    {
        await using var client = new McpWireClient("--allow-write");
        await client.DiscoverAsync();
        var created = await CreateProjectAsync(client, "StaleProject");
        var project = created.GetProperty("projectPath").GetString()!;
        var before = await ToolAsync(client, "project_inspect", new { project });
        var plan = await ToolAsync(client, "configuration_plan", new { project, baseline = before.GetProperty("baselineId").GetString(), request = new { calendar = new { kind = "simple", startYear = 2024, endYear = 2025, tableName = "McpCalendar" } } });
        var planId = plan.GetProperty("planId").GetString()!;
        var approve = plan.GetProperty("digest").GetString()!;
        var modelFile = Directory.GetFiles(root, "model.tmdl", SearchOption.AllDirectories).Single();
        var concurrentContent = await File.ReadAllTextAsync(modelFile) + "\nannotation McpConcurrentTest = user-edit\n";
        await File.WriteAllTextAsync(modelFile, concurrentContent);
        Assert.Equal("STALE_BASELINE", await ToolErrorAsync(client, "configuration_apply", new { planId, approve }));
        Assert.Equal(concurrentContent, await File.ReadAllTextAsync(modelFile));
        Assert.Equal("PLAN_NOT_FOUND", await ToolErrorAsync(client, "configuration_apply", new { planId, approve }));
    }

    /// <summary>通过同一协议发现模板、预览并创建一个无数据源访问的空白工程.</summary>
    private async Task<JsonElement> CreateProjectAsync(McpWireClient client, string name)
    {
        var templates = await ToolAsync(client, "scaffold_templates", new { });
        var template = templates.GetProperty("templates").EnumerateArray().Single(value => value.GetProperty("id").GetString() == "builtin:blank");
        var request = new { schemaVersion = 1, templateId = "builtin:blank", templateBaseline = template.GetProperty("baseline").GetString(), projectName = name, locale = "zh-cn" };
        var output = Path.Combine(root, name);
        var plan = await ToolAsync(client, "scaffold_plan", new { request, output });
        Assert.False(Directory.Exists(output));
        var approve = plan.GetProperty("digest").GetString()!;
        scaffoldDigests.Add(approve);
        var created = await ToolAsync(client, "scaffold_create", new { request, output, approve });
        Assert.True(created.GetProperty("projectCreated").GetBoolean());
        Assert.True(created.GetProperty("verified").GetBoolean());
        return created;
    }

    /// <summary>要求完整工具成功并返回可独立保留的结构化数据.</summary>
    private static async Task<JsonElement> ToolAsync(McpWireClient client, string name, object arguments)
    {
        var response = await client.RequestAsync("tools/call", new { name, arguments });
        Assert.True(response.TryGetProperty("result", out var result), response.ToString());
        Assert.False(result.GetProperty("isError").GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent").Clone();
    }

    /// <summary>要求工具业务错误并读取稳定错误码, 不忽略协议失败.</summary>
    private static async Task<string?> ToolErrorAsync(McpWireClient client, string name, object arguments)
    {
        var response = await client.RequestAsync("tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>仅清理测试登记的脚手架回执和独占临时根, 不枚举或清空其他用户数据.</summary>
    public void Dispose()
    {
        foreach (var digest in scaffoldDigests)
        {
            var path = Path.Combine(RuntimeEnvironment.UserDataDirectory(), "scaffold", "plans", digest + ".json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var output = document.RootElement.GetProperty("plan").GetProperty("outputPath").GetString()!;
            if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(path);
            var lease = Path.ChangeExtension(path, ".lock");
            if (File.Exists(lease)) File.Delete(lease);
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
