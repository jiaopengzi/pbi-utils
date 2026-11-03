// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfPackageTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证函数包路径、身份、依赖及固定 HTTP 来源边界.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Packages;
using System.IO.Compression;
using System.Text;
using System.Net;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>以受控包验证解析边界, 不执行任何 DAX.</summary>
public sealed class UdfPackageTests
{
    internal static byte[] Package(params (string Name, string Text)[] extra)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in new[] { ("manifest.daxlib", "{\"id\":\"Example.Test\",\"version\":\"1.0.0\",\"description\":\"test\"}"),
                ("lib/functions.tmdl", "/// 说明\nfunction 'Example.Test.One' = () => 1\n\tannotation DAXLIB_PackageId = Example.Test\n\tannotation DAXLIB_PackageVersion = 1.0.0\n") }.Concat(extra))
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        return output.ToArray();
    }

    /// <summary>合法函数保留注解和许可.</summary>
    [Fact]
    public void ReadsFunctionAndLicense()
    {
        var package = DaxLibPackageReader.Read(Package(("LICENSE", "MIT")), "example.test", "1.0.0");
        Assert.Equal("Example.Test.One", Assert.Single(package.Functions).Name);
        Assert.Equal("Example.Test", package.Functions[0].Annotations["DAXLIB_PackageId"]);
        Assert.Equal("MIT", package.LicenseFiles["LICENSE"]);
    }
    /// <summary>目录逃逸、重复文件及可执行文件全部拒绝.</summary>
    [Theory]
    [InlineData("../bad.txt")]
    [InlineData("lib/../bad.txt")]
    [InlineData("/root.txt")]
    [InlineData("lib\\bad.txt")]
    [InlineData("manifest.daxlib")]
    [InlineData("run.exe")]
    [InlineData("LICENSE.exe")]
    [InlineData("NOTICE.cmd")]
    public void RejectsUnsafeEntries(string name) => Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package((name, "bad")), "Example.Test", "1.0.0"));
    /// <summary>包内容身份不得替代请求中的精确身份.</summary>
    [Fact] public void RejectsMismatchedIdentity() => Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package(), "Other", "1.0.0"));
    /// <summary>未知 manifest schema 不猜测兼容性.</summary>
    [Fact]
    public void RejectsUnknownManifestSchema()
    {
        using var output = new MemoryStream(); output.Write(Package());
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("manifest.daxlib")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.daxlib").Open());
            writer.Write("{\"id\":\"Example.Test\",\"version\":\"1.0.0\",\"$schema\":\"https://example.com/manifest.99.schema.json\"}");
        }
        Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(output.ToArray(), "Example.Test", "1.0.0"));
    }
    /// <summary>超额解压文件在解析前失败.</summary>
    [Fact] public void RejectsExpandedFileLimit() => Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package(("README.md", new string('a', 33 * 1024 * 1024))), "Example.Test", "1.0.0"));
    /// <summary>未知或可执行内容类型不能以 XML 声明绕过扩展名限制.</summary>
    [Fact] public void RejectsExecutableContentType() => Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package(("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"tmdl\" ContentType=\"application/x-msdownload\" /></Types>")), "Example.Test", "1.0.0"));
    /// <summary>官方 OPC 包将 PNG 图标声明为通用二进制类型, 仍须验证实际图片签名.</summary>
    [Theory]
    [InlineData("<Default Extension=\"png\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Override PartName=\"/icon.png\" ContentType=\"application/octet-stream\" />")]
    public void ReadsOfficialPngWithGenericBinaryContentType(string declaration)
    {
        var package = DaxLibPackageReader.Read(PackageWithIcon(declaration), "Example.Test", "1.0.0");
        Assert.Equal("Example.Test.One", Assert.Single(package.Functions).Name);
        Assert.Equal("MIT", package.LicenseFiles["LICENSE"]);
    }
    /// <summary>OPC 默认扩展名声明不要求包内一定包含该类部件.</summary>
    [Fact]
    public void ReadsUnusedDefaultPngContentType()
    {
        var package = DaxLibPackageReader.Read(Package(("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"png\" ContentType=\"application/octet-stream\" /></Types>")), "Example.Test", "1.0.0");
        Assert.Equal("Example.Test.One", Assert.Single(package.Functions).Name);
    }
    /// <summary>通用二进制类型只兼容 PNG 图标, 不放宽代码、文档和其他图片类型.</summary>
    [Theory]
    [InlineData("<Default Extension=\"tmdl\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Default Extension=\"daxlib\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Default Extension=\"svg\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Default Extension=\"exe\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Override PartName=\"/lib/functions.tmdl\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Override PartName=\"/missing.png\" ContentType=\"application/octet-stream\" />")]
    public void RejectsGenericBinaryContentTypeForOtherParts(string declaration)
    {
        var error = Assert.Throws<AppException>(() => DaxLibPackageReader.Read(PackageWithIcon(declaration), "Example.Test", "1.0.0"));
        Assert.Equal("UDF_PACKAGE_INVALID", error.Code);
    }
    /// <summary>合法 PNG 扩展名及 MIME 兼容声明不能代替实际的 PNG 文件签名.</summary>
    [Theory]
    [InlineData("<Default Extension=\"png\" ContentType=\"application/octet-stream\" />")]
    [InlineData("<Override PartName=\"/icon.png\" ContentType=\"application/octet-stream\" />")]
    public void RejectsGenericBinaryIconWithoutPngSignature(string declaration)
    {
        var error = Assert.Throws<AppException>(() => DaxLibPackageReader.Read(PackageWithIcon(declaration, false), "Example.Test", "1.0.0"));
        Assert.Equal("UDF_PACKAGE_INVALID", error.Code);
    }
    /// <summary>只构造最小包和图片字节, 不替代真实包解析或身份验证.</summary>
    private static byte[] PackageWithIcon(string declaration, bool validSignature = true)
    {
        using var output = new MemoryStream();
        output.Write(Package(("LICENSE", "MIT"), ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" + declaration + "</Types>")));
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            using var stream = zip.CreateEntry("icon.png").Open();
            stream.Write(validSignature ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jw1kAAAAASUVORK5CYII=") : [0x4d, 0x5a, 0, 0]);
        }
        return output.ToArray();
    }
    /// <summary>有界条目数独立于包总字节数.</summary>
    [Fact] public void RejectsTooManyEntries() => Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package([.. Enumerable.Range(0, 2047).Select(i => ("LICENSE-" + i + ".txt", "x"))]), "Example.Test", "1.0.0"));
    /// <summary>展开总额与下载大小都独立有界.</summary>
    [Fact]
    public void RejectsTotalExpansionAndDownloadLimit()
    {
        Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(new byte[32 * 1024 * 1024 + 1], "Example.Test", "1.0.0"));
        var text = new string('a', 27 * 1024 * 1024);
        Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(Package([.. Enumerable.Range(0, 5).Select(i => ("LICENSE-" + i + ".txt", text))]), "Example.Test", "1.0.0"));
    }
    /// <summary>UNIX 符号链接即使名称合法也拒绝.</summary>
    [Fact]
    public void RejectsSymbolicLink()
    {
        using var output = new MemoryStream(); output.Write(Package());
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true)) { var link = zip.CreateEntry("LICENSE"); link.ExternalAttributes = unchecked((int)0xA1FF0000); }
        Assert.ThrowsAny<Exception>(() => DaxLibPackageReader.Read(output.ToArray(), "Example.Test", "1.0.0"));
    }

    /// <summary>只在显式刷新请求固定 GitHub 主机, 搜索文本不进入网络且稳定版过滤在本地完成.</summary>
    [Fact]
    public async Task HttpUsesTrustedHostsAndLocalQuery()
    {
        var requests = new List<Uri>();
        var fixture = new GitHubUdfFixture(Package(), GitHubUdfFixture.Package("Beta", "2.0.0-beta", [("Beta.One", "() => 1", "")]));
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request => { requests.Add(request.RequestUri!); return fixture.Respond(request); })));
            var provider = router.DefaultProvider;
            await provider.RefreshAsync(null, default);
            var count = requests.Count;
            Assert.Single(await provider.SearchAsync("", false, default));
            Assert.Equal(2, (await provider.SearchAsync("", true, default)).Count);
            Assert.Empty(await provider.SearchAsync("a&b /", false, default));
            Assert.Equal(count, requests.Count);
            Assert.All(requests, request => Assert.True(request.Host is "api.github.com" or "codeload.github.com" or "github.com"));
            Assert.All(requests, request => Assert.DoesNotContain("a&b", request.AbsoluteUri, StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>已发布精确包完全离线读取, 显式刷新失败保留相同内容及当前目录事实.</summary>
    [Fact]
    public async Task PublishedPackageRemainsReadableWhenRefreshFailsOffline()
    {
        var offline = false;
        var fixture = new GitHubUdfFixture(Package());
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request => offline ? throw new HttpRequestException("offline") : fixture.Respond(request))));
            var provider = router.DefaultProvider;
            var before = await provider.RefreshAsync(null, default);
            var first = await provider.GetAsync("Example.Test", "1.0.0", default);
            offline = true;
            var cached = await provider.GetAsync("Example.Test", "1.0.0", default);
            Assert.True(cached.FromCache); Assert.Equal(first.Hash, cached.Hash);
            Assert.Equal("UDF_GIT_NETWORK", (await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default))).Code);
            Assert.Equal(before, await provider.GetStatusAsync(default));
            Assert.Equal(first.Hash, (await provider.GetAsync("Example.Test", "1.0.0", default)).Hash);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>重定向响应不解析也不追随, 不发布部分目录.</summary>
    [Fact]
    public async Task RejectsRedirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-redirect-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } })));
            await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default));
            Assert.False((await router.DefaultProvider.GetStatusAsync(default)).Initialized);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>预取消不能被完整本地缓存吞掉并报告成功.</summary>
    [Fact]
    public async Task CancelledRequestDoesNotReturnCachedSuccess()
    {
        var fixture = new GitHubUdfFixture(Package());
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(fixture.Respond)));
            await router.DefaultProvider.RefreshAsync(null, default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DefaultProvider.GetAsync("Example.Test", "1.0.0", new CancellationToken(true)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>未知当前目录格式不能因在线刷新而被覆盖, 保留用户原始文件.</summary>
    [Fact]
    public async Task UnknownCatalogCacheIsPreserved()
    {
        var fixture = new GitHubUdfFixture(Package());
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(fixture.Respond)));
            await router.DefaultProvider.RefreshAsync(null, default);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(root, "udf", "catalog"), "current.json", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(file, "{\"schemaVersion\":999}");
            await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetVersionsAsync("Example.Test", default));
            await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default));
            Assert.Equal("{\"schemaVersion\":999}", await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>只替代 HTTP 传输, 请求取消仍正常传播.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        /// <summary>返回受控响应, 不访问公网或用户配置.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
    }

    /// <summary>单函数选择只纳入可证明的传递闭包, 字符串及注释不产生伪依赖.</summary>
    [Fact]
    public async Task ResolvesTransitiveFunctionsWithoutInstallingWholePackage()
    {
        var package = Model("Example.Test", "1.0.0", [],
            Function("Example.Test.One", "() => Example.Test.Two() + LEN(\"Unknown.Call()\") /* No.Call() */"),
            Function("Example.Test.Two", "() => Example.Test.Three()"), Function("Example.Test.Three", "() => 1"), Function("Example.Test.Unused", "() => 9"));
        var resolver = new UdfDependencyResolver(new Packages(package), new NoLocal());
        var result = await resolver.ResolveAsync([new("daxlib", package.Id, package.Version, ["Example.Test.One"])], default);
        Assert.Equal(3, result.Functions.Count); Assert.True(Assert.Single(result.Packages).Partial);
        Assert.Equal(2, result.Functions.Count(f => f.IsDependency));
    }
    /// <summary>未知调用与循环不得静默丢失.</summary>
    [Theory]
    [InlineData("() => UnknownCall()")]
    [InlineData("() => Example.Test.One()")]
    [InlineData("() => (Unknown)()")]
    public async Task RejectsUnknownAndCycle(string expression)
    {
        var package = Model("Example.Test", "1.0.0", [], Function("Example.Test.One", expression));
        await Assert.ThrowsAnyAsync<Exception>(() => new UdfDependencyResolver(new Packages(package), new NoLocal()).ResolveAsync([new("daxlib", package.Id, package.Version, ["Example.Test.One"])], default));
    }
    /// <summary>稳定版声明的精确预发行依赖自动进入闭包, 无需额外授权开关.</summary>
    [Fact]
    public async Task ResolvesDeclaredPrereleaseDependencyAutomatically()
    {
        var dependency = Model("Other", "1.0.0-beta", [], Function("Other.One", "() => 1"));
        var package = Model("Example.Test", "1.0.0", [new("Other", "1.0.0-beta")], Function("Example.Test.One", "() => Other.One()"));
        var resolver = new UdfDependencyResolver(new Packages(package, dependency), new NoLocal());
        var result = await resolver.ResolveAsync([new("daxlib", package.Id, package.Version, ["Example.Test.One"])], default);
        Assert.Equal(2, result.Functions.Count);
        Assert.Contains(result.Functions, item => item.Function.Name == "Other.One" && item.Version == "1.0.0-beta" && item.IsDependency);
    }
    /// <summary>未固定 tag 的多选冻结每包数值最新稳定版, 不误选 beta 或按字符排序.</summary>
    [Fact]
    public async Task MultipleFunctionsUseLatestStable()
    {
        var package = Model("Example.Test", "1.10.0", [], Function("Example.Test.One", "() => 1"), Function("Example.Test.Two", "() => 2"));
        var resolver = new UdfDependencyResolver(new Packages(package, package with { Version = "1.9.0" }, package with { Version = "2.0.0-beta" }), new NoLocal());
        var result = await resolver.ResolveAsync([new("daxlib", package.Id, null, ["Example.Test.One", "Example.Test.Two"])], default);
        Assert.All(result.Functions, f => Assert.Equal("1.10.0", f.Version));
    }
    /// <summary>只有 beta 的包可直接使用明确版本, 未指定时仍失败关闭.</summary>
    [Fact]
    public async Task BetaOnlyMultipleSelectionRequiresExactVersion()
    {
        var package = Model("Example.Test", "1.0.0-beta", [], Function("Example.Test.One", "() => 1"), Function("Example.Test.Two", "() => 2"));
        var resolver = new UdfDependencyResolver(new Packages(package), new NoLocal());
        Assert.Equal(2, (await resolver.ResolveAsync([new("daxlib", package.Id, package.Version, ["Example.Test.One", "Example.Test.Two"])], default)).Functions.Count);
        await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync([new("daxlib", package.Id, null, ["Example.Test.One", "Example.Test.Two"])], default));
    }
    /// <summary>两个包声明同一依赖的不同版本时拒绝选择.</summary>
    [Fact]
    public async Task RejectsConflictingDeclaredVersions()
    {
        var a = Model("A", "1.0.0", [new("Shared", "1.0.0")], Function("A.One", "() => 1"));
        var b = Model("B", "1.0.0", [new("Shared", "2.0.0")], Function("B.One", "() => 2"));
        var shared = Model("Shared", "1.0.0", [], Function("Shared.One", "() => 3"));
        var resolver = new UdfDependencyResolver(new Packages(a, b, shared, shared with { Version = "2.0.0" }), new NoLocal());
        await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync([new("daxlib", "A", null, ["A.One"]), new("daxlib", "B", null, ["B.One"])], default));
    }
    /// <summary>相同作用域中另一个用户显式选择不能代替包的正式依赖声明.</summary>
    [Fact]
    public async Task RejectsUndeclaredCrossPackageReference()
    {
        var a = Model("A", "1.0.0", [], Function("A.One", "() => B.One()"));
        var b = Model("B", "1.0.0", [], Function("B.One", "() => 2"));
        await Assert.ThrowsAnyAsync<Exception>(() => new UdfDependencyResolver(new Packages(a, b), new NoLocal()).ResolveAsync([new("daxlib", "A", null, ["A.One"]), new("daxlib", "B", null, ["B.One"])], default));
    }
    private static UdfFunction Function(string name, string expression) => new(name, expression, "原说明", new Dictionary<string, string>());
    private static UdfPackage Model(string id, string version, UdfPackageDependency[] dependencies, params UdfFunction[] functions) => new(id, version, "hash-" + version, "", "", new Dictionary<string, string>(), dependencies, functions, DateTimeOffset.UtcNow);
    private sealed class Packages(params UdfPackage[] values) : IUdfPackageProvider
    {
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(values.Where(p => p.Id == packageId).Select(p => p.Version).ToArray());
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => Task.FromResult(values.Single(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && p.Version == version));
    }
    private sealed class NoLocal : ILocalUdfStore
    {
        public Task<IReadOnlyList<LocalUdfEntry>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalUdfEntry> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalUdfEntry> CreateAsync(LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task<LocalUdfEntry> UpdateAsync(string id, string baseline, LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
    }
}
