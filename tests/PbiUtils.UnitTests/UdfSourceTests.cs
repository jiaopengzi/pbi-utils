// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfSourceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证第三方函数源的稳定身份、基线、网络边界和来源隔离.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Repositories;
using System.Net;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用独占临时目录和内存 HTTP 响应, 不访问真实用户配置或网络.</summary>
public sealed class UdfSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-sources-" + Guid.NewGuid().ToString("N"));

    /// <summary>新增不联网, 修改和删除绑定宿主分配身份及当前内容基线.</summary>
    [Fact]
    public async Task RegistryPreservesStableIdentityAndRequiresCurrentBaseline()
    {
        var store = new UdfSourceStore(root);
        Assert.Equal(["daxlib", "local"], (await store.ListAsync(default)).Select(item => item.Id));
        var first = await store.SaveAsync("Team library", "https://github.com/example/functions", null, null, default);
        Assert.StartsWith("source:", first.Id, StringComparison.Ordinal);
        Assert.Equal("https://github.com/example/functions", first.BaseUrl);
        var second = await new UdfSourceStore(root).SaveAsync("Renamed", first.BaseUrl!, first.Id, first.Baseline, default);
        Assert.Equal(first.Id, second.Id); Assert.NotEqual(first.Baseline, second.Baseline);
        Assert.Equal("UDF_SOURCE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => store.DeleteAsync(first.Id, first.Baseline!, default))).Code);
        await store.DeleteAsync(second.Id, second.Baseline!, default);
        Assert.Equal(2, (await store.ListAsync(default)).Count);
    }

    /// <summary>未知版本、重复字段和损坏内容不被新增或更新覆盖.</summary>
    [Theory]
    [InlineData("{\"schemaVersion\":999,\"items\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"items\":[]}")]
    [InlineData("broken")]
    public async Task RegistryPreservesUnknownOrCorruptFile(string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "udf"));
        var path = Path.Combine(root, "udf", "sources.json");
        await File.WriteAllTextAsync(path, content);
        await Assert.ThrowsAnyAsync<Exception>(() => new UdfSourceStore(root).SaveAsync("Team", "https://github.com/example/functions", null, null, default));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    /// <summary>来源配置拒绝凭据、歧义路径、回环地址和附加查询权限.</summary>
    [Theory]
    [InlineData("http://github.com/example/functions")]
    [InlineData("https://user:secret@github.com/example/functions")]
    [InlineData("https://github.com/example/functions?token=secret")]
    [InlineData("https://github.com/example/functions#part")]
    [InlineData("https://localhost/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://api.daxlib.org/")]
    [InlineData("https://github.com/example/functions/tree/main")]
    [InlineData("git@github.com:example/functions.git")]
    public async Task RegistryRejectsUnsafeEndpoint(string url) =>
        await Assert.ThrowsAsync<AppException>(() => new UdfSourceStore(root).SaveAsync("Team", url, null, null, default));

    /// <summary>登记不联网, GitHub 保持原身份, 其他仓库保留大小写与 .git 路由并支持内网端口.</summary>
    [Theory]
    [InlineData("https://GITEE.COM/Team/Library.git/", "https://gitee.com/Team/Library.git")]
    [InlineData("https://GitLab.com/Group/Sub/Library", "https://gitlab.com/Group/Sub/Library")]
    [InlineData("http://git.internal:8080/Library.git", "http://git.internal:8080/Library.git")]
    [InlineData("http://10.10.10.20/Library", "http://10.10.10.20/Library")]
    public async Task RegistrySupportsGenericAnonymousHttpRepositories(string url, string expected)
    {
        var store = new UdfSourceStore(root);
        var saved = await store.SaveAsync("Team", url, null, null, default);
        Assert.Equal(expected, saved.BaseUrl);
        Assert.Equal(saved, await new UdfSourceStore(root).GetAsync(saved.Id, default));
    }

    /// <summary>URL 解析前拒绝编码和路径归一化歧义, 不把规范化后的另一个目标登记为用户意图.</summary>
    [Theory]
    [InlineData("https://git.internal/Group/../Library.git")]
    [InlineData("https://git.internal/Group/%2e%2e/Library.git")]
    [InlineData("https://git.internal/Group//Library.git")]
    [InlineData("https://git.internal/Group\\Library.git")]
    [InlineData("https://git.internal/Library.git?token=secret")]
    [InlineData("http://169.254.169.254/Library.git")]
    [InlineData("http://[::1]/Library.git")]
    [InlineData("http://0.0.0.0/Library.git")]
    [InlineData("http://localhost/Library.git")]
    public async Task RegistryRejectsAmbiguousOrSpecialAddress(string url) =>
        await Assert.ThrowsAsync<AppException>(() => new UdfSourceStore(root).SaveAsync("Team", url, null, null, default));

    /// <summary>实际连接检查 DNS 结果, 包括 IPv4 映射和文档/保留地址.</summary>
    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void PublicAddressBoundary(string address, bool expected) => Assert.Equal(expected, UdfSourceNetwork.IsPublicAddress(IPAddress.Parse(address)));

    /// <summary>来源修改不能让旧选择悄悄访问新地址, 自定义来源无官方回退.</summary>
    [Fact]
    public async Task RouterRequiresSelectedSourceBaselineAndUsesItsEndpoint()
    {
        var requests = new List<Uri>();
        var fixture = GitHubUdfFixture.Catalog();
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request =>
        { requests.Add(request.RequestUri!); return fixture.Respond(request); })));
        var source = await router.Sources.SaveAsync("Team", "https://github.com/example/functions", null, null, default);
        var provider = await router.GetProviderAsync(source.Id, source.Baseline, default);
        await Assert.IsType<IUdfCatalogProvider>(provider, exactMatch: false).RefreshAsync(null, default);
        Assert.All(requests, request => Assert.Contains("/example/functions", request.AbsolutePath, StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Host == "api.github.com");
        Assert.Contains(requests, request => request.Host == "codeload.github.com");
        await router.Sources.SaveAsync("Team", "https://github.com/example/other", source.Id, source.Baseline, default);
        Assert.Equal("UDF_SOURCE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => router.GetProviderAsync(source.Id, source.Baseline, default))).Code);
        await Assert.ThrowsAsync<AppException>(() => router.GetProviderAsync(source.Id, null, default));
    }

    /// <summary>仅新增来源字段即可供 CLI 和桌面共享请求校验, 非宿主身份仍被拒绝.</summary>
    [Fact]
    public void ScaffoldAcceptsStableCustomSourceIdentity()
    {
        var selection = new UdfSelection("source:" + Guid.NewGuid().ToString("N"), "Team.Library", "1.0.0", ["Team.One"], SourceBaseline: new string('a', 64));
        ScaffoldValidation.Validate(new(1, "builtin:blank", new string('b', 64), "Test", "zh-cn", null, null, null, null, [selection]));
    }

    /// <summary>取消发生在原子提交后仍返回已提交条目, 重试不得抹去事实.</summary>
    [Fact]
    public async Task RegistryCommitBoundaryPreservesCompletedWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new UdfSourceStore(root);
        var item = await store.SaveAsync("Team", "https://github.com/example/functions", null, null, cancellation.Token, cancellation.Cancel);
        Assert.Equal(item, await store.GetAsync(item.Id, default));
        var before = await store.ListAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("Other", "https://github.com/example/other", null, null, cancellation.Token));
        Assert.Equal(before, await store.ListAsync(default));
    }

    /// <summary>完整 JSON 大小在提交前校验, 多条合法 Unicode 引用不能写出自身无法读取的配置.</summary>
    [Fact]
    public async Task RegistryRejectsOversizedCandidateBeforeCommitAndPreservesReadableFile()
    {
        var store = new UdfSourceStore(root);
        var path = Path.Combine(root, "udf", "sources.json");
        var saved = 0; var rejected = false;
        for (var index = 0; index < 32; index++)
        {
            var before = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            var committed = false;
            try
            {
                await store.SaveAsync("源" + index + new string('中', 70), "https://github.com/example/functions" + index, null, null,
                    default, () => committed = true, gitRef: new string('汉', 256));
                Assert.True(committed); saved++;
                Assert.Equal(saved + 2, (await store.ListAsync(default)).Count);
            }
            catch (AppException exception) when (exception.Code == "UDF_SOURCE_STORE_LIMIT")
            {
                Assert.False(committed); Assert.Equal(before, await File.ReadAllBytesAsync(path));
                Assert.Equal(saved + 2, (await store.ListAsync(default)).Count);
                rejected = true; break;
            }
        }
        Assert.True(rejected);
    }

    /// <summary>GitHub 地址拒绝超额输入并确定性规范化, 保存后可由同一读取边界恢复.</summary>
    [Fact]
    public async Task RegistryNeverPersistsAnEndpointItsReaderWouldReject()
    {
        var store = new UdfSourceStore(root);
        await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("Too long", "https://github.com/example/" + new string('a', 2048), null, null, default));
        await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("Escaped too long", "https://github.com/example/" + new string('汉', 300), null, null, default));
        Assert.Equal(2, (await store.ListAsync(default)).Count);
        var valid = await store.SaveAsync("Canonical", "https://GitHub.com/Example/Functions.git/", null, null, default);
        Assert.Equal("https://github.com/example/functions", valid.BaseUrl);
        Assert.Equal(valid, await store.GetAsync(valid.Id, default));
    }

    /// <summary>同名函数库在不同来源各自解析版本、依赖和许可, 不回退官方包.</summary>
    [Fact]
    public async Task ResolverKeepsLibrariesAndDependenciesInsideSelectedSource()
    {
        using var router = new UdfSourceProvider(root, entry => new HttpClient(new Handler(request => GitHubUdfFixture.Dependencies(entry.Name).Respond(request))));
        var first = await router.Sources.SaveAsync("First", "https://github.com/first/functions", null, null, default);
        var second = await router.Sources.SaveAsync("Second", "https://github.com/second/functions", null, null, default);
        var firstProvider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(first.Id, first.Baseline, default), exactMatch: false);
        var secondProvider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(second.Id, second.Baseline, default), exactMatch: false);

        var firstStatus = await firstProvider.RefreshAsync(null, default);
        var secondStatus = await secondProvider.RefreshAsync(null, default);
        var official = new RejectPackages();
        var resolver = new UdfDependencyResolver(official, new LocalUdfStore(root), router);
        var result = await resolver.ResolveAsync([
            new(first.Id, "Team.Library", "1.0.0", ["First.One"], SourceBaseline: first.Baseline, CatalogBaseline: firstStatus.Baseline),
            new(second.Id, "Team.Library", "1.0.0", ["Second.One"], SourceBaseline: second.Baseline, CatalogBaseline: secondStatus.Baseline)], default);
        Assert.Equal(4, result.Functions.Count);
        Assert.Equal(4, result.Packages.Count);
        Assert.All(result.Packages, item => Assert.Contains(item.Source, new[] { first.Id, second.Id }));
        Assert.Equal(2, result.Packages.Count(item => item.Id == "Shared"));
        Assert.Equal(0, official.Reads);
    }

    /// <summary>本地声明的缺失函数不能由另一已选择来源的同名函数满足.</summary>
    [Fact]
    public async Task LocalDeclaredDependencyCannotBorrowFunctionFromAnotherSource()
    {
        using var router = new UdfSourceProvider(root, entry => new HttpClient(new Handler(request => GitHubUdfFixture.Dependencies(entry.Name).Respond(request))));
        var first = await router.Sources.SaveAsync("First", "https://github.com/first/functions", null, null, default);
        var second = await router.Sources.SaveAsync("Second", "https://github.com/second/functions", null, null, default);
        var firstProvider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(first.Id, first.Baseline, default), exactMatch: false);
        var secondProvider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(second.Id, second.Baseline, default), exactMatch: false);

        await firstProvider.RefreshAsync(null, default);
        var secondStatus = await secondProvider.RefreshAsync(null, default);
        var local = new LocalUdfStore(root);
        var entry = await local.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", [new(first.Id, "Team.Library", "1.0.0", "Second.One", SourceBaseline: first.Baseline)]), default);
        var resolver = new UdfDependencyResolver(new RejectPackages(), local, router);
        var exception = await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([
            new(second.Id, "Team.Library", "1.0.0", ["Second.One"], SourceBaseline: second.Baseline, CatalogBaseline: secondStatus.Baseline),
            new("local", entry.Id, "1.0.0", ["Local.One"], Baseline: entry.Baseline)], default));
        Assert.Equal("UDF_DEPENDENCY_UNKNOWN", exception.Code);
    }

    /// <summary>缓存按来源隔离, 断网来源不能误用另一来源的同名包.</summary>
    [Fact]
    public async Task PackageCacheDoesNotCrossSourceBoundary()
    {
        var offline = false;
        var fixture = GitHubUdfFixture.Dependencies("First");
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request =>
        {
            if (offline) throw new HttpRequestException("offline");
            return fixture.Respond(request);
        })));
        var first = await router.Sources.SaveAsync("First", "https://github.com/first/functions", null, null, default);
        var second = await router.Sources.SaveAsync("Second", "https://github.com/second/functions", null, null, default);
        var firstProvider = await router.GetProviderAsync(first.Id, first.Baseline, default);
        await Assert.IsType<IUdfCatalogProvider>(firstProvider, exactMatch: false).RefreshAsync(null, default);
        offline = true;
        Assert.True((await firstProvider.GetAsync("Team.Library", "1.0.0", default)).FromCache);
        var secondProvider = await router.GetProviderAsync(second.Id, second.Baseline, default);
        Assert.Equal("UDF_CATALOG_UNINITIALIZED", (await Assert.ThrowsAsync<AppException>(() => secondProvider.GetAsync("Team.Library", "1.0.0", default))).Code);
    }

    /// <summary>旧 API 配置和未知版本不迁移, 读取及写入都保留原始用户文件.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacySourceSchemasFailSafelyWithoutMigration(int schemaVersion)
    {
        var path = Path.Combine(root, "udf", "sources.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion, items = new[] { new { id = "source:" + new string('a', 32), name = "Legacy", baseUrl = "https://api.daxlib.org/", protocol = "api" } } });
        await File.WriteAllBytesAsync(path, bytes);
        var store = new UdfSourceStore(root);
        Assert.Equal("UDF_SOURCE_STORE_INVALID", (await Assert.ThrowsAsync<AppException>(() => store.ListAsync(default))).Code);
        await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("New", "https://github.com/example/new", null, null, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    /// <summary>官方来源、路由默认值和新 schema 使用同一 GitHub 身份, 不隐含旧协议.</summary>
    [Fact]
    public async Task DefaultsAndPersistedSchemaUseGitHubOnly()
    {
        var store = new UdfSourceStore(root);
        Assert.Equal(UdfSourceDefaults.Official, (await store.ListAsync(default))[0]);
        Assert.Equal("git", UdfSourceDefaults.Official.Protocol);
        Assert.Equal("https://github.com/daxlib/daxlib", UdfSourceDefaults.Official.BaseUrl);
        await store.SaveAsync("Team", "https://github.com/example/functions", null, null, default);
        using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "udf", "sources.json")));
        Assert.Equal(3, saved.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("git", saved.RootElement.GetProperty("items")[0].GetProperty("protocol").GetString());
    }

    /// <summary>新来源不接受已退休 API 或任意其他协议, 即使地址本身是合法 GitHub 仓库.</summary>
    [Theory]
    [InlineData("api", null)]
    [InlineData("api", "main")]
    [InlineData("ssh", null)]
    public async Task UnsupportedProtocolsNeverBecomeImplicitGit(string protocol, string? gitRef)
    {
        var store = new UdfSourceStore(root);
        Assert.Equal("UDF_SOURCE_PROTOCOL_INVALID", (await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("Unsupported",
            "https://github.com/example/functions", null, null, default, protocol: protocol, gitRef: gitRef))).Code);
        Assert.Equal(2, (await store.ListAsync(default)).Count);
    }

    /// <summary>schema3 仍严格要求 Git 协议, 不把缺失字段或不受支持的地址当成迁移请求.</summary>
    [Theory]
    [InlineData("\"protocol\":\"api\",", "https://github.com/example/functions")]
    [InlineData("", "https://github.com/example/functions")]
    [InlineData("\"protocol\":\"git\",", "https://gitlab.com/example/functions?token=secret")]
    public async Task NewSchemaRejectsUnsupportedOrMissingSourceContract(string protocol, string url)
    {
        var path = Path.Combine(root, "udf", "sources.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = "{\"schemaVersion\":3,\"items\":[{\"id\":\"source:" + new string('a', 32)
            + "\",\"name\":\"Unsafe\"," + protocol + "\"baseUrl\":\"" + url + "\"}]}";
        await File.WriteAllTextAsync(path, text);
        Assert.Equal("UDF_SOURCE_STORE_INVALID", (await Assert.ThrowsAsync<AppException>(() => new UdfSourceStore(root).ListAsync(default))).Code);
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    /// <summary>内网仓库刷新发布完整固定提交快照后, 断网仍由同一来源身份读取精确版本.</summary>
    [Fact]
    public async Task GenericRepositoryPublishesCompleteSnapshotAndReadsOffline()
    {
        var fixture = new GitSmartUdfFixture();
        var offline = false;
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(request =>
        { if (offline) throw new HttpRequestException("offline"); return fixture.Respond(request); })));
        var entry = await router.Sources.SaveAsync("Internal", "http://git.internal:8080/Library.git", null, null, default);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(entry.Id, entry.Baseline, default), exactMatch: false);
        var status = await provider.RefreshAsync(null, default);
        Assert.NotNull(status.Baseline);
        offline = true;
        var package = await provider.GetAsync("Example.One", "1.0.0", default);
        Assert.True(package.FromCache);
        Assert.Equal("Example.Helper", Assert.Single(package.Functions).Name);
        Assert.Equal(4, fixture.Requests.Count);
    }

    /// <summary>实际连接只为显式非 GitHub 来源允许内网, 回环、元数据服务及保留地址继续拒绝.</summary>
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.31.1.1", true)]
    [InlineData("100.64.1.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void RepositoryAddressBoundary(string address, bool expected) => Assert.Equal(expected, UdfSourceNetwork.IsRepositoryAddress(IPAddress.Parse(address)));

    /// <summary>Git ref 进入配置基线, 任何旧 API 协议明确拒绝.</summary>
    [Fact]
    public async Task GitSourceProtocolAndReferenceAreConfigurationIdentity()
    {
        var store = new UdfSourceStore(root);
        var entry = await store.SaveAsync("Git", "https://github.com/example/functions", null, null, default, protocol: "git", gitRef: "main");
        Assert.Equal("git", entry.Protocol); Assert.Equal("main", entry.GitRef);
        var changed = await store.SaveAsync(entry.Name, entry.BaseUrl!, entry.Id, entry.Baseline, default, protocol: "git", gitRef: "release");
        Assert.NotEqual(entry.Baseline, changed.Baseline);
        Assert.Equal(changed, await store.GetAsync(changed.Id, default));
        await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("API", "https://api.example.org/", null, null, default, protocol: "api", gitRef: "main"));
    }

    /// <summary>目录基线即使尚未用于单选解析, 输入层仍拒绝非 SHA256 值.</summary>
    [Fact]
    public void ScaffoldRejectsMalformedOptionalCatalogBaseline()
    {
        var selection = new UdfSelection("daxlib", "Team.Library", "1.0.0", ["Team.One"], CatalogBaseline: "invalid");
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(new(1, "builtin:blank", new string('b', 64), "Test", "zh-cn", null, null, null, null, [selection])));
    }

    /// <summary>官方提供者被触碰就失败, 检查自定义来源不回退.</summary>
    private sealed class RejectPackages : IUdfPackageProvider
    {
        internal int Reads { get; private set; }
        /// <inheritdoc />
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) { Reads++; throw new InvalidOperationException(); }
        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) { Reads++; throw new InvalidOperationException(); }
        /// <inheritdoc />
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) { Reads++; throw new InvalidOperationException(); }
    }

    /// <summary>清理测试自己创建的目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    /// <summary>只替代传输, 真实提供者仍解析并缓存目录.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        /// <summary>不接触网络, 保留请求 URI 供断言.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
    }
}
