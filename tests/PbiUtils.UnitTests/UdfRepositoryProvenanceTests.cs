// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfRepositoryProvenanceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证仓库提交追溯独立于目录内容身份且随当前缓存发布完整核验.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Catalog;
using System.Security.Cryptography;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using System.Text.Json.Nodes;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅写独占临时目录, 不操作真实来源或模型.</summary>
public sealed class UdfRepositoryProvenanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-provenance-" + Guid.NewGuid().ToString("N"));
    private static readonly UdfSourceEntry Source = new("daxlib", "DAXLib", "https://github.com/daxlib/daxlib", null, "builtin", "git");

    /// <summary>仓库其他文件产生新commit时包内容基线不变, 激活修订与追溯事实仍更新.</summary>
    [Fact]
    public void SamePackagesDifferentCommitsKeepContentBaseline()
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Publish(store, 'a');
        string baseline; string? revision;
        using (var first = store.Read()!) { baseline = first.Baseline; revision = first.Revision; }
        Publish(store, 'b');
        using var second = store.Read()!;
        Assert.Equal(baseline, second.Baseline); Assert.NotEqual(revision, second.Revision);
        Assert.Equal(new string('b', 40), second.Repository.ResolvedCommit);
    }

    /// <summary>追溯文件被篡改时拒绝读取, 不写回或隐式修复已发布指针.</summary>
    [Fact]
    public void TamperedProvenanceCannotBecomeTrustedCurrent()
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Publish(store, 'a');
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var before = File.ReadAllBytes(pointer);
        var revision = Assert.Single(Directory.GetFiles(root, "repository.json", SearchOption.AllDirectories));
        File.AppendAllText(revision, " ");
        var error = Assert.Throws<AppException>(() => store.Read());
        Assert.Equal("UDF_CATALOG_INVALID", error.Code);
        Assert.Equal(before, File.ReadAllBytes(pointer));
    }

    /// <summary>候选的来源和ref必须与宿主登记事实一致, 不能为包内容附上其他仓库身份.</summary>
    [Fact]
    public void RejectsProvenanceFromAnotherRegisteredSource()
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Assert.Throws<AppException>(() => store.Stage([Package()],
            new("https://github.com/example/other", null, new string('a', 40)), default));
        Assert.Null(store.Read());
    }

    /// <summary>正式来源不接受无仓库追溯的候选, 也不创建能够发布的目录.</summary>
    [Fact]
    public void MissingCandidateProvenanceIsRejected()
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Assert.Equal("UDF_CATALOG_INVALID", Assert.Throws<AppException>(() => store.Stage([Package()], null!, default)).Code);
        Assert.Null(store.Read());
    }

    /// <summary>删除当前追溯摘要不能降级成旧格式成功, 失败保留指针原字节.</summary>
    [Fact]
    public async Task StrippedProvenanceCannotBypassProductionRead()
    {
        var store = new UdfCatalogSnapshotStore(root, Source); Publish(store, 'a');
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var document = JsonNode.Parse(File.ReadAllBytes(pointer))!.AsObject(); document.Remove("repositoryHash");
        File.WriteAllText(pointer, document.ToJsonString()); var before = File.ReadAllBytes(pointer);
        using var router = new UdfSourceProvider(root, _ => throw new InvalidOperationException("Unexpected network"));
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.GetStatusAsync(default))).Code);
        Assert.Equal("UDF_CATALOG_INVALID", (await Assert.ThrowsAsync<AppException>(() => router.DefaultProvider.RefreshAsync(null, default))).Code);
        Assert.Equal(before, File.ReadAllBytes(pointer));
    }

    /// <summary>即使重算追溯文件摘要, 仓库地址或ref与登记来源不一致仍不能读取.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RehashedWrongRepositoryOrReferenceRemainsInvalid(bool wrongReference)
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Publish(store, 'a');
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var repository = Assert.Single(Directory.GetFiles(root, "repository.json", SearchOption.AllDirectories));
        var revision = JsonNode.Parse(File.ReadAllBytes(repository))!.AsObject();
        revision[wrongReference ? "requestedRef" : "repositoryUrl"] = wrongReference ? "other-branch" : "https://github.com/example/other";
        File.WriteAllText(repository, revision.ToJsonString());
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(repository)));
        var document = JsonNode.Parse(File.ReadAllBytes(pointer))!.AsObject();
        document["repositoryHash"] = digest;
        File.WriteAllText(pointer, document.ToJsonString());
        var before = File.ReadAllBytes(pointer);
        Assert.Equal("UDF_CATALOG_INVALID", Assert.Throws<AppException>(() => store.Read()).Code);
        Assert.Equal(before, File.ReadAllBytes(pointer));
    }

    /// <summary>已确认提交后指针被外部移除仍不能清理已发布代, 固定读者继续读取原完整快照.</summary>
    [Fact]
    public void SuccessfulCleanupKeepsPublishedGenerationWhenPointerDisappears()
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        using var gate = store.LockRefresh();
        var candidate = store.Stage([Package()], new(Source.BaseUrl!, null, new string('a', 40)), default);
        using var published = store.Publish(candidate, null, null, default);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        File.Delete(pointer);
        store.CleanupCandidate(candidate, null);
        Assert.Single(published.ReadAll(default));
        Assert.Single(Directory.GetFiles(root, "repository.json", SearchOption.AllDirectories));
        Assert.Null(store.Read());
    }

    /// <summary>下载前记录的完整指针修订不能被同内容新commit或A-B-A内容刷新替换, 失败只清理未引用候选.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishRejectsChangedPointerEvenWhenContentReturnsToOriginal(bool aba)
    {
        var store = new UdfCatalogSnapshotStore(root, Source);
        Publish(store, 'a');
        using var original = store.Read()!;
        var candidate = store.Stage([Package()], new(Source.BaseUrl!, null, new string('c', 40)), default);
        var candidateId = JsonNode.Parse(candidate)!["generation"]!.GetValue<string>();
        Publish(store, 'b');
        if (aba) Publish(store, 'a');
        using var current = store.Read()!;
        Assert.Equal(original.Baseline, current.Baseline); Assert.NotEqual(original.Revision, current.Revision);
        var pointer = Assert.Single(Directory.GetFiles(root, "current.json", SearchOption.AllDirectories));
        var before = File.ReadAllBytes(pointer);
        var error = Assert.Throws<AppException>(() => store.Publish(candidate, original.Revision, null, default));
        Assert.Equal("UDF_CATALOG_CHANGED", error.Code);
        store.CleanupCandidate(candidate, error);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(pointer)!, candidateId)));
        Assert.Equal(before, File.ReadAllBytes(pointer));
        Assert.Equal(new string(aba ? 'a' : 'b', 40), current.Repository.ResolvedCommit);
    }

    /// <summary>在相同真实包上发布指定commit, 每代由原缓存提交边界负责激活.</summary>
    private static void Publish(UdfCatalogSnapshotStore store, char commit)
    {
        using var gate = store.LockRefresh();
        var candidate = store.Stage([Package()], new(Source.BaseUrl!, null, new string(commit, 40)), default);
        string? revision; using (var previous = store.Read()) revision = previous?.Revision;
        using var published = store.Publish(candidate, revision, null, default);
    }

    /// <summary>构造最小有效包, 包内容与提交元数据无关.</summary>
    private static UdfPackage Package() => new("Example.One", "1.0.0", new string('c', 64), "description", "",
        new Dictionary<string, string> { ["LICENSE"] = "license text" }, [],
        [new("Example.Answer", "() => 42", "answer", new Dictionary<string, string>())], DateTimeOffset.UtcNow);

    /// <summary>仅清理本测试创建的独占目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
