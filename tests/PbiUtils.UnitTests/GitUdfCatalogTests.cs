// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitUdfCatalogTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证公开 Git 来源、完整 packages 布局及安全读取边界.

using PbiUtils.Infrastructure.Udf.Repositories;
using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>Git 来源只解析受约束的快照, 不执行仓库内容.</summary>
public sealed class GitUdfCatalogTests
{
    /// <summary>完整历史版本测试共用固定期望值, 不在重复执行时分配常量数组.</summary>
    private static readonly string[] ExpectedVersions = ["1.0.0", "2.0.0"];

    /// <summary>正式适配器必须存在, 后端刷新不以占位接口替代实际实现.</summary>
    [Fact]
    public void GitCatalogHasProductionAdapter() => Assert.NotNull(typeof(DaxLibPackageReader).Assembly.GetType("PbiUtils.Infrastructure.Udf.Repositories.GitUdfCatalogSource"));

    /// <summary>完整历史版本、说明和适用祖先许可同时保留, 包身份由 manifest/目录/注解共同验证.</summary>
    [Fact]
    public void ReadsAllVersionsAndApplicableLicenses()
    {
        var snapshot = Files("1.0.0").Concat(Files("2.0.0")).Append(File("LICENSE", "root MIT"))
            .Append(File("packages/e/example.test/LICENSE.md", "package BSD"))
            .Append(File("packages/e/example.test/1.0.0/README.md", "documentation"))
            .Append(File("packages/e/example.test/1.0.0/LICENSE", "version license")).ToArray();
        var packages = GitUdfCatalogSource.ReadSnapshot(snapshot, null, default);
        Assert.Equal(ExpectedVersions, packages.Select(package => package.Version));
        Assert.Equal("documentation", packages[0].Readme);
        Assert.Equal("version license", packages[0].LicenseFiles["LICENSE"]);
        Assert.All(packages, package => Assert.Equal("root MIT", package.LicenseFiles["repository/LICENSE"]));
        Assert.All(packages, package => Assert.Equal("package BSD", package.LicenseFiles["repository/packages/e/example.test/LICENSE.md"]));
        Assert.All(packages, package => Assert.Equal("Example.Test.One", Assert.Single(package.Functions).Name));
        Assert.NotEqual(packages[0].Hash, packages[1].Hash);
        Assert.Equal(packages[0].Hash, GitUdfCatalogSource.ReadSnapshot(snapshot.Reverse().ToArray(), null, default)[0].Hash);
    }

    /// <summary>包内许可不能占用祖先许可的保留身份, 任意祖先层级和大小写冲突均安全拒绝.</summary>
    [Theory]
    [InlineData("LICENSE", "repository/LICENSE")]
    [InlineData("LICENSE", "repository/license")]
    [InlineData("packages/LICENSE", "repository/packages/LICENSE")]
    [InlineData("packages/e/LICENSE.txt", "repository/packages/e/LICENSE.txt")]
    [InlineData("packages/e/example.test/LICENSE.md", "repository/packages/e/example.test/LICENSE.md")]
    public void RejectsPackageLicenseCollisionWithAncestor(string ancestor, string packageLicense)
    {
        var snapshot = Files("1.0.0").Append(File(ancestor, "ancestor MIT"))
            .Append(File("packages/e/example.test/1.0.0/" + packageLicense, "package BSD")).ToArray();
        var error = Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(snapshot, null, default));
        Assert.Equal("UDF_GIT_INVALID", error.Code);
        Assert.DoesNotContain(packageLicense, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>少量共享大许可不能被每个历史版本重复展开而绕过完整解析预算, 超限整批拒绝.</summary>
    [Fact]
    public void RejectsAncestorLicenseAmplificationAcrossVersions()
    {
        var license = new byte[DaxLibPackageReader.MaxFileBytes];
        license.AsSpan().Fill((byte)'L');
        var snapshot = Enumerable.Range(1, 5).SelectMany(version => Files($"{version}.0.0"))
            .Append(new GitUdfFile("LICENSE", "100644", license)).ToArray();

        var error = Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(snapshot, null, default));

        Assert.Equal("UDF_GIT_LIMIT", error.Code);
    }

    /// <summary>上游历史 manifest 仍引用迁移前的官方 schema, 仅兼容精确旧仓库和固定 schema 版本.</summary>
    [Theory]
    [InlineData("https://raw.githubusercontent.com/sql-bi/daxlib/refs/heads/main/schemas/manifest/1.0.0/manifest.1.0.0.schema.json")]
    [InlineData("https://raw.githubusercontent.com/sql-bi/daxlib/main/schemas/manifest/1.0.0/manifest.schema.json")]
    public void ReadsOfficialHistoricalManifestSchema(string schema)
    {
        var files = Files("1.0.0").Select(file => file.Path.EndsWith("manifest.daxlib", StringComparison.Ordinal)
            ? file with { Content = Encoding.UTF8.GetBytes("{\"$schema\":\"" + schema + "\"," + Encoding.UTF8.GetString(file.Content)[1..]) }
            : file).ToArray();
        Assert.Single(GitUdfCatalogSource.ReadSnapshot(files, null, default));
    }

