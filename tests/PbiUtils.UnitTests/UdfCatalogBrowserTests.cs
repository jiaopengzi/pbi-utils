// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogBrowserTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证包级摘要分页、完整元数据搜索和按需完整包读取.

using PbiUtils.Host.Bridge.Udf;
using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Catalog;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用正式快照存储和隔离目录, 不请求网络或模型.</summary>
public sealed class UdfCatalogBrowserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-browser-" + Guid.NewGuid().ToString("N"));

    /// <summary>包名、说明、作者、标签和函数均可匹配, 同包多函数命中只返回一张卡片.</summary>
    [Theory]
    [InlineData("lbo")]
    [InlineData("description-only")]
    [InlineData("Alice")]
    [InlineData("Math")]
    [InlineData("cNvN")]
    public async Task SearchReturnsWholePackageAcrossMetadataAndFunctions(string query)
    {
        Publish(Package("Lib.One", "1.0.0", "Lib.ConvertNumber", "Lib.ConvertNext") with { Metadata = new("Alice", ["Math"], "Release", null, null) });
        using var router = Router();
        var page = await router.DefaultProvider.QueryPackagesAsync(new(query, false), null, default);
        var row = Assert.Single(page.Items);
        Assert.Equal("Lib.One", row.Id); Assert.Equal(2, row.FunctionCount);
        Assert.Equal("Alice", row.Authors); Assert.Equal("Math", Assert.Single(row.Tags));
        Assert.Null(row.CommittedAt);
        Assert.Empty((await router.DefaultProvider.QueryPackagesAsync(new("<img", false), page.Status.Baseline, default)).Items);
    }

    /// <summary>包摘要按 UTF8 整体预算续传, 不重复或跳过包, 不携函数正文或 README.</summary>
    [Fact]
    public async Task LargePackagePagesRespectByteBudgetAndContinueWithoutLoss()
    {
        Publish([.. Enumerable.Range(0, 80).Select(index => Package("Lib.P" + index.ToString("D3"), "1.0.0", "Lib.F" + index) with { Description = new string('长', 400) })]);
        using var router = Router();
        var ids = new List<string>();
        int? offset = 0;
        do
        {
            var page = await router.DefaultProvider.QueryPackagesAsync(new("", false, offset!.Value, 500), null, default);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(page, JsonSerializerOptions.Web);
            Assert.True(bytes.Length <= 48 * 1024); Assert.Equal(80, page.Total); Assert.NotEmpty(page.Items);
            Assert.DoesNotContain("README BODY", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            ids.AddRange(page.Items.Select(item => item.Id)); offset = page.NextOffset;
            if (offset is not null) Assert.Equal(ids.Count, offset);
        } while (offset is not null);
        Assert.Equal(80, ids.Count); Assert.Equal(80, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>默认最新稳定, 显示预发行也不自动采用 beta; 显式 tag 按整包统一读取.</summary>
    [Fact]
    public async Task ExplicitHistoricalTagWorksWithoutSelectingBetaByDefault()
    {
        Publish(Package("Lib.One", "1.0.0", "Lib.Old"), Package("Lib.One", "2.0.0", "Lib.New"), Package("Lib.One", "3.0.0-beta", "Lib.Beta"));
        using var router = Router();
        Assert.Equal("2.0.0", Assert.Single((await router.DefaultProvider.QueryPackagesAsync(new("", true), null, default)).Items).Version);
        var page = await router.DefaultProvider.QueryPackagesAsync(new("Old", false, PackageVersions: new Dictionary<string, string> { ["Lib.One"] = "1.0.0" }), null, default);
        Assert.Equal("1.0.0", Assert.Single(page.Items).Version);
        Assert.Empty((await router.DefaultProvider.QueryPackagesAsync(new("New", false, PackageVersions: new Dictionary<string, string> { ["Lib.One"] = "1.0.0" }), null, default)).Items);
    }

    /// <summary>预发行显示只提供 beta-only 包发现候选, 不把它解释为 latest 或预发行授权.</summary>
    [Fact]
    public async Task PrereleaseVisibilityDiscoversBetaOnlyCandidate()
    {
        Publish(Package("Lib.Beta", "1.0.0-beta", "Lib.Experimental"));
        using var router = Router();
        Assert.Empty((await router.DefaultProvider.QueryPackagesAsync(new("", false), null, default)).Items);
        Assert.Equal("1.0.0-beta", Assert.Single((await router.DefaultProvider.QueryPackagesAsync(new("", true), null, default)).Items).Version);
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.QueryPackagesAsync(new("", false, PackageVersions: new Dictionary<string, string> { ["Lib.Beta"] = "1.0.0-beta" }), null, default));
    }

    /// <summary>摘要浏览不读取无关包体, 精确包详情仍对目标包做内容摘要核验并包含全部函数.</summary>
    [Fact]
    public async Task SummaryDoesNotReadUnrelatedPackageBodies()
    {
        Publish(Package("Lib.One", "1.0.0", "Lib.Alpha", "Lib.Another"), Package("Lib.Two", "1.0.0", "Lib.Beta"));
        var body = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Single(path => Path.GetFileName(path).Length == 69 && File.ReadAllText(path).Contains("\"id\":\"Lib.Two\"", StringComparison.Ordinal));
        File.WriteAllText(body, "damaged");
        using var router = Router();
        var page = await router.DefaultProvider.QueryPackagesAsync(new("Alpha", false), null, default); Assert.Single(page.Items);
        var bridge = new UdfCatalogBridge(router.DefaultProvider, null);
        var detail = JsonSerializer.SerializeToElement(await bridge.ReadAsync(JsonSerializer.SerializeToElement(new
        { action = "package", packageId = "Lib.One", version = "1.0.0", catalogBaseline = page.Status.Baseline }), default), JsonSerializerOptions.Web);
        Assert.Equal(2, detail.GetProperty("package").GetProperty("functions").GetArrayLength());
        await Assert.ThrowsAsync<AppException>(() => bridge.ReadAsync(JsonSerializer.SerializeToElement(new { action = "status" }), default));
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetAsync("Lib.Two", "1.0.0", default));
    }

    /// <summary>包图标按宿主身份读取, 过期目录基线和伪造图标不可复用.</summary>
    [Fact]
    public async Task IconsAndCatalogWitnessAreExact()
    {
        Publish(Package("Lib.One", "1.0.0", "Lib.Alpha", "Lib.Beta") with { Metadata = new("Author", ["Math"], "Release", null, null, "data:image/png;base64,AA==") });
        using var router = Router();
        var page = await router.DefaultProvider.QueryPackagesAsync(new("", false), null, default);
        var package = Assert.Single(page.Items); Assert.NotNull(package.IconKey);
        var icon = await router.DefaultProvider.GetIconAsync("Lib.One", "1.0.0", package.IconKey!, page.Status.Baseline, default);
        Assert.Equal("data:image/png;base64,AA==", icon.DataUrl);
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetIconAsync("Lib.One", "1.0.0", new string('f', 64), page.Status.Baseline, default));
        var changed = await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.QueryPackagesAsync(new("", false), new string('f', 64), default));
        Assert.Equal("UDF_CATALOG_CHANGED", changed.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DefaultProvider.QueryPackagesAsync(new("", false), page.Status.Baseline, new(true)));
    }

    /// <summary>错误页界限不能通过取整或截断变成成功.</summary>
    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    [InlineData(1025, 50)]
    public async Task InvalidPageIsRejected(int offset, int limit)
    {
        Publish(Package("Lib.One", "1.0.0", "Lib.One")); using var router = Router();
        await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.QueryPackagesAsync(new("", false, offset, limit), null, default));
    }

    /// <summary>直接通过正式stage/publish构造已验证的本地目录, 不替换产品分页逻辑.</summary>
    private void Publish(params UdfPackage[] packages)
    {
        var store = new UdfCatalogSnapshotStore(root, UdfSourceDefaults.Official);
        using var gate = store.LockRefresh();
        var candidate = store.Stage(packages, new(UdfSourceDefaults.Official.BaseUrl!, null, new string('a', 40)), default);
        string? revision; using (var previous = store.Read()) revision = previous?.Revision;
        using var published = store.Publish(candidate, revision, null, default);
    }
    /// <summary>严格禁止只读目录请求创建网络客户端.</summary>
    private UdfSourceProvider Router() => new(root, _ => throw new InvalidOperationException("Unexpected network client"));
    /// <summary>完整包体包含标记说明和函数表达式, 可验证摘要未携带它们.</summary>
    private static UdfPackage Package(string id, string version, params string[] names) => new(id, version, new string('a', 64), "description-only", "README BODY", new Dictionary<string, string> { ["LICENSE"] = "License text" }, [],
        names.Select(name => new UdfFunction(name, "() => 1", "description-only", new Dictionary<string, string>())).ToArray(), DateTimeOffset.UtcNow);
    /// <summary>只删除本测试拥有的隔离根目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
