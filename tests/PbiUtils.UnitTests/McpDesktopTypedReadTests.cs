// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpDesktopTypedReadTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以真实宿主 DTO 验证 MCP 摘要、分页及已完成事实, 不先序列化测试返回值.

using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅替代模型上下文, 执行正式 MCP 分发/投影/输出; 不启动产品进程或读写真实模型.</summary>
public sealed class McpDesktopTypedReadTests
{
    /// <summary>真实 ProjectSnapshot 默认经过摘要, 保存事实和观察代际仍可用于下一次请求.</summary>
    [Fact]
    public async Task TypedSnapshotDefaultsToSummaryAndKeepsSaveFactsAndRevision()
    {
        var snapshot = Snapshot();
        var context = new Context(new(snapshot, false, null, false, 7));
        await using var runtime = Runtime(context);

        var inspected = await CallAsync(runtime, "desktop_project_inspect", new { parameters = new { } });
        Assert.False(inspected.IsError);
        var summary = inspected.StructuredContent!.Value;
        Assert.Equal("summary", summary.GetProperty("projection").GetString());
        Assert.Equal("p", summary.GetProperty("projectId").GetString());
        Assert.Equal("b", summary.GetProperty("baselineId").GetString());
        Assert.Equal(3, summary.GetProperty("summary").GetProperty("tableCount").GetInt32());
        Assert.False(summary.GetProperty("model").TryGetProperty("tables", out _));
        Assert.False(summary.GetProperty("model").TryGetProperty("measures", out _));
        Assert.DoesNotContain("SECRET_EXPRESSION", summary.GetRawText());
        Assert.True(summary.GetProperty("saveResult").GetProperty("modelSaved").GetBoolean());
        Assert.False(summary.GetProperty("saveResult").GetProperty("fileSaved").GetBoolean());
        Assert.True(summary.GetProperty("saveResult").GetProperty("desktopSynchronized").GetBoolean());

        var saved = await CallAsync(runtime, "desktop_model_save", new { parameters = new { } });
        Assert.False(saved.IsError);
        Assert.Equal("summary", saved.StructuredContent!.Value.GetProperty("projection").GetString());
        Assert.Equal(new long?[] { null, 7 }, context.Observations);
        Assert.Equal(7, new McpDesktopRead().Apply(context.Result).ConnectionRevision);
        Assert.Equal("SECRET_EXPRESSION", snapshot.Model.Measures[0].Expression);
    }

