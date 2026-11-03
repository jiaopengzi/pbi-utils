// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证目录状态、显式更新与严格请求分界, 不访问网络或真实用户数据.

using PbiUtils.Host.Bridge.Udf;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class UdfCatalogBridgeTests
{
    /// <summary>首次状态不触发更新, 更新仅通过独立显式动作执行并传递提交边界.</summary>
    [Fact]
    public async Task StatusAndReadsNeverRefreshAndExplicitRefreshHasStrictFields()
    {
        var provider = new Catalog();
        var catalog = new UdfCatalogBridge(provider, null);
        var status = JsonSerializer.SerializeToElement(await catalog.ReadAsync(Input(new { action = "status" }), CancellationToken.None), JsonSerializerOptions.Web);
        Assert.False(status.GetProperty("status").GetProperty("initialized").GetBoolean());
        Assert.Equal(0, provider.Refreshes);
        var committed = false;
        var updated = JsonSerializer.SerializeToElement(await catalog.RefreshAsync(Input(new { }), null, CancellationToken.None, () => committed = true), JsonSerializerOptions.Web);
        Assert.True(committed);
        Assert.True(updated.GetProperty("initialized").GetBoolean());
        await catalog.ReadAsync(Input(new { action = "search", query = "" }), CancellationToken.None);
        Assert.Equal(1, provider.Refreshes);
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => catalog.ReadAsync(Input(new { action = "status", query = "forbidden" }), CancellationToken.None))).Code);
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => catalog.RefreshAsync(Input(new { url = "https://unbound.example.org/" }), null, CancellationToken.None))).Code);
    }

    /// <summary>测试参数使用真实 Web JSON 命名, 不放宽产品字段白名单.</summary>
    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>分页不能把大量多字节摘要塞入单条信封, 下一页无重复且保留完整总数.</summary>
    [Fact]
    public async Task SearchPagesAreBoundedByUtf8BytesAndValidateSnapshotWitness()
    {
        var provider = new Catalog { Items = Enumerable.Range(0, 120).Select(index => new UdfPackageSummary("Library" + index, "1.0.0", new string('中', 1000))).ToArray() };
        var catalog = new UdfCatalogBridge(provider, null);
        await catalog.RefreshAsync(Input(new { }), null, CancellationToken.None);
        var first = JsonSerializer.SerializeToElement(await catalog.ReadAsync(Input(new { action = "search", limit = 100, catalogBaseline = new string('a', 64) }), CancellationToken.None), JsonSerializerOptions.Web);
        Assert.Equal(120, first.GetProperty("total").GetInt32());
        var count = first.GetProperty("packages").GetArrayLength();
        Assert.InRange(count, 1, 99);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first).Length < 64 * 1024);
        var nextOffset = first.GetProperty("nextOffset").GetInt32();
        Assert.Equal(count, nextOffset);
        var next = JsonSerializer.SerializeToElement(await catalog.ReadAsync(Input(new { action = "search", offset = nextOffset }), CancellationToken.None), JsonSerializerOptions.Web);
        Assert.Equal("Library" + count, next.GetProperty("packages")[0].GetProperty("id").GetString());
        Assert.Equal("UDF_CATALOG_CHANGED", (await Assert.ThrowsAsync<AppException>(() => catalog.ReadAsync(Input(new { action = "search", catalogBaseline = new string('b', 64) }), CancellationToken.None))).Code);
        await Assert.ThrowsAsync<AppException>(() => catalog.ReadAsync(Input(new { action = "search", limit = 101 }), CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => catalog.ReadAsync(Input(new { action = "search", offset = -1 }), CancellationToken.None));
    }

    /// <summary>可观察刷新次数的本地目录替身, 不承担 HTTP 或模型提交能力.</summary>
    private sealed class Catalog : IUdfCatalogProvider
    {
        public Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken) => throw new NotSupportedException();
        internal int Refreshes { get; private set; }
        internal IReadOnlyList<UdfPackageSummary> Items { get; init; } = [];
        public Task<UdfCatalogStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new UdfCatalogStatus(Refreshes > 0, Refreshes > 0 ? new string('a', 64) : null, Refreshes > 0 ? DateTimeOffset.UnixEpoch : null, 0, 0, 0));
        public async Task<UdfCatalogStatus> RefreshAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken, Action? committing = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            committing?.Invoke();
            Refreshes++;
            return await GetStatusAsync(CancellationToken.None);
        }
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => Task.FromResult(Items);
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => throw new AppException("UDF_PACKAGE_MISSING", "测试目录为空", 3);
    }
}
