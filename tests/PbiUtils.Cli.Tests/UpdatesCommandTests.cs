// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\UpdatesCommandTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 隔离验证 CLI/MCP 更新查询与失败分类, 不联网或打开浏览器.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Commands;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Updates;
using Xunit;

namespace PbiUtils.Cli.Tests;

public sealed class UpdatesCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-update-command-" + Guid.NewGuid().ToString("N"));

    /// <summary>显式双源查询通过同一服务, 没有本产品发布与失败保持独立结果.</summary>
    [Fact]
    public async Task CliQueriesRequestedSourceAndRejectsNetworkFailure()
    {
        var source = new Releases();
        var service = new ProductUpdateService(new LocalProductUpdateStore(root), source, "0.1.0");
        var result = await ProductUpdateCommands.RunAsync(["updates", "check", "--source", "gitee", "--json"], service, default);
        Assert.Equal("gitee", source.LastSource);
        Assert.Equal("noRelease", result.Check!.Status);
        source.Fail = true;
        var error = await Assert.ThrowsAsync<AppException>(() => ProductUpdateCommands.RunAsync(["updates", "check"], service, default));
        Assert.Equal("UPDATE_NETWORK_FAILED", error.Code);
    }

    /// <summary>拒绝重复、不适用参数及任意源地址, 不启动网络.</summary>
    [Theory]
    [InlineData("updates check --source github --source gitee")]
    [InlineData("updates check --source https://evil.test")]
    [InlineData("updates check --json --json")]
    [InlineData("updates download")]
    [InlineData("updates check --source")]
    public async Task CliRejectsInvalidArguments(string command)
    {
        var source = new Releases();
        var service = new ProductUpdateService(new LocalProductUpdateStore(root), source, "0.1.0");
        await Assert.ThrowsAsync<AppException>(() => ProductUpdateCommands.RunAsync(command.Split(' '), service, default));
        Assert.Null(source.LastSource);
    }

    /// <summary>只读 MCP 无需写权限; 网络失败返回工具错误并保留结果, 没有浏览器/安装工具.</summary>
    [Fact]
    public async Task McpReadOnlyQueryPreservesFailedOutcome()
    {
        var source = new Releases();
        var service = new ProductUpdateService(new LocalProductUpdateStore(root), source, "0.1.0");
        await using var runtime = new McpBusinessRuntime(false, root, NullLoggerFactory.Instance, productUpdates: service);
        var tool = Assert.Single(runtime.Tools, tool => tool.Name == "updates_check");
        Assert.True(tool.Annotations!.ReadOnlyHint);
        Assert.True(tool.Annotations.OpenWorldHint);
        Assert.DoesNotContain(runtime.Tools, tool => tool.Name is "updates_open" or "updates_install");
        var request = new CallToolRequestParams
        {
            Name = "updates_check",
            Arguments = new Dictionary<string, JsonElement>
            { ["source"] = JsonSerializer.SerializeToElement("gitee") }
        };
        var success = await runtime.CallAsync(request, default);
        Assert.False(success.IsError);
        Assert.Equal("noRelease", success.StructuredContent!.Value.GetProperty("check").GetProperty("status").GetString());
        source.Fail = true;
        var failed = await runtime.CallAsync(request, default);
        Assert.True(failed.IsError);
        Assert.Equal("UPDATE_NETWORK_FAILED", failed.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", failed.StructuredContent.Value.GetProperty("result").GetProperty("check").GetProperty("status").GetString());
    }

    /// <summary>仅清理当前测试自己创建的唯一临时用户目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    /// <summary>替换外部传输, 保留真实选择与存储服务.</summary>
    private sealed class Releases : IProductReleaseSource
    {
        public string? LastSource { get; private set; }
        public bool Fail { get; set; }
        /// <summary>记录实际来源并注入确定失败, 无网络副作用.</summary>
        public Task<ProductRelease[]> ReadAsync(string source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastSource = source;
            return Fail ? Task.FromException<ProductRelease[]>(new AppException("UPDATE_NETWORK_FAILED", "更新查询失败", 3))
                : Task.FromResult<ProductRelease[]>([]);
        }
    }
}