    /// <summary>真实表分页遵守 limit/offset 和目标基线, 不泄露表列或度量完整定义.</summary>
    [Fact]
    public async Task TypedTablePagesRemainBoundedAndRejectChangedIdentity()
    {
        await using var runtime = Runtime(new(new(Snapshot(), false, null, false, 7)));
        var first = await CallAsync(runtime, "desktop_project_inspect", new { parameters = new { }, read = new { section = "tables", limit = 2, offset = 0 } });
        Assert.False(first.IsError);
        var page = first.StructuredContent!.Value;
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
        Assert.Equal(1, page.GetProperty("items")[0].GetProperty("columnCount").GetInt32());
        Assert.False(page.GetProperty("items")[0].TryGetProperty("columns", out _));
        Assert.DoesNotContain("SECRET_EXPRESSION", page.GetRawText());
        var second = await CallAsync(runtime, "desktop_project_inspect", new { parameters = new { }, read = new { section = "tables", limit = 2, offset = 2, projectId = "p", baselineId = "b" } });
        Assert.False(second.IsError);
        Assert.Single(second.StructuredContent!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.StructuredContent.Value.GetProperty("nextOffset").ValueKind);
        var stale = await CallAsync(runtime, "desktop_project_inspect", new { parameters = new { }, read = new { section = "tables", limit = 2, offset = 2, projectId = "other", baselineId = "b" } });
        Assert.True(stale.IsError);
        Assert.Equal("STALE_BASELINE", stale.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>真实度量值定义仅显式请求时返回, UTF-8 超预算不截断或静默返回全部模型.</summary>
    [Fact]
    public void TypedDefinitionsRequireOptInAndHonorByteBudget()
    {
        var source = new McpDesktopResult(Snapshot(), false, null, false, 7);
        var hidden = Assert.IsType<JsonElement>(new McpDesktopRead("measures", Limit: 1).Apply(source).Value);
        Assert.Single(hidden.GetProperty("items").EnumerateArray());
        Assert.False(hidden.GetProperty("items")[0].TryGetProperty("expression", out _));
        var included = Assert.IsType<JsonElement>(new McpDesktopRead("measures", Limit: 1, IncludeExpressions: true).Apply(source).Value);
        Assert.Equal("SECRET_EXPRESSION", included.GetProperty("items")[0].GetProperty("expression").GetString());
        var oversized = source with { Value = Snapshot(new string('界', McpDesktopRead.MaximumBytes / 2)) };
        Assert.Equal("OUTPUT_TOO_LARGE", Assert.Throws<AppException>(() => new McpDesktopRead("measures", IncludeExpressions: true).Apply(oversized)).Code);
    }

    /// <summary>配置真实包装类型保留已完成/可写事实, 默认不回传定义, 显式配置页保留配置字段.</summary>
    [Fact]
    public async Task TypedConfigurationKeepsFactsAndExplicitConfiguration()
    {
        var value = ModelConfigurationContext.Create("target", Snapshot(), false) with { NoChanges = true };
        await using var runtime = Runtime(new(new(value, false, null, false, 7)));
        var summaryResult = await CallAsync(runtime, "desktop_configuration_context", new { parameters = new { target = "current" } });
        Assert.False(summaryResult.IsError);
        var summary = summaryResult.StructuredContent!.Value;
        Assert.Equal("target", summary.GetProperty("targetId").GetString());
        Assert.True(summary.GetProperty("canWrite").GetBoolean());
        Assert.True(summary.GetProperty("noChanges").GetBoolean());
        Assert.Equal("summary", summary.GetProperty("project").GetProperty("projection").GetString());
        Assert.False(summary.TryGetProperty("details", out _));
        Assert.DoesNotContain("SECRET_EXPRESSION", summary.GetRawText());

        var configured = await CallAsync(runtime, "desktop_configuration_context", new { parameters = new { target = "current" }, read = new { section = "configuration" } });
        Assert.False(configured.IsError);
        Assert.Equal("configuration", configured.StructuredContent!.Value.GetProperty("projection").GetString());
        Assert.Equal(3, configured.StructuredContent.Value.GetProperty("details").GetProperty("tables").GetArrayLength());
        Assert.False(configured.StructuredContent.Value.GetProperty("project").GetProperty("model").TryGetProperty("measures", out _));
        Assert.Equal(3, value.Project.Model.Tables.Count);
    }

    /// <summary>真实打开工程包装须保留 opened 事实, 内部 EntryPath 的 JsonIgnore 保持有效.</summary>
    [Fact]
    public async Task TypedDesktopOpenKeepsOpenedFactWhileSummarizingProject()
    {
        var opened = new ScaffoldBridge.DesktopOpenResult(true, Snapshot(), "PRIVATE_ENTRY_PATH");
        await using var runtime = Runtime(new(new(opened, false, null, false, 7)));
        var result = await CallAsync(runtime, "desktop_scaffold_openDesktop", new { parameters = new { } });
        Assert.False(result.IsError);
        var summary = result.StructuredContent!.Value;
        Assert.True(summary.GetProperty("opened").GetBoolean());
        Assert.Equal("summary", summary.GetProperty("project").GetProperty("projection").GetString());
        Assert.False(summary.GetProperty("project").GetProperty("model").TryGetProperty("tables", out _));
        Assert.DoesNotContain("PRIVATE_ENTRY_PATH", summary.GetRawText());
        Assert.DoesNotContain("SECRET_EXPRESSION", summary.GetRawText());
    }

    /// <summary>只读展示超限明确失败, 已完成写入的事实不因同样展示预算变成失败.</summary>
    [Fact]
    public async Task TypedCompletedWriteRetainsFactsBeyondReadSummaryBudget()
    {
        var snapshot = Snapshot() with { Warnings = [new string('x', McpDesktopRead.MaximumBytes)] };
        await using var runtime = Runtime(new(new(snapshot, false, null, false, 7)));
        var inspected = await CallAsync(runtime, "desktop_project_inspect", new { parameters = new { } });
        Assert.True(inspected.IsError);
        Assert.Equal("OUTPUT_TOO_LARGE", inspected.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        var saved = await CallAsync(runtime, "desktop_model_save", new { parameters = new { } });
        Assert.False(saved.IsError);
        Assert.True(saved.StructuredContent!.Value.GetProperty("saveResult").GetProperty("modelSaved").GetBoolean());
        Assert.Equal("summary", saved.StructuredContent.Value.GetProperty("projection").GetString());
    }

    /// <summary>失败或待核验强类型结果不进行展示投影, 原对象、错误和代际保持不变.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TypedFailedOrUncertainResultsRemainUntouched(bool failed, bool uncertain)
    {
        var source = new McpDesktopResult(Snapshot(), failed, "SAVE_FAILED", uncertain, 7);
        Assert.Same(source, new McpDesktopRead().Apply(source));
    }

    /// <summary>成功值不满足快照形状时保持原对象, 可空或非对象 model 不得误触发 JSON 属性异常.</summary>
    [Fact]
    public void SuccessfulNonSnapshotValuesRemainUntouched()
    {
        object?[] values = [null, true, new { version = "fixture" }, new { model = (object?)null },
            new { model = "label" }, new { project = new { model = (object?)null } }, new { model = new { tables = (object?)null } }];
        foreach (var value in values)
        {
            var source = new McpDesktopResult(value, false, null, false, 7);
            Assert.Same(source, new McpDesktopRead().Apply(source));
        }
    }

    /// <summary>构造生产实际使用的不可变 DTO, 保留用于泄露检测的表达式和成功 Desktop 保存状态.</summary>
    private static ProjectSnapshot Snapshot(string expression = "SECRET_EXPRESSION") => new("fixture.pbix", "p", "b", "TMDL", "PBIR", "1", null,
        new("Model", 1606, "en-US", [new("Sales", null, false, [new("Amount", "Data", "Decimal", false, null)], []),
            new("Dates", null, false, [], []), new("Products", null, false, [], [])],
            [new("Sales", "First", expression, null, null, null, false), new("Sales", "Second", "2", null, null, null, false)], [], 0, 0),
        [], [], [], new("desktop", "Fixture"), SaveResult: new(true, false, true, ModelSaveResult.ConnectedDesktopMessage));

    /// <summary>创建正式运行时, 仅注入无外部访问的类型化上下文.</summary>
    private static McpBusinessRuntime Runtime(Context context) => new(true, Path.GetTempPath(), NullLoggerFactory.Instance, desktopContext: context);

    /// <summary>从工具协议入口到最终 structuredContent 验证, 不预先序列化上下文返回的 DTO.</summary>
    private static async Task<CallToolResult> CallAsync(McpBusinessRuntime runtime, string name, object input)
    {
        var arguments = JsonSerializer.SerializeToElement(input);
        return await runtime.CallAsync(new() { Name = name, Arguments = arguments.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone()) }, default);
    }

    /// <summary>模型边界替身只返回原始 DTO 与真实信封形状, 不复制投影、权限或输出逻辑.</summary>
    private sealed class Context(McpDesktopResult result) : IMcpDesktopContext
    {
        public string SessionId => "typed-context";
        internal McpDesktopResult Result { get; } = result;
        internal List<long?> Observations { get; } = [];

        /// <summary>记录运行时观察代际并返回同一强类型结果, 取消保持明确失败.</summary>
        public Task<McpDesktopResult> InvokeAsync(string method, JsonElement parameters, long? connectionRevision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observations.Add(connectionRevision);
            return Task.FromResult(Result);
        }
    }
}
