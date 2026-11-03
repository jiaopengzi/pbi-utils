// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitSmartUdfTransportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证多主机 HTTP Git 协议、固定提交、完整对象图和无凭据来源登记.

using PbiUtils.Infrastructure.Udf.Repositories;
using System.Net;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>内存处理器仅替代网络, 所有协议和包校验由正式代码执行.</summary>
public sealed class GitSmartUdfTransportTests
{
    /// <summary>不同品牌、多层命名空间和单路径内网仓库均通过同一无凭据协议获取.</summary>
    [Theory]
    [InlineData("https://gitee.com/Team/Library.git")]
    [InlineData("https://gitlab.com/Group/Subgroup/Library.git")]
    [InlineData("http://git.internal:8080/Library.git")]
    [InlineData("https://192.168.1.20:8443/Team/Library")]
    public async Task FetchesCompleteSnapshotFromGenericSmartHttp(string url)
    {
        var fixture = new GitSmartUdfFixture();
        using var client = new HttpClient(new Handler(fixture.Respond));
        var source = new GitUdfCatalogSource("unused", new("source:" + new string('a', 32), "Team", url, "baseline", "custom"), client);
        var packages = await source.DownloadAsync(null, default);
        var package = Assert.Single(packages); Assert.Equal("Example.One", package.Id);
        Assert.Equal("Example.Helper", Assert.Single(package.Functions).Name);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch, package.CommittedAt);
        Assert.All(fixture.Requests, uri => Assert.Equal(new Uri(url).Authority, uri.Authority));
    }

    /// <summary>分支、标签、完整 ref 和精确 SHA 均绑定广告中的固定对象, 注解标签解析到 commit.</summary>
    [Theory]
    [InlineData("main", false)]
    [InlineData("refs/heads/main", false)]
    [InlineData("v1", true)]
    [InlineData("refs/tags/v1", true)]
    public async Task ResolvesRequestedRefAndAnnotatedTag(string reference, bool annotated)
    {
        var fixture = new GitSmartUdfFixture { AnnotatedTag = annotated };
        using var client = new HttpClient(new Handler(fixture.Respond));
        var source = new GitUdfCatalogSource("unused", Entry(reference), client);
        Assert.Single(await source.DownloadAsync(null, default));
    }

    /// <summary>完整 SHA 请求只能接受对应 commit, 服务端返回另一份自洽 PACK 仍失败.</summary>
    [Fact]
    public async Task ExactCommitCannotBeSubstitutedByServer()
    {
        var fixture = new GitSmartUdfFixture { IgnoreWantedObject = true };
        using var client = new HttpClient(new Handler(fixture.Respond));
        Assert.Single(await new GitUdfCatalogSource("unused", Entry(fixture.Commit.ToUpperInvariant()), client).DownloadAsync(null, default));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => new GitUdfCatalogSource("unused", Entry(new string('b', 40)), client).DownloadAsync(null, default))).Code);
    }

    /// <summary>损坏 PACK 和缺失 blob 不能被部分 packages 内容掩盖.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectsIncompleteOrCorruptObjectGraph(bool corrupt, bool missing)
    {
        var fixture = new GitSmartUdfFixture { CorruptPack = corrupt, OmitBlob = missing };
        using var client = new HttpClient(new Handler(fixture.Respond));
        var error = await Assert.ThrowsAsync<AppException>(() => new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, default));
        Assert.Equal("UDF_GIT_INVALID", error.Code);
    }

    /// <summary>Git 常用 ofs/ref delta 必须在预算内正确展开, 不能只兼容无 delta 的伪 pack.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsPackWithDeltaObjects(bool referenceDelta)
    {
        var fixture = new GitSmartUdfFixture { UseDelta = true, UseReferenceDelta = referenceDelta };
        using var client = new HttpClient(new Handler(fixture.Respond));
        Assert.Single(await new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, default));
    }

    /// <summary>合法边界深度可以读取, 超过 64 层的 delta 链整批拒绝, 覆盖 ofs/ref 两种基础身份.</summary>
    [Theory]
    [InlineData(false, 64, true)]
    [InlineData(true, 64, true)]
    [InlineData(false, 65, false)]
    [InlineData(true, 65, false)]
    public async Task BoundsDeltaChainDepth(bool referenceDelta, int depth, bool valid)
    {
        var fixture = new GitSmartUdfFixture { UseDelta = true, UseReferenceDelta = referenceDelta, DeltaDepth = depth };
        using var client = new HttpClient(new Handler(fixture.Respond));
        var source = new GitUdfCatalogSource("unused", Entry(), client);
        if (valid) Assert.Single(await source.DownloadAsync(null, default));
        else Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => source.DownloadAsync(null, default))).Code);
    }

    /// <summary>树深度与重复大小写路径不依赖是否属于 packages, 不能藏在无关源码中绕过.</summary>
    [Theory]
    [InlineData("a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/file")]
    [InlineData("PACKAGES/extra.txt")]
    public async Task RejectsUnsafeUnrelatedTreePath(string path)
    {
        var fixture = new GitSmartUdfFixture(extraPath: path);
        using var client = new HttpClient(new Handler(fixture.Respond));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, default))).Code);
    }

    /// <summary>网络读取期间取消保留调用方取消语义, 不发布部分包或错误转换为超时.</summary>
    [Fact]
    public async Task PreservesCallerCancellationDuringFetch()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, cancellation.Token));
    }

    /// <summary>链接、子模块和可执行包内容保持拒绝, 不从仓库执行或读取外部内容.</summary>
    [Theory]
    [InlineData("120000")]
    [InlineData("160000")]
    [InlineData("100755")]
    public async Task RejectsUnsafeGitTreeModes(string mode)
    {
        var fixture = new GitSmartUdfFixture(mode);
        using var client = new HttpClient(new Handler(fixture.Respond));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, default))).Code);
    }

    /// <summary>任何 HTTP 重定向、登录或 HTML 正文不参与 ref 或对象解析.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Found, "UDF_GIT_REDIRECT")]
    [InlineData(HttpStatusCode.Unauthorized, "UDF_GIT_AUTH_REQUIRED")]
    [InlineData(HttpStatusCode.Forbidden, "UDF_GIT_FORBIDDEN")]
    public async Task RejectsRedirectAndAuthentication(HttpStatusCode status, string code)
    {
        using var client = new HttpClient(new Handler(_ => new(status)));
        Assert.Equal(code, (await Assert.ThrowsAsync<AppException>(() => new GitUdfCatalogSource("unused", Entry(), client).DownloadAsync(null, default))).Code);
    }

    /// <summary>仅为测试生成稳定来源输入; 不向真实主机联网.</summary>
    private static UdfSourceEntry Entry(string? reference = null) => new("source:" + new string('a', 32), "Team", "http://git.internal:8080/Library.git", "baseline", "custom", GitRef: reference);

    /// <summary>转发取消令牌给 HttpClient, 返回调用方提供的协议响应.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }

    /// <summary>异步处理器仅控制取消时序, 不启动服务器或请求真实主机.</summary>
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
