// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfFunctionBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证包级分页、图标及缓存操作拒绝的桥接分界和严格参数, 不读取真实用户数据.

using PbiUtils.Host.Bridge.Udf;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>新浏览动作只委派对应能力, 不先遍历完整状态或隐式刷新.</summary>
public sealed class UdfFunctionBridgeTests
{
    /// <summary>500是合法显示上限, remaining短页可用1, 明确tag/witness完整传入provider.</summary>
    [Fact]
    public async Task PackagePagesUseDedicatedBrowserWithoutReadingWholeCatalog()
    {
        var provider = new Catalog();
        var bridge = new UdfCatalogBridge(provider, null);
        var result = JsonSerializer.SerializeToElement(await bridge.ReadAsync(Input(new
        {
            action = "packages",
            query = "oef",
            includePrerelease = true,
            limit = 500,
            offset = 50,
            packageVersions = new Dictionary<string, string> { ["Example.One"] = "1.0.0" },
            catalogBaseline = Catalog.Baseline
        }), default), JsonSerializerOptions.Web);

        Assert.Equal("Example.One", result.GetProperty("packagePage").GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(500, provider.Query!.Limit);
        Assert.Equal(50, provider.Query.Offset);
        Assert.Equal("1.0.0", provider.Query.PackageVersions!["Example.One"]);
        Assert.Equal(Catalog.Baseline, provider.ExpectedBaseline);
        await bridge.ReadAsync(Input(new { action = "packages", limit = 1 }), default);
        Assert.Equal(1, provider.Query.Limit);
        Assert.Equal(0, provider.StatusReads);
        Assert.Equal(0, provider.Refreshes);
    }

    /// <summary>新动作专属字段不能串用, 分页大小/版本映射/图标身份均由宿主先校验.</summary>
    [Theory]
    [InlineData("{\"action\":\"packages\",\"limit\":501}")]
    [InlineData("{\"action\":\"packages\",\"offset\":-1}")]
    [InlineData("{\"action\":\"packages\",\"offset\":1025}")]
    [InlineData("{\"action\":\"function\",\"packageId\":\"Example.One\",\"version\":\"1.0.0\",\"functionName\":\"Name\\nControl\"}")]
    [InlineData("{\"action\":\"packages\",\"limit\":1.5}")]
    [InlineData("{\"action\":\"packages\",\"includePrerelease\":\"true\"}")]
    [InlineData("{\"action\":\"packages\",\"packageVersions\":null}")]
    [InlineData("{\"action\":\"packages\",\"packageVersions\":{\"Example.One\":\"1.0.0\",\"example.one\":\"2.0.0\"}}")]
    [InlineData("{\"action\":\"packages\",\"version\":\"1.0.0\"}")]
    [InlineData("{\"action\":\"history\"}")]
    [InlineData("{\"action\":\"icon\",\"packageId\":\"Example.One\",\"version\":\"1.0.0\",\"iconKey\":\"https://untrusted.example/icon.png\"}")]
    public async Task InvalidPackageActionParametersAreRejected(string json)
    {
        var provider = new Catalog();
        var error = await Assert.ThrowsAsync<AppException>(() => new UdfCatalogBridge(provider, null).ReadAsync(JsonDocument.Parse(json).RootElement, default));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.Null(provider.Query);
        Assert.Equal(0, provider.Refreshes);
    }

    /// <summary>构造与网页相同的JSON字段命名, 不经过宽松参数转换.</summary>
    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>已移除的函数级动作不得作为别名继续访问.</summary>
    [Theory]
    [InlineData("functions")]
    [InlineData("function")]
    public async Task RemovedFunctionActionsAreRejected(string action)
    {
        var provider = new Catalog();
        await Assert.ThrowsAsync<AppException>(() => new UdfCatalogBridge(provider, null).ReadAsync(Input(new { action }), default));
        Assert.Null(provider.Query);
    }

    /// <summary>只观察实际委派方法, 不拥有网络、磁盘或模型.</summary>
    private sealed class Catalog : IUdfCatalogProvider
    {
        internal static readonly string Baseline = new('a', 64);
        internal UdfPackageQuery? Query { get; private set; }
        internal string? ExpectedBaseline { get; private set; }
        internal int StatusReads { get; private set; }
        internal int Refreshes { get; private set; }
        private static UdfCatalogStatus Status => new(true, Baseline, DateTimeOffset.UnixEpoch, 1, 2, 1);
        public Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken)
        {
            Query = query; ExpectedBaseline = expectedBaseline;
            return Task.FromResult(new UdfPackagePage([new("Example.One", "1.0.0", Baseline, "Description", "Alice", ["Math"], null, null, 2)], 1, null, Status));
        }
        public Task<UdfCatalogStatus> GetStatusAsync(CancellationToken cancellationToken) { StatusReads++; return Task.FromResult(Status); }
        public Task<UdfCatalogStatus> RefreshAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken, Action? committing = null) { Refreshes++; return Task.FromResult(Status); }
        public Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
