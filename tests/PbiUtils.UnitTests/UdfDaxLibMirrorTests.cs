// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfDaxLibMirrorTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证内置 DAXLib 的镜像回退、真实追溯、缓存及取消边界.

using System.Net;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf.Catalog;
using PbiUtils.Infrastructure.Udf.Sources;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>通过受控 HTTP 传输更新独占临时目录, 不请求真实来源或修改用户数据.</summary>
public sealed class UdfDaxLibMirrorTests : IDisposable
{
    private const string MirrorUrl = "https://gitee.com/jiaopengzi/daxlib-mirror";
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-mirror-" + Guid.NewGuid().ToString("N"));

    /// <summary>GitHub 不可用时仅提交一次完整镜像目录, 重开后按内置来源离线读取并保留实际仓库追溯.</summary>
    [Fact]
    public async Task GitHubUnavailablePublishesMirrorInBuiltinCatalog()
    {
        var mirror = new GitSmartUdfFixture();
        var requests = new List<Uri>();
        using var router = Router(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            return uri.Host == "gitee.com" ? mirror.Respond(request) : new(HttpStatusCode.ServiceUnavailable);
        });

        var commits = 0;
        var status = await router.DefaultProvider.RefreshAsync(null, default, () => commits++);

        Assert.True(status.Initialized);
        Assert.True(status.UsedMirror);
        Assert.Equal(1, commits);
        Assert.Equal("api.github.com", requests[0].Host);
        Assert.All(requests.Skip(1), uri => Assert.StartsWith(MirrorUrl + "/", uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Equal(4, mirror.Requests.Count);
        using var snapshot = new UdfCatalogSnapshotStore(root, UdfSourceDefaults.Official).Read()!;
        Assert.Equal(new UdfRepositoryRevision(MirrorUrl, "main", mirror.Commit), snapshot.Repository);

        using var reopened = Router(_ => throw new InvalidOperationException("Unexpected network"));
        var provider = Assert.IsType<IUdfCatalogProvider>(await reopened.GetProviderAsync("daxlib", null, default), exactMatch: false);
        Assert.Equal(status, await provider.GetStatusAsync(default));
        var package = await provider.GetAsync("Example.One", "1.0.0", default);
        Assert.True(package.FromCache);
        Assert.Equal("Example.Helper", Assert.Single(package.Functions).Name);
    }

    /// <summary>开启强制 GitHub 后, 新实例仍按该偏好更新; GitHub 恢复后缓存追溯回到源仓库.</summary>
    [Fact]
    public async Task GitHubOnlyPersistsAndSkipsMirrorWhenGitHubIsDown()
    {
        var mirror = new GitSmartUdfFixture();
        using (var router = Router(request => request.RequestUri!.Host == "gitee.com"
            ? mirror.Respond(request) : new(HttpStatusCode.ServiceUnavailable)))
        {
            Assert.False(await router.Sources.GetDaxLibGitHubOnlyAsync(default));
            Assert.True((await router.DefaultProvider.RefreshAsync(null, default)).UsedMirror);
            Assert.True(await router.Sources.SaveDaxLibGitHubOnlyAsync(true, default));
        }

        var fixture = GitHubUdfFixture.Catalog();
        var unavailable = true;
        using var reopened = Router(request =>
        {
            Assert.NotEqual("gitee.com", request.RequestUri!.Host);
            return unavailable ? new(HttpStatusCode.ServiceUnavailable) : fixture.Respond(request);
        });
        Assert.True(await reopened.Sources.GetDaxLibGitHubOnlyAsync(default));
        var before = await reopened.DefaultProvider.GetStatusAsync(default);
        Assert.True(before.UsedMirror);

        var error = await Assert.ThrowsAsync<AppException>(() => reopened.DefaultProvider.RefreshAsync(null, default));
        Assert.Equal("UDF_GIT_HTTP_FAILED", error.Code);
        Assert.Equal(before, await reopened.DefaultProvider.GetStatusAsync(default));

        unavailable = false;
        var refreshed = await reopened.DefaultProvider.RefreshAsync(null, default);
        Assert.False(refreshed.UsedMirror);
        using var snapshot = new UdfCatalogSnapshotStore(root, UdfSourceDefaults.Official).Read()!;
        Assert.Equal(new UdfRepositoryRevision(UdfSourceDefaults.Official.BaseUrl!, null, fixture.Commit), snapshot.Repository);
        Assert.False(await reopened.Sources.SaveDaxLibGitHubOnlyAsync(false, default));
        Assert.False(await reopened.Sources.GetDaxLibGitHubOnlyAsync(default));
    }

    /// <summary>两个仓库均不可用时报告失败, 保持旧指针和整个缓存, 不进入发布回调.</summary>
    [Fact]
    public async Task BothRepositoriesUnavailablePreservesPublishedCatalog()
    {
        var fixture = GitHubUdfFixture.Catalog();
        var failedHosts = new List<string>();
        var unavailable = false;
        using var router = Router(request =>
        {
            if (!unavailable) return fixture.Respond(request);
            failedHosts.Add(request.RequestUri!.Host);
            return new(HttpStatusCode.ServiceUnavailable);
        });
        var before = await router.DefaultProvider.RefreshAsync(null, default);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var original = File.ReadAllBytes(pointer);
        unavailable = true;
        var commits = 0;

        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default, () => commits++));

        Assert.Collection(failedHosts, host => Assert.Equal("api.github.com", host), host => Assert.Equal("gitee.com", host));
        Assert.Equal(0, commits);
        Assert.Equal(original, File.ReadAllBytes(pointer));
        Assert.Equal(before, await router.DefaultProvider.GetStatusAsync(default));
    }

    /// <summary>调用方在 GitHub 拉取期间取消时, 请求取消且不会进入镜像或发布目录.</summary>
    [Fact]
    public async Task CancellationDuringGitHubDownloadNeverStartsMirror()
    {
        using var cancellation = new CancellationTokenSource();
        using var router = Router(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DefaultProvider.RefreshAsync(null, cancellation.Token));
        Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
    }

    /// <summary>同为 GitHub 的第三方登记不采用 DAXLib 镜像, 自定义失败继续返回其原始错误.</summary>
    [Fact]
    public async Task CustomGitHubSourceNeverUsesDaxLibMirror()
    {
        using var router = Router(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("/repos/example/functions", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.ServiceUnavailable);
        });
        var source = await router.Sources.SaveAsync("Team", "https://github.com/example/functions", null, null, default);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(source.Id, source.Baseline, default), exactMatch: false);
        var error = await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default));
        Assert.Equal("UDF_GIT_HTTP_FAILED", error.Code);
    }

    /// <summary>仅替代网络响应, 保留正式刷新、Git 解析及存储路径.</summary>
    private UdfSourceProvider Router(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(root, _ => new HttpClient(new Handler(respond)));

    /// <summary>清理当前用例自己创建的目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>请求进入夹具前检查调用方取消, 无真实网络连接.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
