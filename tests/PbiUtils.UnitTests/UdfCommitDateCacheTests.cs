// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCommitDateCacheTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证精确版本 Git 提交日期的缓存隔离、UTC 规范化与索引正文一致性.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Catalog;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用独占临时目录和正式快照存储, 不访问远端或用户数据.</summary>
public sealed class UdfCommitDateCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-commit-dates-" + Guid.NewGuid().ToString("N"));

    /// <summary>不同 tag 的提交日期及未知状态独立保存, 精确详情和摘要相同且读取不联网.</summary>
    [Fact]
    public async Task ExactVersionCommitDatesRemainUtcAndUnknownWithoutNetwork()
    {
        var timestamp = DateTimeOffset.Parse("2026-01-01T08:00:00+08:00", CultureInfo.InvariantCulture);
        Publish(Package("1.0.0") with { CommittedAt = timestamp }, Package("2.0.0"));
        using var router = Router();
        var provider = router.DefaultProvider;
        var old = await provider.GetAsync("Example.One", "1.0.0", default);
        var current = await provider.GetAsync("Example.One", "2.0.0", default);
        Assert.Equal(timestamp.ToUniversalTime(), old.CommittedAt);
        Assert.Equal(TimeSpan.Zero, old.CommittedAt!.Value.Offset);
        Assert.Null(current.CommittedAt);
        Assert.Equal(new string('a', 64), old.Hash);
        var page = await provider.QueryPackagesAsync(new("", false, PackageVersions: new Dictionary<string, string> { ["Example.One"] = "1.0.0" }), null, default);
        Assert.Equal(old.CommittedAt, Assert.Single(page.Items).CommittedAt);
        Assert.Null(Assert.Single((await provider.QueryPackagesAsync(new("", false), page.Status.Baseline, default)).Items).CommittedAt);
    }

    /// <summary>日期必须在索引和包体一致且字段存在; 外部同步改写索引摘要也不能掩盖损坏.</summary>
    [Theory]
    [InlineData("different")]
    [InlineData("missing")]
    [InlineData("non-utc")]
    public async Task CachedPackageAndIndexMustHaveSameCanonicalCommitDate(string damage)
    {
        Publish(Package("1.0.0") with { CommittedAt = DateTimeOffset.Parse("2026-01-01T08:00:00+08:00", CultureInfo.InvariantCulture) });
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var head = JsonNode.Parse(File.ReadAllBytes(pointer))!.AsObject();
        var path = Path.Combine(Path.GetDirectoryName(pointer)!, head["generation"]!.GetValue<string>(), "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        var entry = manifest["packages"]![0]!.AsObject();
        if (damage == "missing") entry.Remove("committedAt");
        else entry["committedAt"] = damage == "non-utc" ? "2026-01-01T08:00:00+08:00" : "2025-01-01T00:00:00+00:00";
        File.WriteAllText(path, manifest.ToJsonString());
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        head["manifestHash"] = hash; head["baseline"] = hash; File.WriteAllText(pointer, head.ToJsonString());
        using var router = Router();
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetAsync("Example.One", "1.0.0", default))).Code);
    }

    /// <summary>调用正式发布路径建立完整已核验缓存, 不用伪造索引代替正常准备过程.</summary>
    private void Publish(params UdfPackage[] packages)
    {
        var store = new UdfCatalogSnapshotStore(root, UdfSourceDefaults.Official);
        using var gate = store.LockRefresh();
        var candidate = store.Stage(packages, new(UdfSourceDefaults.Official.BaseUrl!, null, new string('a', 40)), default);
        using var published = store.Publish(candidate, null, null, default);
    }

    /// <summary>缓存只读阶段若意外请求网络立即失败.</summary>
    private UdfSourceProvider Router() => new(root, _ => throw new InvalidOperationException("Unexpected network"));

    /// <summary>构造含相同执行内容哈希的精确版本, 提交元数据不进入函数身份.</summary>
    private static UdfPackage Package(string version) => new("Example.One", version, new string('a', 64), "Example", "# Example",
        new Dictionary<string, string>(), [], [new("Example.Helper", "() => 1", "helper", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch);

    /// <summary>释放测试拥有的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
