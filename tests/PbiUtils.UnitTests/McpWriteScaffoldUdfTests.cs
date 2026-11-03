// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteScaffoldUdfTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 经真实 MCP 与精确确认核验 UDF 来源、本地函数和目录刷新的持久化结果, 网络仅替换 HTTP 字节.

using PbiUtils.Infrastructure.Udf.Sources;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Infrastructure.Udf;
using Xunit;
using static PbiUtils.UnitTests.McpWriteScaffoldFixture;

namespace PbiUtils.UnitTests;

/// <summary>真实存储与正式 Git 解析/发布运行在隔离用户目录, 不读取用户库或连接模型.</summary>
public sealed class McpWriteScaffoldUdfTests
{
    /// <summary>来源新增、精确基线更新和删除都通过 MCP, 独立读取证明内容与同级来源保护.</summary>
    [Fact]
    public async Task SourceSaveUpdatesExactBaselineAndDeletePreservesOtherSources()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var unrelated = await fixture.Sources.Sources.SaveAsync("Keep", "https://github.com/example/keep", null, null, default);
        var originalSources = await File.ReadAllBytesAsync(Path.Combine(fixture.UserRoot, "udf", "sources.json"));
        var source = (await fixture.WriteAsync("udf.sourceSave", new { name = "MCP Source", baseUrl = "https://github.com/example/functions", protocol = "git", gitRef = "main" },
            _ => Assert.Equal(originalSources, File.ReadAllBytes(Path.Combine(fixture.UserRoot, "udf", "sources.json"))), true)).Deserialize<UdfSourceEntry>(JsonSerializerOptions.Web)!;
        Assert.StartsWith("source:", source.Id, StringComparison.Ordinal);
        Assert.Equal("MCP Source", (await new UdfSourceStore(fixture.UserRoot).GetAsync(source.Id, default)).Name);
        var updated = (await fixture.WriteAsync("udf.sourceSave", new
        {
            id = source.Id,
            baseline = source.Baseline,
            name = "MCP Renamed",
            baseUrl = source.BaseUrl,
            protocol = "git",
            gitRef = "release"
        }, commitStarted: true)).Deserialize<UdfSourceEntry>(JsonSerializerOptions.Web)!;
        Assert.Equal(source.Id, updated.Id);
        Assert.NotEqual(source.Baseline, updated.Baseline);
        var reread = await new UdfSourceStore(fixture.UserRoot).GetAsync(updated.Id, default);
        Assert.Equal(updated, reread);
        Assert.Equal("release", reread.GitRef);
        var deleted = await fixture.WriteAsync("udf.sourceDelete", new { id = updated.Id, baseline = updated.Baseline, confirm = true }, commitStarted: true);
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        var remaining = await new UdfSourceStore(fixture.UserRoot).ListAsync(default);
        Assert.DoesNotContain(remaining, item => item.Id == updated.Id);
        Assert.Equal(unrelated, Assert.Single(remaining, item => item.Id == unrelated.Id));
        Assert.Contains(remaining, item => item.Id == "daxlib");
        Assert.Contains(remaining, item => item.Id == "local");
    }

    /// <summary>本地函数新增、更新及删除均落真实 JSON, 更新保留 ID, 删除不影响另一个函数.</summary>
    [Fact]
    public async Task LocalSaveUpdatesDefinitionAndDeleteRemovesOnlyExactEntry()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var retained = await fixture.Local.CreateAsync(new(1, "Local.Keep", "1.0.0", "() => 9", "保留", []), default);
        LocalUdfDefinition definition = new(1, "Local.McpValue", "1.0.0", "() => 1", "MCP 实际写入", []);
        var saved = (await fixture.WriteAsync("udf.localSave", new { definition }, commitStarted: true)).Deserialize<LocalUdfEntry>(JsonSerializerOptions.Web)!;
        var path = Path.Combine(fixture.UserRoot, "udf", "local", saved.Id + ".json");
        Assert.True(File.Exists(path));
        Assert.Equal(definition.Expression, (await new LocalUdfStore(fixture.UserRoot).GetAsync(saved.Id, default)).Definition.Expression);
        var updated = (await fixture.WriteAsync("udf.localSave", new
        {
            id = saved.Id,
            baseline = saved.Baseline,
            definition = definition with { Expression = "() => 2", Description = "MCP 更新" }
        }, commitStarted: true)).Deserialize<LocalUdfEntry>(JsonSerializerOptions.Web)!;
        Assert.Equal(saved.Id, updated.Id);
        Assert.NotEqual(saved.Baseline, updated.Baseline);
        var reread = await new LocalUdfStore(fixture.UserRoot).GetAsync(updated.Id, default);
        Assert.Equal(updated.Baseline, reread.Baseline);
        Assert.Equal("() => 2", reread.Definition.Expression);
        var mcpRead = Json(await fixture.Session.InvokeMcpAsync("udf.localGet", Json(new { id = updated.Id }), default) ?? throw new InvalidOperationException());
        Assert.Equal("() => 2", mcpRead.GetProperty("definition").GetProperty("expression").GetString());
        Assert.True((await fixture.WriteAsync("udf.localDelete", new { id = updated.Id, baseline = updated.Baseline, confirm = true }, commitStarted: true)).GetProperty("deleted").GetBoolean());
        Assert.False(File.Exists(path));
        var survivor = Assert.Single(await new LocalUdfStore(fixture.UserRoot).ListAsync(default));
        Assert.Equal(retained.Id, survivor.Id);
        Assert.Equal(retained.Baseline, survivor.Baseline);
    }

    /// <summary>官方与自定义来源经 MCP 真正下载、解析 Git 历史并原子发布缓存, 独立提供者回读不联网.</summary>
    /// <param name="custom">是否先经 MCP 建立自定义仓库及其配置基线.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogRefreshParsesTransportAndPersistsReopenableSnapshot(bool custom)
    {
        var transport = GitHubUdfFixture.Catalog();
        List<Uri> requests = [];
        await using var fixture = new McpWriteScaffoldFixture(_ => new HttpClient(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            requests.Add(request.RequestUri!);
            return transport.Respond(request);
        })));
        var source = custom
            ? (await fixture.WriteAsync("udf.sourceSave", new { name = "MCP Refresh", baseUrl = "https://github.com/example/library" }, commitStarted: true)).Deserialize<UdfSourceEntry>(JsonSerializerOptions.Web)!
            : UdfSourceDefaults.Official;
        var parameters = custom ? Json(new { source = source.Id, sourceBaseline = source.Baseline }) : Json(new { });
        var status = (await fixture.WriteAsync("udf.catalogRefresh", parameters, _ =>
        {
            Assert.Empty(requests);
            Assert.False(Directory.Exists(fixture.UserRoot) && Directory.GetFiles(fixture.UserRoot, "current.json", SearchOption.AllDirectories).Length > 0);
        }, true)).Deserialize<UdfCatalogStatus>(JsonSerializerOptions.Web)!;
        Assert.True(status.Initialized);
        Assert.False(string.IsNullOrWhiteSpace(status.Baseline));
        Assert.NotEmpty(requests);
        Assert.Contains(requests, uri => uri.Host == "github.com" && uri.AbsolutePath.EndsWith("/git-upload-pack", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(fixture.UserRoot, "current.json", SearchOption.AllDirectories));
        using var independent = new UdfSourceProvider(fixture.UserRoot, _ => throw new InvalidOperationException("Cached read must not create HTTP client"));
        var provider = Assert.IsType<IUdfCatalogProvider>(await independent.GetProviderAsync(source.Id, source.Baseline, default), exactMatch: false);
        Assert.Equal(status, await provider.GetStatusAsync(default));
        Assert.Equal(["1.0.0", "2.0.0"], await provider.GetVersionsAsync("Example.One", default));
        var package = await provider.GetAsync("Example.One", "1.0.0", default);
        Assert.NotEmpty(package.Functions);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), package.CommittedAt);
        var count = requests.Count;
        var browser = Json(await fixture.Session.InvokeMcpAsync("udf.catalog", Json(new { action = "status", source = source.Id, sourceBaseline = source.Baseline }), default)
            ?? throw new InvalidOperationException());
        Assert.Equal(status.Baseline, browser.GetProperty("status").GetProperty("baseline").GetString());
        Assert.Equal(count, requests.Count);
    }

    /// <summary>仅替代远端 HTTP 响应, 取消与正式传输、校验和、历史解析继续生效.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>传递原请求并检查取消, 不合成目录或业务成功对象.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
