// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpCatalogWriteTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : MCP 目录刷新使用受控 Git HTTP 字节和真实隔离快照事务, 验证成功发布、读取及失败保留.

using PbiUtils.Infrastructure.Execution;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Cli.Mcp;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.UnitTests;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>只替换外部 HTTP, 权限、Git 校验、来源基线、磁盘快照和回执均走正式服务.</summary>
public sealed class McpCatalogWriteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-catalog-write-" + Guid.NewGuid().ToString("N"));

    /// <summary>刷新先提交完整目录再返回成功; 网络失败不能覆盖已有快照, 后续只读调用不隐式联网.</summary>
    [Fact]
    public async Task RefreshPublishesReadableSnapshotAndFailedRefreshPreservesIt()
    {
        var fixture = new GitSmartUdfFixture();
        var fail = false;
        var requests = 0;
        await using var runtime = new McpBusinessRuntime(true, root, NullLoggerFactory.Instance, _ => new HttpClient(new Handler(request =>
        {
            requests++;
            return fail ? new(HttpStatusCode.ServiceUnavailable) : fixture.Respond(request);
        })));
        var source = Success(await CallAsync(runtime, "udf_source_save", new { name = "Isolated Git", baseUrl = "https://gitee.com/example/functions" }));
        var arguments = new { source = source.GetProperty("id").GetString(), sourceBaseline = source.GetProperty("baseline").GetString() };
        var before = Success(await CallAsync(runtime, "udf_catalog_status", arguments));
        Assert.False(before.GetProperty("initialized").GetBoolean());
        Assert.Equal(0, requests);
        var refreshed = Success(await CallAsync(runtime, "udf_catalog_refresh", arguments));
        Assert.True(refreshed.GetProperty("initialized").GetBoolean());
        Assert.Equal(1, refreshed.GetProperty("packageCount").GetInt32());
        Assert.Equal(1, refreshed.GetProperty("versionCount").GetInt32());
        Assert.NotEmpty(fixture.Requests);
        var baseline = refreshed.GetProperty("baseline").GetString();
        var countAfterRefresh = requests;
        var search = Success(await CallAsync(runtime, "udf_catalog_search", new { arguments.source, arguments.sourceBaseline, query = "Example" }));
        Assert.Single(search.GetProperty("packages").EnumerateArray());
        var versions = Success(await CallAsync(runtime, "udf_catalog_versions", new { arguments.source, arguments.sourceBaseline, packageId = "Example.One" }));
        Assert.NotEmpty(versions.GetProperty("versions").EnumerateArray());
        var package = Success(await CallAsync(runtime, "udf_catalog_show", new { arguments.source, arguments.sourceBaseline, packageId = "Example.One", version = "1.0.0" }));
        Assert.Contains("Example.Helper", package.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(countAfterRefresh, requests);
        var completed = new ExecutionJournal(root).List().Single(view => view.Record.Method == "udf_catalog_refresh");
        Assert.Equal("completed", completed.Record.State);
        fail = true;
        var failed = await CallAsync(runtime, "udf_catalog_refresh", arguments);
        Assert.True(failed.IsError);
        Assert.Equal("UDF_GIT_HTTP_FAILED", failed.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(baseline, Success(await CallAsync(runtime, "udf_catalog_status", arguments)).GetProperty("baseline").GetString());
        Assert.Equal(2, new ExecutionJournal(root).List().Count(view => view.Record.Method == "udf_catalog_refresh"));
    }

    /// <summary>调用实际 MCP 分发入口, 未绕过权限与业务回执.</summary>
    private static ValueTask<CallToolResult> CallAsync(McpBusinessRuntime runtime, string name, object arguments)
        => runtime.CallAsync(new CallToolRequestParams { Name = name, Arguments = JsonSerializer.SerializeToElement(arguments).Deserialize<Dictionary<string, JsonElement>>() }, default);

    /// <summary>成功必须带非错误的结构化结果, 失败输出原诊断便于定位.</summary>
    private static JsonElement Success(CallToolResult result)
    {
        Assert.False(result.IsError, result.StructuredContent.ToString());
        return result.StructuredContent!.Value;
    }

    /// <summary>明确替换外部传输; 未配置地址由独立 Git 夹具抛错, 不回退真实网络.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>测试服务排空后仅清理随机独占目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
