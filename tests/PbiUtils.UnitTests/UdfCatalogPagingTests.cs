// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogPagingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 GitHub 完整目录、重复或截断树、缺失归档及取消不会产生部分发布.

using PbiUtils.Infrastructure.Udf.Sources;
using System.Net;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>原 API 分页边界改由完整树与归档一致性验证, 不保留退休 API 的偏移假设.</summary>
public sealed class UdfCatalogPagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-pages-" + Guid.NewGuid().ToString("N"));

    /// <summary>从同一完整提交读取所有包, 自定义来源始终使用其绑定仓库.</summary>
    [Fact]
    public async Task ReadsEveryPackageFromBoundRepository()
    {
        var requests = new List<Uri>();
        string[] ids = ["Example.One", "Example.Two", "Example.Three"];
        var fixture = new GitHubUdfFixture([.. ids
            .Select(id => GitHubUdfFixture.Package(id, "1.0.0", [(id + ".One", "() => 1", "")]))]);
        using var router = Router(request => { requests.Add(request.RequestUri!); return fixture.Respond(request); });
        var source = await router.Sources.SaveAsync("Team", "https://github.com/example/team", null, null, default);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(source.Id, source.Baseline, default), exactMatch: false);
        await provider.RefreshAsync(null, default);
        Assert.Equal(["Example.One", "Example.Three", "Example.Two"], (await provider.SearchAsync("", false, default)).Select(item => item.Id));
        Assert.Equal(6, requests.Count);
        Assert.All(requests, request => Assert.Contains("/example/team", request.AbsolutePath, StringComparison.Ordinal));
    }

    /// <summary>重复树身份或被 GitHub 截断的树均整次失败, 不把已读文件当成完整目录.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsRepeatedOrTruncatedTreeWithoutPartialCatalog(bool repeated)
    {
        var fixture = new GitHubUdfFixture(UdfPackageTests.Package()) { RepeatTreeEntry = repeated, Truncated = !repeated };
        using var router = Router(fixture.Respond);
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default));
        Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
    }

    /// <summary>树已取得后压缩包传输失败, 不能返回已知包名或部分新代.</summary>
    [Fact]
    public async Task ArchiveFailureDoesNotReturnPartialCatalog()
    {
        var fixture = GitHubUdfFixture.Catalog();
        using var router = Router(request => request.RequestUri!.Host == "codeload.github.com" ? throw new HttpRequestException("offline") : fixture.Respond(request));
        Assert.Equal("UDF_GIT_NETWORK", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default))).Code);
        Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
    }

    /// <summary>压缩包遗漏树中的文件时整批拒绝, 即使其他版本完整也不得发布.</summary>
    [Fact]
    public async Task MissingArchiveFileDoesNotPublishCompleteLookingSubset()
    {
        var fixture = new GitHubUdfFixture(UdfPackageTests.Package()) { OmitArchiveFile = true };
        using var router = Router(fixture.Respond);
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default));
        Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
    }

    /// <summary>后续归档取消时整次退出, 不返回已读树或缓存成功.</summary>
    [Fact]
    public async Task CancellationDoesNotReturnPartialCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = GitHubUdfFixture.Catalog();
        var calls = 0;
        using var router = Router(request =>
        {
            calls++;
            if (request.RequestUri!.Host == "codeload.github.com") cancellation.Cancel();
            return fixture.Respond(request);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DefaultProvider.RefreshAsync(null, cancellation.Token));
        Assert.Equal(4, calls);
        Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
    }

    /// <summary>共享正式来源路由与发布实现, 只注入受控内存传输.</summary>
    private UdfSourceProvider Router(Func<HttpRequestMessage, HttpResponseMessage> action) => new(root, _ => new HttpClient(new Handler(action)));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        /// <summary>取消发生在发送前或接收后都由正式调用链处理.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
    }

    /// <summary>只清理本类拥有的临时缓存.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
