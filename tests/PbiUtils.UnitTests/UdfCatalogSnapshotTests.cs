// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogSnapshotTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证完整目录快照显式更新与本地读取边界.

using PbiUtils.Infrastructure.Udf.Sources;
using System.Net;
using System.Text.Json.Nodes;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用独占临时用户目录及受控传输, 不操作用户配置.</summary>
public sealed class UdfCatalogSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-snapshots-" + Guid.NewGuid().ToString("N"));

    /// <summary>停用的索引不参与浏览或迁移; 显式刷新可整体重建, 失败或取消均保持旧指针和文件字节.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task UnsupportedIndexRebuildRequiresExplicitSuccessfulRefresh(int oldSchema)
    {
        var fail = false;
        using var router = Router(request => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Fixture(request));
        var provider = Assert.IsType<IUdfCatalogProvider>(router.DefaultProvider, exactMatch: false);
        await provider.RefreshAsync(null, default);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var head = JsonNode.Parse(File.ReadAllBytes(pointer))!.AsObject();
        var manifest = Path.Combine(Path.GetDirectoryName(pointer)!, head["generation"]!.GetValue<string>(), "manifest.json");
        var index = JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject(); index["schemaVersion"] = oldSchema;
        foreach (var item in index["packages"]!.AsArray())
        {
            item!.AsObject().Remove("committedAt");
            if (oldSchema == 4) item["publishedAt"] = "2099-01-01T00:00:00+00:00";
            if (oldSchema == 2) { item.AsObject().Remove("authors"); item.AsObject().Remove("tags"); }
        }
        File.WriteAllText(manifest, index.ToJsonString());
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifest)));
        head["baseline"] = hash; head["manifestHash"] = hash; File.WriteAllText(pointer, head.ToJsonString());
        var pointerBytes = File.ReadAllBytes(pointer); var manifestBytes = File.ReadAllBytes(manifest);

        Assert.False((await provider.GetStatusAsync(default)).Initialized);
        Assert.Equal("UDF_CATALOG_UPDATE_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => provider.QueryPackagesAsync(new("", false), null, default))).Code);
        fail = true;
        await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default));
        Assert.Equal(pointerBytes, File.ReadAllBytes(pointer)); Assert.Equal(manifestBytes, File.ReadAllBytes(manifest));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(null, new(true)));
        Assert.Equal(pointerBytes, File.ReadAllBytes(pointer)); Assert.Equal(manifestBytes, File.ReadAllBytes(manifest));
        fail = false;
        var rebuilt = await provider.RefreshAsync(null, default);
        Assert.True(rebuilt.Initialized); Assert.NotEqual(hash, rebuilt.Baseline);
        Assert.Single((await provider.QueryPackagesAsync(new("", false), rebuilt.Baseline, default)).Items);
    }

    /// <summary>首次浏览只能引导显式更新, 不能触发任何网络请求.</summary>
    [Fact]
    public async Task UninitializedSearchNeverRequestsNetwork()
    {
        var requests = 0;
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(_ =>
        {
            requests++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
        })));
        var provider = await router.GetProviderAsync("daxlib", null, default);
        var error = await Assert.ThrowsAsync<AppException>(() => provider.SearchAsync("", false, default));
        Assert.Equal("UDF_CATALOG_UNINITIALIZED", error.Code);
        Assert.Equal(0, requests);
    }

    /// <summary>首次精确包读取也不能从网络填充零散缓存.</summary>
    [Fact]
    public async Task UninitializedPackageReadNeverRequestsNetwork()
    {
        var requests = 0;
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new Handler(_ =>
        {
            requests++;
            return new(HttpStatusCode.NotFound);
        })));
        var provider = await router.GetProviderAsync("daxlib", null, default);
        var error = await Assert.ThrowsAsync<AppException>(() => provider.GetAsync("Example.One", "1.0.0", default));
        Assert.Equal("UDF_CATALOG_UNINITIALIZED", error.Code);
        Assert.Equal(0, requests);
    }

    /// <summary>完整更新取得所有历史版本, 后续搜索、版本和精确包完全离线.</summary>
    [Fact]
    public async Task RefreshPublishesAllVersionsAndOfflineFunctionSearch()
    {
        var requests = 0;
        using var router = Router(request => { requests++; return Fixture(request); });
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        Assert.False((await provider.GetStatusAsync(default)).Initialized);
        var status = await provider.RefreshAsync(null, default);
        Assert.True(status.Initialized);
        Assert.Equal(1, status.PackageCount); Assert.Equal(2, status.VersionCount); Assert.Equal(2, status.FunctionCount);
        var afterRefresh = requests;
        Assert.Single(await provider.SearchAsync("Helper", false, default));
        Assert.Equal(2, (await provider.GetVersionsAsync("Example.One", default)).Count);
        Assert.True((await provider.GetAsync("Example.One", "1.0.0", default)).FromCache);
        Assert.Equal(afterRefresh, requests);
        using var reopened = Router(_ => throw new InvalidOperationException("network"));
        var reader = Assert.IsType<IUdfCatalogProvider>(await reopened.GetProviderAsync("daxlib", null, default), exactMatch: false);
        Assert.Equal(status, await reader.GetStatusAsync(default));
        Assert.Single(await reader.SearchAsync("helper description", false, default));
    }

    /// <summary>提交、完整树和压缩包任一下载失败不能发布部分新代.</summary>
    [Theory]
    [InlineData("commits")]
    [InlineData("trees")]
    [InlineData("archive")]
    [InlineData("history")]
    public async Task FailedRefreshPreservesPublishedSnapshot(string failed)
    {
        var fail = false;
        using var router = Router(request =>
        {
            var uri = request.RequestUri!;
            if (fail && (failed == "commits" && uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                || failed == "archive" && uri.Host == "codeload.github.com"
                || failed == "history" && uri.Host == "github.com"
                || failed == "trees" && uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))) throw new HttpRequestException("offline");
            return Fixture(request);
        });
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        fail = true;
        Assert.Equal("UDF_GIT_NETWORK", (await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default))).Code);
        Assert.Equal(before, await provider.GetStatusAsync(default));
        Assert.Equal(2, (await provider.GetVersionsAsync("Example.One", default)).Count);
    }

    /// <summary>提交前取消保留旧快照, committing 后取消仍返回真实新快照.</summary>
    [Fact]
    public async Task CancellationHonorsPublicationBoundary()
    {
        using var router = Router(Fixture);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(null, cancelled.Token));
        Assert.Equal(before, await provider.GetStatusAsync(default));
        using var duringCommit = new CancellationTokenSource();
        var committed = await provider.RefreshAsync(null, duringCommit.Token, duringCommit.Cancel);
        Assert.Equal(committed, await provider.GetStatusAsync(default));
        Assert.True(duringCommit.IsCancellationRequested);
    }

    /// <summary>生产多选必须带目录基线, 更新后不能静默采用新版本.</summary>
    [Fact]
    public async Task MultipleSelectionRejectsMissingOrStaleCatalogBaseline()
    {
        using var router = Router(Fixture);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var status = await provider.RefreshAsync(null, default);
        var resolver = new UdfDependencyResolver(provider, new LocalUdfStore(root), router);
        foreach (var baseline in new string?[] { null, new('a', 64) })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([
                new("daxlib", "Example.One", null, ["Example.Helper", "Example.Other"], CatalogBaseline: baseline)], default));
            Assert.Equal("UDF_CATALOG_CHANGED", error.Code);
        }
        var result = await resolver.ResolveAsync([new("daxlib", "Example.One", null, ["Example.Helper", "Example.Other"], CatalogBaseline: status.Baseline)], default);
        Assert.All(result.Functions, function => Assert.Equal("2.0.0", function.Version));
    }

    /// <summary>传输过程中来源被编辑后不能发布, 旧提供者也不可读取新来源配置.</summary>
    [Fact]
    public async Task SourceEditDuringRefreshCannotPublishOldConfiguration()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new AsyncHandler(async (request, token) =>
        {
            reached.TrySetResult(); await resume.Task.WaitAsync(token);
            return Fixture(request);
        })));
        var source = await router.Sources.SaveAsync("Original", "https://github.com/example/functions", null, null, default);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(source.Id, source.Baseline, default), exactMatch: false);
        var refresh = provider.RefreshAsync(null, default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var edited = await router.Sources.SaveAsync("Renamed", source.BaseUrl!, source.Id, source.Baseline, default);
        resume.SetResult();
        Assert.Equal("UDF_SOURCE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => refresh)).Code);
        Assert.Equal("UDF_SOURCE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => provider.GetStatusAsync(default))).Code);
        var current = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync(edited.Id, edited.Baseline, default), exactMatch: false);
        Assert.False((await current.GetStatusAsync(default)).Initialized);
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(root, "udf", "catalog"), "*", SearchOption.AllDirectories),
            path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
    }

    /// <summary>并发刷新由跨实例文件锁拒绝, 更新期间读者仍可完整读取旧代.</summary>
    [Fact]
    public async Task ConcurrentRefreshDoesNotBlockLocalReadersOrOverwriteSnapshot()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        using var router = new UdfSourceProvider(root, _ => new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (block) { reached.TrySetResult(); await resume.Task.WaitAsync(token); }
            return Fixture(request);
        })));
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        block = true;
        var update = provider.RefreshAsync(null, default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var other = Router(Fixture);
        var second = Assert.IsType<IUdfCatalogProvider>(await other.GetProviderAsync("daxlib", null, default), exactMatch: false);
        Assert.Equal("USER_DATA_BUSY", (await Assert.ThrowsAsync<AppException>(() => second.RefreshAsync(null, default))).Code);
        Assert.Equal(before, await second.GetStatusAsync(default));
        Assert.Single(await second.SearchAsync("Example", false, default));
        resume.SetResult(); await update;
    }

    /// <summary>损坏内容不降级为空目录或旧零散缓存, 更新也不能覆盖损坏的发布事实.</summary>
    [Fact]
    public async Task CorruptPublishedPackageFailsClosedAndPreservesPointer()
    {
        using var router = Router(Fixture);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        await provider.RefreshAsync(null, default);
        var catalog = Path.Combine(root, "udf", "catalog");
        var pointer = Directory.GetFiles(catalog, "current.json", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(pointer);
        var package = Directory.GetFiles(catalog, "*.json", SearchOption.AllDirectories).First(path => Path.GetFileNameWithoutExtension(path).Length == 64);
        await File.WriteAllTextAsync(package, "{}");
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => provider.GetStatusAsync(default))).Code);
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default))).Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(pointer));
    }

    /// <summary>目录内容未变时基线稳定, 多选冻结仍核验目录新增内容和精确函数 hash.</summary>
    [Fact]
    public async Task UnchangedRefreshKeepsBaselineAndFrozenMultiSelection()
    {
        using var router = Router(Fixture);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        var resolver = new UdfDependencyResolver(provider, new LocalUdfStore(root), router);
        var result = await resolver.ResolveAsync([new("daxlib", "Example.One", null, ["Example.Helper", "Example.Other"], CatalogBaseline: before.Baseline)], default);
        var after = await provider.RefreshAsync(null, default);
        Assert.Equal(before.Baseline, after.Baseline);
        await resolver.VerifyFrozenAsync(result, default);
    }

    /// <summary>固定会话在刷新后仍只读原代, 多选解析和冻结核验必须显式拒绝旧目录基线.</summary>
    [Fact]
    public async Task PinnedReadSessionCannotMixRefreshedGeneration()
    {
        var newer = false;
        using var router = Router(request => (newer ? NewerFixture : InitialFixture).Respond(request));
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        using var pinned = await Assert.IsType<IUdfCatalogSnapshotSource>(provider, exactMatch: false).OpenSnapshotAsync(default);
        var resolver = new UdfDependencyResolver(provider, new LocalUdfStore(root), router);
        var selections = new PbiUtils.Application.Udf.UdfSelection[]
        { new("daxlib", "Example.One", null, ["Example.Helper", "Example.Other"], CatalogBaseline: before.Baseline) };
        var frozen = await resolver.ResolveAsync(selections, default);
        newer = true;
        var after = await provider.RefreshAsync(null, default);
        Assert.NotEqual(before.Baseline, after.Baseline);
        Assert.Equal(before, pinned.Status);
        Assert.Equal(2, (await pinned.GetVersionsAsync("Example.One", default)).Count);
        Assert.Equal(3, (await provider.GetVersionsAsync("Example.One", default)).Count);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync(selections, default))).Code);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.VerifyFrozenAsync(frozen, default))).Code);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync(selections.Select(selection => selection with { CatalogBaseline = null }).ToArray(), default))).Code);
        var reviewed = await resolver.ResolveAsync(selections.Select(selection => selection with { CatalogBaseline = after.Baseline }).ToArray(), default);
        Assert.All(reviewed.Functions, function => Assert.Equal("3.0.0", function.Version));
    }

    /// <summary>候选清理同时失败时保留原阶段错误码, 并明确报告清理失败.</summary>
    [Fact]
    public async Task CleanupFailurePreservesFirstStageErrorAndExitCode()
    {
        using var router = Router(Fixture);
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        FileStream? locked = null;
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => provider.RefreshAsync(null, default, () =>
            {
                var file = Directory.GetFiles(Path.Combine(root, "udf", "catalog"), "manifest.json", SearchOption.AllDirectories).Single();
                locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                throw new AppException("UDF_TEST_FIRST_FAILURE", "提交前验证失败", 3);
            }));
            Assert.Equal("UDF_TEST_FIRST_FAILURE", error.Code);
            Assert.Equal(3, error.ExitCode);
            Assert.Contains("清理", error.Message, StringComparison.Ordinal);
            Assert.False((await provider.GetStatusAsync(default)).Initialized);
        }
        finally { locked?.Dispose(); }
    }

    /// <summary>多选移除到单选后保留的 latest 草稿仍需目录基线, 显式历史版本继续精确核验.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public async Task SingleLatestSelectionRequiresCatalogBaselineAndFreezesIt(string? version)
    {
        var newer = false;
        using var router = Router(request => (newer ? NewerFixture : InitialFixture).Respond(request));
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        var resolver = new UdfDependencyResolver(provider, new LocalUdfStore(root), router);
        foreach (var baseline in new string?[] { null, new('a', 64) })
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([
                new("daxlib", "Example.One", version, ["Example.Helper"], CatalogBaseline: baseline)], default));
            Assert.Equal("UDF_CATALOG_CHANGED", failure.Code);
        }
        var selection = new PbiUtils.Application.Udf.UdfSelection("daxlib", "Example.One", version, ["Example.Helper"], CatalogBaseline: before.Baseline);
        var frozen = await resolver.ResolveAsync([selection], default);
        Assert.Equal(before.Baseline, Assert.Single(frozen.Packages).CatalogBaseline);
        var historical = await provider.GetAsync("Example.One", "1.0.0", default);
        newer = true;
        await provider.RefreshAsync(null, default);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([selection], default))).Code);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.VerifyFrozenAsync(frozen, default))).Code);
        var exact = await resolver.ResolveAsync([new("daxlib", "Example.One", "1.0.0", ["Example.Helper"], Baseline: historical.Hash)], default);
        Assert.Equal("1.0.0", Assert.Single(exact.Packages).Version);
        Assert.Null(Assert.Single(exact.Packages).CatalogBaseline);
    }

    /// <summary>明确历史 tag 携带的浏览见证也须核验并冻结, 不允许刷新后静默沿用旧目录事实.</summary>
    [Fact]
    public async Task ExplicitVersionRetainsSuppliedCatalogWitness()
    {
        var newer = false;
        using var router = Router(request => (newer ? NewerFixture : InitialFixture).Respond(request));
        var provider = Assert.IsType<IUdfCatalogProvider>(await router.GetProviderAsync("daxlib", null, default), exactMatch: false);
        var before = await provider.RefreshAsync(null, default);
        var resolver = new UdfDependencyResolver(provider, new LocalUdfStore(root), router);
        var selection = new PbiUtils.Application.Udf.UdfSelection("daxlib", "Example.One", "1.0.0", ["Example.Helper"], CatalogBaseline: before.Baseline);
        var frozen = await resolver.ResolveAsync([selection], default);
        Assert.Equal(before.Baseline, Assert.Single(frozen.Packages).CatalogBaseline);
        newer = true;
        await provider.RefreshAsync(null, default);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([selection], default))).Code);
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.VerifyFrozenAsync(frozen, default))).Code);
        var exactWithoutWitness = await resolver.ResolveAsync([selection with { CatalogBaseline = null }], default);
        Assert.Null(Assert.Single(exactWithoutWitness.Packages).CatalogBaseline);
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        /// <summary>可控异步传输允许测试真实在途取消与跨实例刷新.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }

    /// <summary>创建传输可观测的生产来源路由.</summary>
    private UdfSourceProvider Router(Func<HttpRequestMessage, HttpResponseMessage> action) => new(root, _ => new HttpClient(new Handler(action)));

    private static readonly GitHubUdfFixture InitialFixture = GitHubUdfFixture.Catalog();
    private static readonly GitHubUdfFixture NewerFixture = GitHubUdfFixture.Catalog(newer: true);

    /// <summary>GitHub 树、全部历史版本和同提交 ZIP 一并进入正式验证链.</summary>
    private static HttpResponseMessage Fixture(HttpRequestMessage request) => InitialFixture.Respond(request);

    /// <summary>释放测试自己创建的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        /// <summary>保留真实产品协议处理, 仅替代网络传输.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
    }
}

