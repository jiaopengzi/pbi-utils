// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitUdfHistoryTransportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证标准 Git 历史的能力协商、内容身份、来源隔离以及失败/取消保留缓存边界.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Repositories;
using PbiUtils.Infrastructure.Udf.Packages;
using System.Net;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅替代 HTTP 传输; 全部历史对象、日期与目录缓存经过正式解析器.</summary>
public sealed class GitUdfHistoryTransportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-git-history-" + Guid.NewGuid().ToString("N"));

    /// <summary>GitHub、第三方与内网仓库均用精确同源 smart 端点, filter 仅在广告允许时发送.</summary>
    [Theory]
    [InlineData("https://github.com/example/repository", true)]
    [InlineData("https://gitlab.com/team/subgroup/library.git", true)]
    [InlineData("http://git.internal:8080/library.git", false)]
    public async Task HistoryUsesStandardGitForEverySourceAndPreservesContentHash(string url, bool filter)
    {
        const string path = "packages/e/example.one/1.0.0";
        var fixture = new GitUdfHistoryFixture();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path + "/manifest.daxlib"] = "manifest"u8.ToArray() };
        var tree = fixture.Tree(files); var commit = fixture.Commit(tree, 1700000000);
        var requests = new List<Uri>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Response("advertisement", [.. GitSmartUdfFixture.Packet("# service=git-upload-pack\n"), .. "0000"u8,
                    .. GitSmartUdfFixture.Packet(commit + " HEAD\0shallow ofs-delta" + (filter ? " filter" : "") + "\n"), .. "0000"u8]));
            var body = request.Content!.ReadAsStringAsync(_).GetAwaiter().GetResult();
            Assert.Contains("want " + commit, body, StringComparison.Ordinal);
            Assert.Contains("deepen 4096\n", body, StringComparison.Ordinal);
            Assert.Equal(filter, body.Contains("filter blob:none\n", StringComparison.Ordinal));
            Assert.DoesNotContain("thin-pack", body, StringComparison.Ordinal);
            return Task.FromResult(Response("result", [.. GitSmartUdfFixture.Packet("NAK\n"), .. fixture.Pack()]));
        }));
        var original = DaxLibPackageReader.Read(GitHubUdfFixture.Package("Example.One", "1.0.0", [("Example.One", "() => 1", "")]), "Example.One", "1.0.0");
        var snapshot = new GitUdfDownload([new(path + "/manifest.daxlib", "100644", [])], url, null, commit, tree);
        var actual = Assert.Single(await GitUdfHistory.EnrichAsync(snapshot, [original], client, default));
        Assert.Equal(original.Hash, actual.Hash); Assert.Same(original.Functions, actual.Functions);
        Assert.Same(original.Dependencies, actual.Dependencies); Assert.Same(original.LicenseFiles, actual.LicenseFiles);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), actual.CommittedAt);
        Assert.Equal(2, requests.Count); Assert.All(requests, uri => Assert.Equal(new Uri(url).Authority, uri.Authority));
        var clone = url + (new Uri(url).Host == "github.com" ? ".git" : "");
        Assert.Equal(clone + "/info/refs?service=git-upload-pack", requests[0].AbsoluteUri);
        Assert.Equal(clone + "/git-upload-pack", requests[1].AbsoluteUri);
    }

    /// <summary>官方和用户来源均使用仓库历史, 后续搜索/包/切版本只读缓存且不请求日期服务.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAndCacheOnlyReadsShareRepositoryDates(bool custom)
    {
        var fixture = GitHubUdfFixture.Catalog(); var requests = new List<Uri>();
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler((request, _) =>
        { requests.Add(request.RequestUri!); return Task.FromResult(fixture.Respond(request)); })));
        var source = custom ? await new UdfSourceStore(root).SaveAsync("Team", "https://github.com/example/library", null, null, default) : UdfSourceDefaults.Official;
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(source.Id, source.Baseline, default), exactMatch: false);
        var status = await provider.RefreshAsync(null, default);
        var count = requests.Count;
        Assert.Contains(requests, uri => uri.Host == "github.com" && uri.AbsolutePath.EndsWith("/git-upload-pack", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, uri => uri.Host.Contains("daxlib", StringComparison.Ordinal));
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var package = await provider.GetAsync("Example.One", version, default);
            var page = await provider.QueryPackagesAsync(new("", false, PackageVersions: new Dictionary<string, string> { ["Example.One"] = version }), status.Baseline, default);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), package.CommittedAt);
            Assert.Equal(package.CommittedAt, Assert.Single(page.Items).CommittedAt);
        }
        Assert.Single(await provider.SearchAsync("helper", false, default));
        Assert.Equal(count, requests.Count);
    }

    /// <summary>历史请求已在途时取消、网络/状态/损坏失败均保留完整旧指针与精确包, 不发布无日期替代目录.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("network")]
    [InlineData("http")]
    [InlineData("corrupt")]
    public async Task HistoryFailureDoesNotReplacePublishedSnapshot(string fault)
    {
        var fixture = GitHubUdfFixture.Catalog(); var fail = false;
        using var cancellation = new CancellationTokenSource();
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(async (request, token) =>
        {
            if (!fail || request.RequestUri!.Host != "github.com" || request.Method != HttpMethod.Post) return fixture.Respond(request);
            if (fault == "cancel") { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); }
            if (fault == "network") throw new HttpRequestException("network");
            if (fault == "http") return new(HttpStatusCode.ServiceUnavailable);
            return Response("result", [.. GitSmartUdfFixture.Packet("NAK\n"), .. "PACKbad"u8]);
        })));
        var provider = router.DefaultProvider;
        var before = await provider.RefreshAsync(null, default);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories)); var pointerBytes = File.ReadAllBytes(pointer);
        var original = await provider.GetAsync("Example.One", "1.0.0", default);
        fail = true;
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(null, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default));
            Assert.Equal(fault == "network" ? "UDF_GIT_NETWORK" : fault == "http" ? "UDF_GIT_HTTP_FAILED" : "UDF_GIT_INVALID", error.Code);
        }
        Assert.Equal(pointerBytes, File.ReadAllBytes(pointer)); Assert.Equal(before, await provider.GetStatusAsync(default));
        var retained = await provider.GetAsync("Example.One", "1.0.0", default);
        Assert.Equal(original.Hash, retained.Hash); Assert.Equal(original.CommittedAt, retained.CommittedAt);
    }

    /// <summary>协议媒体类型和 pkt-line 均模拟实际 smart HTTP 响应.</summary>
    private static HttpResponseMessage Response(string kind, byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/x-git-upload-pack-" + kind) } } };

    /// <summary>临时根由本测试创建, 不涉及实际用户缓存或来源配置.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    /// <summary>转发真实取消令牌, 可控网络仅替换传输层.</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        /// <summary>把请求交给当前用例, 不屏蔽取消或故障.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