    /// <summary>缺 manifest 的版本不能被完整刷新静默忽略.</summary>
    [Fact]
    public void RejectsIncompleteVersionInsteadOfTruncatingCatalog()
    {
        var snapshot = Files("1.0.0").Concat(Files("2.0.0").Where(file => !file.Path.EndsWith("manifest.daxlib", StringComparison.Ordinal))).ToArray();
        Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(snapshot, null, default));
    }

    /// <summary>目录身份、分组和函数注解不得和 manifest 分离.</summary>
    [Theory]
    [InlineData("packages/e/other/1.0.0/")]
    [InlineData("packages/z/example.test/1.0.0/")]
    [InlineData("packages/e/example.test/2.0.0/")]
    [InlineData("packages/example.test/1.0.0/")]
    public void RejectsDirectoryIdentityMismatch(string prefix) => Assert.ThrowsAny<Exception>(() => GitUdfCatalogSource.ReadSnapshot(Files("1.0.0", prefix), null, default));

    /// <summary>链接、可执行部件、目录逃逸和不允许的媒体部件全部拒绝.</summary>
    [Theory]
    [InlineData("packages/e/example.test/1.0.0/lib/functions.tmdl", "120000")]
    [InlineData("packages/e/example.test/1.0.0/run.exe", "100644")]
    [InlineData("packages/e/example.test/1.0.0/LICENSE.cmd", "100644")]
    [InlineData("packages/e/example.test/1.0.0/../README.md", "100644")]
    [InlineData("packages/e/example.test/1.0.0/x.png", "100755")]
    public void RejectsUnsafePackageParts(string path, string mode)
    {
        var snapshot = Files("1.0.0").Where(file => file.Path != path).Append(new GitUdfFile(path, mode, "bad"u8.ToArray())).ToArray();
        Assert.ThrowsAny<Exception>(() => GitUdfCatalogSource.ReadSnapshot(snapshot, null, default));
    }

    /// <summary>相同路径大小写重复、缺函数及伪造函数来源都整批拒绝.</summary>
    [Fact]
    public void RejectsDuplicateMissingAndForgedFunctions()
    {
        var valid = Files("1.0.0");
        Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(valid.Append(valid[0] with { Path = valid[0].Path.ToUpperInvariant() }).ToArray(), null, default));
        Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(valid.Take(1).ToArray(), null, default));
        Assert.Throws<AppException>(() => GitUdfCatalogSource.ReadSnapshot(valid.Select(file => file.Path.EndsWith(".tmdl", StringComparison.Ordinal) ? file with { Content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Content).Replace("PackageVersion = 1.0.0", "PackageVersion = 9.0.0", StringComparison.Ordinal)) } : file).ToArray(), null, default));
    }

    /// <summary>非 HTTP(S)、凭据和回环 URL 在创建来源适配器时拒绝.</summary>
    [Theory]
    [InlineData("file:///C:/repo")]
    [InlineData("ssh://github.com/a/b")]
    [InlineData("https://user:secret@github.com/a/b")]
    [InlineData("https://127.0.0.1/repo")]
    [InlineData("https://github.com/a/b?q=x")]
    [InlineData("https://github.com/a/b/tree/main")]
    public void RejectsUnsafeSource(string url) => Assert.Throws<AppException>(() => new GitUdfCatalogSource("unused", Source(url)));

    /// <summary>引用只支持普通名称和 commit, 不能带选项、refspec、shell 表达式或 revision 运算符.</summary>
    [Theory]
    [InlineData("--upload-pack=x")]
    [InlineData("main:refs/heads/other")]
    [InlineData("HEAD~1")]
    [InlineData("main\nnext")]
    [InlineData("refs/heads/../main")]
    [InlineData("main.lock")]
    [InlineData("")]
    public void RejectsUnsafeReference(string reference) => Assert.Throws<AppException>(() => GitUdfCatalogSource.ValidateReference(reference));

    /// <summary>合法分支、标签和固定 SHA 保持精确字符, 不执行替换.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    [InlineData("refs/tags/v1.0.0")]
    [InlineData("e94dbbf72994411d55a75bf3f223a913eac3edaf")]
    public void AcceptsConstrainedReferences(string? reference) => GitUdfCatalogSource.ValidateReference(reference);

    /// <summary>预取消在任何 DNS 或用户目录创建前传播, 不伪装为成功快照.</summary>
    [Fact]
    public async Task CancellationDoesNotCreateStagingOrPartialResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "git-udf-unused-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitUdfCatalogSource(directory, Source()).DownloadAsync(null, new CancellationToken(true)));
        Assert.False(Directory.Exists(directory));
        Assert.ThrowsAny<OperationCanceledException>(() => GitUdfCatalogSource.ReadSnapshot(Files("1.0.0"), null, new CancellationToken(true)));
    }

    /// <summary>构造标准小包 fixture, 使用正式 ZIP 解析入口相同的函数内容.</summary>
    private static GitUdfFile[] Files(string version, string prefix = "packages/e/example.test/1.0.0/")
    {
        if (prefix == "packages/e/example.test/1.0.0/") prefix = $"packages/e/example.test/{version}/";
        using var zip = new ZipArchive(new MemoryStream(UdfPackageTests.Package()), ZipArchiveMode.Read);
        return [.. zip.Entries.Select(entry => { using var reader = new StreamReader(entry.Open()); return File(prefix + entry.FullName, reader.ReadToEnd().Replace("1.0.0", version, StringComparison.Ordinal)); })];
    }

    /// <summary>fixture 文件只存在内存, 不创建仓库或执行内容.</summary>
    private static GitUdfFile File(string path, string text) => new(path, "100644", Encoding.UTF8.GetBytes(text));
    /// <summary>测试来源保留正式 git 协议和默认远端 HEAD.</summary>
    private static UdfSourceEntry Source(string url = "https://github.com/daxlib/daxlib") => new("test", "test", url, "baseline", "custom", "git");
}
