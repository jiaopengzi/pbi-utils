// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogCurrentCacheTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证最新完整缓存、严格当前指针、原子发布与读者资源保护.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Catalog;
using System.Text.Json.Nodes;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有写入局限于独占临时目录, 不修改用户函数或模型.</summary>
public sealed class UdfCatalogCurrentCacheTests : IDisposable
{
    private static readonly string[] expectedLatestVersions = ["1.0.0", "2.0.0", "3.0.0-beta"];
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-current-" + Guid.NewGuid().ToString("N"));

    /// <summary>新发布指针只包含当前代, 当前完整目录仍保留所有包版本且不影响本地文件.</summary>
    [Fact]
    public async Task LatestCacheContainsAllVersionsAndRemovesObsoletePublishedGenerations()
    {
        Publish("0.9.0");
        var local = Path.Combine(root, "udf", "local", "user-owned.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!); File.WriteAllText(local, "unchanged");
        Publish("1.0.0", "2.0.0", "3.0.0-beta");
        using var router = Router();
        var versions = await router.DefaultProvider.GetVersionsAsync("Lib.One", default);
        Assert.Equal(expectedLatestVersions, versions);
        Assert.Equal("1.0.0", (await router.DefaultProvider.GetAsync("Lib.One", "1.0.0", default)).Version);
        var head = JsonNode.Parse(File.ReadAllBytes(Pointer()))!.AsObject();
        Assert.Equal(3, head["schemaVersion"]!.GetValue<int>()); Assert.False(head.ContainsKey("history"));
        Assert.Single(Generations()); Assert.Equal("unchanged", File.ReadAllText(local));
    }

    /// <summary>旧发布 schema2 即使带完整历史也拒绝使用, 原指针及文件保持不变.</summary>
    [Fact]
    public void PreviousPublishedSchemaIsRejectedWithoutRewritingOrRetiringFiles()
    {
        Publish("1.0.0");
        using var snapshot = Store().Read()!;
        var path = Pointer(); var head = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        head["schemaVersion"] = 2;
        head["history"] = new JsonArray(new JsonObject
        {
            ["generation"] = head["generation"]!.DeepClone(),
            ["baseline"] = head["baseline"]!.DeepClone(),
            ["manifestHash"] = head["manifestHash"]!.DeepClone(),
            ["retrievedAt"] = head["retrievedAt"]!.DeepClone(),
            ["repositoryHash"] = head["repositoryHash"]!.DeepClone(),
            ["packageCount"] = 1,
            ["versionCount"] = 1,
            ["functionCount"] = 1
        });
        File.WriteAllText(path, head.ToJsonString()); var original = File.ReadAllBytes(path);
        Assert.Equal("UDF_CATALOG_INVALID", Assert.Throws<AppException>(() => Store().Read()).Code);
        Assert.Equal("UDF_CATALOG_INVALID", Assert.Throws<AppException>(() => Publish("2.0.0")).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Generations());
    }

    /// <summary>未知指针、旧发布 schema、候选、未知字段或旧索引均拒绝读取/刷新, 保留原始字节.</summary>
    [Theory]
    [InlineData("pointer")]
    [InlineData("manifest")]
    [InlineData("schema2")]
    [InlineData("candidate")]
    [InlineData("history")]
    public async Task InvalidCacheMetadataIsNeverRewrittenOrRecovered(string damage)
    {
        Publish("1.0.0"); var pointer = Pointer(); var head = JsonNode.Parse(File.ReadAllBytes(pointer))!.AsObject();
        var manifest = Path.Combine(Path.GetDirectoryName(pointer)!, head["generation"]!.GetValue<string>(), "manifest.json");
        if (damage == "manifest")
        {
            var index = JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject(); index["schemaVersion"] = 1;
            File.WriteAllText(manifest, index.ToJsonString());
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifest)));
            head["baseline"] = hash; head["manifestHash"] = hash;
        }
        else if (damage == "schema2") head["schemaVersion"] = 2;
        else if (damage == "candidate") { head["schemaVersion"] = 1; head.Remove("activationId"); }
        else if (damage == "history") head["history"] = new JsonArray();
        else head["schemaVersion"] = 999;
        File.WriteAllText(pointer, head.ToJsonString());
        var originalPointer = File.ReadAllBytes(pointer); var originalManifest = File.ReadAllBytes(manifest);
        using var router = Router();
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetStatusAsync(default))).Code);
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default))).Code);
        Assert.Equal(originalPointer, File.ReadAllBytes(pointer)); Assert.Equal(originalManifest, File.ReadAllBytes(manifest));
        Assert.Single(Generations());
    }

    /// <summary>同内容刷新仍更换修订, 陈旧发布不得覆盖新指针, 仅清理自身候选.</summary>
    [Fact]
    public void SameContentRefreshCannotReuseRevision()
    {
        Publish("1.0.0");
        string baseline; string? revision;
        using (var first = Store().Read()!) { baseline = first.Baseline; revision = first.Revision; }
        Publish("1.0.0");
        var store = Store(); using var gate = store.LockRefresh(); using var current = store.Read()!;
        Assert.Equal(baseline, current.Baseline); Assert.NotEqual(revision, current.Revision);
        var candidate = store.Stage([Package("2.0.0")], Repository(), default);
        var error = Assert.Throws<AppException>(() => store.Publish(candidate, revision, null, default));
        Assert.Equal("UDF_CATALOG_CHANGED", error.Code); store.CleanupCandidate(candidate, error);
        Assert.Single(Generations());
    }

    /// <summary>提交前取消保留旧代, committing 中取消后仍排空并发布实际新代.</summary>
    [Fact]
    public void CancellationHonorsAtomicPublishBoundary()
    {
        Publish("1.0.0"); var store = Store(); using var gate = store.LockRefresh();
        string? revision; using (var original = store.Read()!) revision = original.Revision;
        var candidate = store.Stage([Package("2.0.0")], Repository(), default);
        var error = Assert.ThrowsAny<OperationCanceledException>(() => store.Publish(candidate, revision, null, new(true)));
        store.CleanupCandidate(candidate, error); Assert.Single(Generations());
        using (var unchanged = store.Read()!) Assert.Equal(revision, unchanged.Revision);
        var next = store.Stage([Package("3.0.0")], Repository(), default); using var during = new CancellationTokenSource();
        using var published = store.Publish(next, revision, during.Cancel, during.Token);
        Assert.True(during.IsCancellationRequested); Assert.Equal("3.0.0", Assert.Single(published.Entries).Version);
        Assert.Single(Generations());
    }

    /// <summary>已固定读者阻止旧代删除, 后续成功发布才回收已释放旧代; 未发布候选不被扫描清理.</summary>
    [Fact]
    public async Task ActiveReaderKeepsOldFilesUntilReleasedAndNextConfirmedPublish()
    {
        Publish("1.0.0"); using var router = Router();
        var reader = await ((IUdfCatalogSnapshotSource)router.DefaultProvider).OpenSnapshotAsync(default);
        try
        {
            Publish("2.0.0"); Assert.Equal(2, Generations().Length);
            Assert.Equal("1.0.0", (await reader.GetAsync("Lib.One", "1.0.0", default)).Version);
        }
        finally { reader.Dispose(); }
        var orphan = Store().Stage([Package("9.0.0")], Repository(), default);
        var orphanId = JsonNode.Parse(orphan)!["generation"]!.GetValue<string>();
        Publish("3.0.0"); Assert.Equal(2, Generations().Length);
        Assert.Contains(Generations(), path => Path.GetFileName(path) == orphanId);
    }

    /// <summary>提交后回读失败属于未知提交, 原当前和候选都保留且不自动恢复.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfirmedCommitNeverCleansOldOrCandidate(bool pointerDisappears)
    {
        Publish("1.0.0"); var store = Store(); using var gate = store.LockRefresh();
        string? revision; using (var original = store.Read()!) revision = original.Revision;
        var candidate = store.Stage([Package("2.0.0")], Repository(), default);
        var generation = JsonNode.Parse(candidate)!["generation"]!.GetValue<string>();
        var path = Path.Combine(Path.GetDirectoryName(Pointer())!, generation);
        var error = Assert.Throws<AppException>(() => store.Publish(candidate, revision,
            () => File.WriteAllText(Path.Combine(path, "manifest.json"), "damaged"), default));
        Assert.Equal("UDF_CATALOG_UNCONFIRMED", error.Code);
        if (pointerDisappears) File.Delete(Pointer());
        store.CleanupCandidate(candidate, error); Assert.Equal(2, Generations().Length);
    }

    /// <summary>发布确认后的清理错误不能使已提交代重新被当作失败候选删除, 即使指针随后消失.</summary>
    [Fact]
    public void CleanupFailurePreservesConfirmedCandidateAndReportsCommittedFact()
    {
        Publish("1.0.0"); var store = Store(); using var gate = store.LockRefresh();
        var oldPath = Assert.Single(Generations()); Directory.CreateDirectory(Path.Combine(oldPath, "unexpected-directory"));
        string? revision; using (var previous = store.Read()) revision = previous!.Revision;
        var candidate = store.Stage([Package("2.0.0")], Repository(), default);
        var candidateId = JsonNode.Parse(candidate)!["generation"]!.GetValue<string>();
        var error = Assert.Throws<AppException>(() => store.Publish(candidate, revision, null, default));
        Assert.Equal("UDF_CATALOG_CLEANUP_FAILED", error.Code); Assert.Contains("缓存已更新", error.Message, StringComparison.Ordinal);
        File.Delete(Pointer()); store.CleanupCandidate(candidate, error);
        Assert.Contains(Generations(), path => Path.GetFileName(path) == candidateId);
        Assert.True(Directory.Exists(Path.Combine(oldPath, "unexpected-directory")));
    }

    /// <summary>发布仅在独占锁内进行; 测试释放所有固定读者后检查磁盘回收.</summary>
    private void Publish(params string[] versions)
    {
        var store = Store(); using var gate = store.LockRefresh();
        string? revision; using (var previous = store.Read()) revision = previous?.Revision;
        using var published = store.Publish(store.Stage(versions.Select(Package).ToArray(), Repository(), default), revision, null, default);
    }
    /// <summary>与正式内置来源一致的隔离存储身份.</summary>
    private UdfCatalogSnapshotStore Store() => new(root, UdfSourceDefaults.Official);
    /// <summary>返回当前指针的唯一真实位置.</summary>
    private string Pointer() => Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
    /// <summary>仅枚举正式代目录, 不把来源根或本地用户文件当作缓存.</summary>
    private string[] Generations() => [.. Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))];
    /// <summary>明确绑定受控测试仓库提交.</summary>
    private static UdfRepositoryRevision Repository() => new(UdfSourceDefaults.Official.BaseUrl!, null, new string('a', 40));
    /// <summary>只读缓存调用不创建网络客户端.</summary>
    private UdfSourceProvider Router() => new(root, _ => throw new InvalidOperationException("Unexpected network"));
    /// <summary>身份、函数和许可完整且没有环境依赖的包定义.</summary>
    private static UdfPackage Package(string version) => new("Lib.One", version, new string('a', 64), "", "", new Dictionary<string, string>(), [], [new("Lib.One", "() => 1", "", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch);
    /// <summary>只清理本测试拥有的隔离目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
