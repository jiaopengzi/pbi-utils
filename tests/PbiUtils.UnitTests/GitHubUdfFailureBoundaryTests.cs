// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitHubUdfFailureBoundaryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证仓库地址 ASCII 边界及完整 HTTPS 更新中的安全许可冲突失败.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Repositories;
using System.IO.Compression;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实解析与缓存发布作用于独占临时根, 不访问用户目录或外网.</summary>
public sealed class GitHubUdfFailureBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-failure-boundaries-" + Guid.NewGuid().ToString("N"));

    /// <summary>Unicode 不得借助正则忽略大小写映射成为声称仅 ASCII 的仓库标识.</summary>
    [Theory]
    [InlineData("https://github.com/example/\u212a")]
    [InlineData("https://github.com/\u212a/repository")]
    [InlineData("https://github.com/example/\u0130")]
    [InlineData("https://github.com/example/\u017f")]
    [InlineData("https://g\u0131thub.com/example/repository")]
    [InlineData("https://g\u0130thub.com/example/repository")]
    [InlineData("http\u017f://github.com/example/repository")]
    public void RejectsUnicodeRepositoryIdentity(string url)
    {
        var error = Assert.Throws<AppException>(() => GitHubUdfRepositoryUri.Normalize(url));
        Assert.Equal("UDF_SOURCE_REPOSITORY_INVALID", error.Code);
        Assert.DoesNotContain(url, error.Message, StringComparison.Ordinal);
    }

    /// <summary>明确 ASCII 边界仍保留原有 HTTPS 大小写、固定端口和可选 .git 规范化行为.</summary>
    [Theory]
    [InlineData("https://github.com/example/repository")]
    [InlineData("HTTPS://GITHUB.COM:443/Example/Repository.GIT/")]
    public void PreservesAsciiRepositoryNormalization(string url) =>
        Assert.Equal("https://github.com/example/repository", GitHubUdfRepositoryUri.Normalize(url).AbsoluteUri);

    /// <summary>已通过树和 ZIP blob 核验的许可冲突仍安全失败, 原指针、时间和精确包全部保留.</summary>
    [Fact]
    public async Task LicenseCollisionFailsSafelyWithoutReplacingPublishedCatalog()
    {
        var fixture = GitHubUdfFixture.Catalog();
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request => fixture.Respond(request))));
        var provider = router.DefaultProvider;
        var before = await provider.RefreshAsync(null, default);
        var package = await provider.GetAsync("Example.One", "1.0.0", default);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var pointerBytes = await File.ReadAllBytesAsync(pointer);
        fixture = new GitHubUdfFixture(CollidingPackage());

        var error = await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default));

        Assert.Equal("UDF_GIT_INVALID", error.Code);
        Assert.DoesNotContain("repository/LICENSE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pointerBytes, await File.ReadAllBytesAsync(pointer));
        Assert.Equal(before, await provider.GetStatusAsync(default));
        Assert.Equal(package.Hash, (await provider.GetAsync("Example.One", "1.0.0", default)).Hash);
    }

    /// <summary>构造合法普通包并添加被支持的嵌套许可, 只在祖先许可身份合并时形成冲突.</summary>
    private static byte[] CollidingPackage()
    {
        using var bytes = new MemoryStream();
        bytes.Write(UdfPackageTests.Package());
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Update, true))
        {
            using var content = archive.CreateEntry("repository/LICENSE").Open();
            content.Write("package BSD"u8);
        }
        return bytes.ToArray();
    }

    /// <summary>仅替代外部 HTTPS, 保留全部正式 commit、树、归档、包与发布处理.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>路由和请求均已排空后清理本测试唯一临时根, 不处理共享用户缓存.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
