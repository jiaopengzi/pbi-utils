// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitHubUdfTransportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证固定 GitHub commit 获取、完整归档校验及安全 HTTP 失败边界.

using PbiUtils.Infrastructure.Udf.Repositories.GitHub;
using PbiUtils.Infrastructure.Udf.Repositories;
using PbiUtils.Infrastructure.Udf.Packages;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只替代 HTTPS 传输, 生产树、ZIP 和包解析器均真实执行.</summary>
public sealed class GitHubUdfTransportTests
{
    /// <summary>默认分支、命名分支、标签和固定提交只解析一次, 后续树与 ZIP 均绑定同一 commit.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("feature/catalog")]
    [InlineData("refs/tags/v1.0.0")]
    [InlineData("commit")]
    public async Task DownloadsCompleteFixedCommitWithoutGit(string? reference)
    {
        var fixture = GitHubUdfFixture.Catalog();
        if (reference == "commit") reference = fixture.Commit;
        var requests = new List<Uri>();
        using var client = new HttpClient(new Handler(request => { requests.Add(request.RequestUri!); return fixture.Respond(request); }));
        var result = await Adapter(client, reference).DownloadSnapshotAsync(null, default);
        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(fixture.Commit, result.ResolvedCommit);
        Assert.Equal(reference, result.RequestedRef);
        Assert.Equal("https://github.com/example/repository", result.RepositoryUrl);
        Assert.Equal(reference is null ? 6 : 5, requests.Count);
        var commitRequest = Assert.Single(requests, uri => uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal));
        Assert.Equal("/repos/example/repository/commits/" + Uri.EscapeDataString(reference ?? "main"), commitRequest.AbsolutePath);
        Assert.Equal("", commitRequest.Query);
        var treeRequest = Assert.Single(requests, uri => uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal));
        Assert.Equal("/repos/example/repository/git/trees/" + fixture.Tree, treeRequest.AbsolutePath);
        Assert.Equal("?recursive=1", treeRequest.Query);
        var archiveRequest = Assert.Single(requests, uri => uri.Host == "codeload.github.com");
        Assert.EndsWith("/zip/" + fixture.Commit, archiveRequest.AbsolutePath);
        Assert.Equal("github.com", requests[^1].Host);
        Assert.Equal("/example/repository.git/git-upload-pack", requests[^1].AbsolutePath);
        Assert.All(result.Packages, package => Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), package.CommittedAt));
        Assert.All(result.Packages, package => Assert.Equal("fixture repository MIT", package.LicenseFiles["repository/LICENSE"]));
    }

    /// <summary>截断树、重复路径或归档遗漏最后一个文件都拒绝整批, 不返回表面完整目录.</summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task RejectsIncompleteSnapshot(string fault)
    {
        var fixture = new GitHubUdfFixture(UdfPackageTests.Package())
        { Truncated = fault == "truncated", RepeatTreeEntry = fault == "duplicate", OmitArchiveFile = fault == "missing" };
        using var client = new HttpClient(new Handler(fixture.Respond));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => Adapter(client).DownloadAsync(null, default))).Code);
    }

    /// <summary>不受信任树在任何目录出现链接、子模块、非法大小、路径冲突或错误根身份都拒绝.</summary>
    [Theory]
    [InlineData("symlink")]
    [InlineData("submodule")]
    [InlineData("negative")]
    [InlineData("size")]
    [InlineData("traversal")]
    [InlineData("case")]
    [InlineData("hash")]
    [InlineData("root")]
    public async Task RejectsUnsafeTree(string fault)
    {
        var fixture = GitHubUdfFixture.Catalog();
        using var client = new HttpClient(new Handler(request =>
        {
            var response = fixture.Respond(request);
            if (!request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)) return response;
            var root = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var item = root["tree"]![0]!;
            switch (fault)
            {
                case "symlink": item["mode"] = "120000"; break;
                case "submodule": item["mode"] = "160000"; item["type"] = "commit"; break;
                case "negative": item["size"] = -1; break;
                case "size": item["size"] = DaxLibPackageReader.MaxFileBytes + 1; break;
                case "traversal": item["path"] = "../LICENSE"; break;
                case "hash": item["sha"] = new string('0', 40); break;
                case "root": root["sha"] = new string('0', 40); break;
                case "case": var duplicate = item.DeepClone(); duplicate["path"] = "license"; root["tree"]!.AsArray().Add(duplicate); break;
            }
            response.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => Adapter(client).DownloadAsync(null, default))).Code);
    }

    /// <summary>同一 ZIP 的根、文件集合、内容和模式都须与固定树一致.</summary>
    [Theory]
    [InlineData("root")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("content")]
    [InlineData("symlink")]
    [InlineData("executable")]
    [InlineData("malformed")]
    public async Task RejectsUnsafeArchive(string fault)
    {
        var fixture = GitHubUdfFixture.Catalog();
        using var client = new HttpClient(new Handler(request =>
        {
            var response = fixture.Respond(request);
            if (request.RequestUri!.Host != "codeload.github.com") return response;
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            using var original = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var source in original.Entries)
                {
                    var name = source.FullName;
                    if (fault == "root") name = "other-commit/" + name.Split('/').Last();
                    if (fault == "traversal") name = name[..name.IndexOf('/')] + "/../LICENSE";
                    var entry = zip.CreateEntry(name);
                    if (fault == "symlink") entry.ExternalAttributes = 0xa1ff << 16;
                    if (fault == "executable") entry.ExternalAttributes = 0x81ed << 16;
                    using var target = entry.Open();
                    if (fault == "content") target.Write("tampered"u8); else { using var stream = source.Open(); stream.CopyTo(target); }
                }
                if (fault is "duplicate" or "extra") zip.CreateEntry(fault == "duplicate" ? "repository-" + fixture.Commit + "/license" : "repository-" + fixture.Commit + "/extra.txt");
            }
            response.Content = new ByteArrayContent(fault == "malformed" ? "not a zip"u8.ToArray() : output.ToArray());
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return response;
        }));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => Adapter(client).DownloadAsync(null, default))).Code);
    }

    /// <summary>认证、拒绝、额度和重定向分别给出安全错误码, 不接触响应中的敏感正文.</summary>
    [Theory]
    [InlineData(401, "UDF_GIT_AUTH_REQUIRED")]
    [InlineData(403, "UDF_GIT_FORBIDDEN")]
    [InlineData(429, "UDF_GIT_RATE_LIMIT")]
    [InlineData(302, "UDF_GIT_REDIRECT")]
    [InlineData(404, "UDF_GIT_HTTP_FAILED")]
    [InlineData(206, "UDF_GIT_HTTP_FAILED")]
    public async Task ReportsSafeHttpFailure(int status, string expected)
    {
        using var client = new HttpClient(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("secret remote body") }));
        var error = await Assert.ThrowsAsync<AppException>(() => Adapter(client).DownloadAsync(null, default));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    /// <summary>成功状态的 HTML 或错误 Content-Length 不能进入 JSON/ZIP 解析.</summary>
    [Fact]
    public async Task RejectsHtmlAndOversizedMetadata()
    {
        using var html = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") }));
        Assert.Equal("UDF_GIT_INVALID", (await Assert.ThrowsAsync<AppException>(() => Adapter(html).DownloadAsync(null, default))).Code);
        using var large = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Content.Headers.ContentLength = 1024 * 1024;
            return response;
        }));
        Assert.Equal("UDF_GIT_LIMIT", (await Assert.ThrowsAsync<AppException>(() => Adapter(large).DownloadAsync(null, default))).Code);
    }

    /// <summary>声明大小虽在总预算内, 极高压缩比的成员也不能进入包解析.</summary>
    [Fact]
    public void RejectsExcessiveCompressionRatio()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var stream = zip.CreateEntry("repository-commit/README.md", CompressionLevel.SmallestSize).Open();
            stream.Write(bytes);
        }
        var hash = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0"), .. bytes]));
        Assert.Throws<AppException>(() => GitHubUdfArchive.Read(output.ToArray(), [new("README.md", "100644", hash, bytes.Length)], "repository-commit/", default));
    }

    /// <summary>GitHub 规范仓库名可有大小写, 但同一归档不得混入两个仅大小写不同的根.</summary>
    [Fact]
    public void AcceptsCanonicalRepositoryCaseAndRejectsMixedRoots()
    {
        var first = "first"u8.ToArray();
        var second = "second"u8.ToArray();
        var tree = new[] { Entry("LICENSE", first), Entry("NOTICE", second) };
        var valid = Archive(("Repository-commit/LICENSE", first), ("Repository-commit/NOTICE", second));
        Assert.Equal(2, GitHubUdfArchive.Read(valid, tree, "repository-commit/", default).Count);
        var mixed = Archive(("Repository-commit/LICENSE", first), ("repository-commit/NOTICE", second));
        Assert.Throws<AppException>(() => GitHubUdfArchive.Read(mixed, tree, "repository-commit/", default));
    }

    /// <summary>隐式父目录大小写冲突及文件占用目录前缀均不能形成可接受快照.</summary>
    [Theory]
    [InlineData("README", "README/file")]
    [InlineData("docs/readme.md", "DOCS/next.md")]
    public void RejectsConflictingPathHierarchy(string first, string second)
    {
        var entries = new[] { first, second }.Select(path => new { path, mode = "100644", type = "blob", sha = new string('a', 40), size = 0 });
        var root = JsonSerializer.SerializeToElement(new { sha = new string('a', 40), truncated = false, tree = entries });
        Assert.Throws<AppException>(() => GitHubUdfTree.Read(root, new string('a', 40), default));
    }

    /// <summary>传输期间取消按调用者取消传播, 注入客户端由测试拥有而不被适配器释放.</summary>
    [Fact]
    public async Task PropagatesCallerCancellationAndKeepsClientOwnedByCaller()
    {
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(_ => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(client).DownloadAsync(null, cancel.Token));
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));
    }

    /// <summary>最后一包报告期间的取消仍须传播, 不能以已完成 CPU 解析为由返回成功.</summary>
    [Fact]
    public async Task HonorsCancellationAfterFinalPackageParse()
    {
        var fixture = GitHubUdfFixture.Catalog();
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(fixture.Respond));
        var progress = new InlineProgress(value => { if (value.Stage == "git.parse" && value.Completed == value.Total) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(client).DownloadAsync(progress, cancel.Token));
    }

    /// <summary>网络原文不得流入错误, 请求超时与调用者取消保持不同分类.</summary>
    [Fact]
    public async Task ReportsSafeNetworkAndTimeoutFailures()
    {
        using var network = new HttpClient(new Handler(_ => throw new HttpRequestException("secret remote host")));
        var error = await Assert.ThrowsAsync<AppException>(() => Adapter(network).DownloadAsync(null, default));
        Assert.Equal("UDF_GIT_NETWORK", error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        using var timeout = new HttpClient(new AsyncHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); })) { Timeout = TimeSpan.FromMilliseconds(25) };
        Assert.Equal("UDF_GIT_TIMEOUT", (await Assert.ThrowsAsync<AppException>(() => Adapter(timeout).DownloadAsync(null, default))).Code);
    }

    /// <summary>测试来源仅使用固定公开 GitHub 形状, 远端访问由注入处理器替代.</summary>
    private static GitUdfCatalogSource Adapter(HttpClient client, string? reference = null) => new("unused", new UdfSourceEntry("test", "test", "https://github.com/example/repository", "baseline", "custom", "git", reference), client);

    /// <summary>计算普通测试文件的真实 Git blob 指纹.</summary>
    private static GitUdfTreeEntry Entry(string path, byte[] content) => new(path, "100644", Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes("blob " + content.Length + "\0"), .. content])), content.Length);

    /// <summary>创建只含指定普通文件的 ZIP, 用于独立验证根目录和文件集合.</summary>
    private static byte[] Archive(params (string Path, byte[] Content)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var stream = zip.CreateEntry(file.Path).Open(); stream.Write(file.Content); }
        return output.ToArray();
    }

    /// <summary>同步 fixture 不联网; 请求取消由 HttpClient 和 fixture 共同保留.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }

    /// <summary>异步处理器只用于真实取消令牌和 HttpClient 超时行为测试.</summary>
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    /// <summary>进度同步触发, 可准确固定最后一个解析边界的取消时序.</summary>
    private sealed class InlineProgress(Action<UdfCatalogProgress> report) : IProgress<UdfCatalogProgress>
    {
        public void Report(UdfCatalogProgress value) => report(value);
    }
}
