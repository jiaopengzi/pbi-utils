// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\RecentProjectFileTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证最近 PBIP/PBIX 文件类型、身份绑定、锁竞争与损坏历史保护.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Connections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>最近文件测试使用独立临时目录, 不打开真实 Desktop 或读取用户历史.</summary>
public sealed class RecentProjectFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-recent-files-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>损坏文件中即使身份和名称均匹配, 非白名单类型也拒绝读取和覆盖.</summary>
    /// <param name="suffix">不受支持的文件后缀.</param>
    [Theory]
    [InlineData(".pbit")]
    [InlineData(".json")]
    [InlineData(".pbix.exe")]
    [InlineData("")]
    public void UnsupportedStoredFileIsPreserved(string suffix)
    {
        var store = new LocalRecentProjects(root);
        store.Record(Path.Combine(root, "supported.pbip"));
        var file = Path.Combine(root, "recent-projects.json");
        var invalidPath = Path.Combine(root, "unsupported" + suffix);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(invalidPath.ToUpperInvariant())));
        var entry = new RecentProject(identity, invalidPath, Path.GetFileName(invalidPath), DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { entry }, Json);
        File.WriteAllBytes(file, bytes);
        Assert.Equal("INVALID_RECENT_PROJECTS", Assert.Throws<AppException>(() => store.Load()).Code);
        Assert.Equal("INVALID_RECENT_PROJECTS", Assert.Throws<AppException>(() => store.Record(Path.Combine(root, "next.pbix"))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    /// <summary>同一身份出现多次视为损坏历史, 保存新的文件也不会覆盖原内容.</summary>
    [Fact]
    public void DuplicateStoredIdentityIsPreserved()
    {
        var store = new LocalRecentProjects(root);
        store.Record(Path.Combine(root, "same.pbix"));
        var entry = Assert.Single(store.Load());
        var file = Path.Combine(root, "recent-projects.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { entry, entry }, Json);
        File.WriteAllBytes(file, bytes);
        Assert.Equal("INVALID_RECENT_PROJECTS", Assert.Throws<AppException>(() => store.Load()).Code);
        Assert.Equal("INVALID_RECENT_PROJECTS", Assert.Throws<AppException>(() => store.Record(Path.Combine(root, "next.pbip"))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    /// <summary>旧版 PBIP 历史继续可读, PBIX 路径的大小写和分隔变化均复用同一记录, 锁竞争不覆盖.</summary>
    [Fact]
    public void FileHistoryMergesBothTypesAndPreservesConcurrentBaseline()
    {
        var store = new LocalRecentProjects(root);
        var legacy = Path.Combine(root, "legacy.pbip");
        store.Record(legacy);
        var legacyId = Assert.Single(store.Load()).Id;
        var pbix = Path.Combine(root, "new.PBIX");
        store.Record(pbix);
        Assert.Equal(legacyId, store.Load().Single(item => item.Path == legacy).Id);
        var firstId = store.Load()[0].Id;
        new LocalRecentProjects(root).Record(pbix.ToUpperInvariant().Replace('\\', '/'));
        Assert.Equal(firstId, store.Load()[0].Id);
        Assert.Equal(2, store.Load().Count);
        var file = Path.Combine(root, "recent-projects.json");
        var bytes = File.ReadAllBytes(file);
        using (UserDataStore.Lock(file + ".lock"))
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => store.Record(Path.Combine(root, "third.pbix"))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>删除按绑定身份合并最新历史, 已丢失的文件也能移除, 保留另一个实例新增的记录和真实模型文件.</summary>
    [Fact]
    public void DeleteUsesBoundIdentityAndLatestHistoryWithoutDeletingModels()
    {
        var store = new LocalRecentProjects(root); var model = Path.Combine(root, "keep.pbix");
        Directory.CreateDirectory(root); File.WriteAllText(model, "model-file");
        store.Record(model); store.Record(Path.Combine(root, "missing.pbip"));
        var missingId = store.Load()[0].Id;
        new LocalRecentProjects(root).Record(Path.Combine(root, "newer.pbix"));
        Assert.True(store.Delete(missingId, default));
        Assert.Equal(2, store.Load().Count); Assert.Equal("model-file", File.ReadAllText(model));
        var file = Path.Combine(root, "recent-projects.json"); var bytes = File.ReadAllBytes(file);
        Assert.False(store.Delete(missingId, default)); Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal("RECENT_PROJECT_NOT_FOUND", Assert.Throws<AppException>(() => store.Resolve(missingId)).Code);
        Assert.True(store.Delete(store.Load().Single(item => item.Path == model).Id, default));
        Assert.Equal("model-file", File.ReadAllText(model)); Assert.Single(store.Load());
    }

    /// <summary>取消、锁冲突和损坏历史不触发提交; 提交已经开始后的取消保留实际删除结果.</summary>
    [Fact]
    public void DeleteHonorsCommitBoundaryAndPreservesInvalidOrBusyHistory()
    {
        var store = new LocalRecentProjects(root); store.Record(Path.Combine(root, "keep.pbip"));
        var id = Assert.Single(store.Load()).Id; var file = Path.Combine(root, "recent-projects.json"); var bytes = File.ReadAllBytes(file);
        Assert.ThrowsAny<OperationCanceledException>(() => store.Delete(id, new(true)));
        using (UserDataStore.Lock(file + ".lock"))
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => store.Delete(id, default)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        File.WriteAllText(file, "{broken");
        Assert.Equal("INVALID_RECENT_PROJECTS", Assert.Throws<AppException>(() => store.Delete(id, default)).Code);
        Assert.Equal("{broken", File.ReadAllText(file)); File.WriteAllBytes(file, bytes);
        Assert.ThrowsAny<OperationCanceledException>(() => store.Delete(id, default, () => throw new OperationCanceledException()));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        using var cancelAfterCommit = new CancellationTokenSource();
        Assert.True(store.Delete(id, cancelAfterCommit.Token, cancelAfterCommit.Cancel));
        Assert.Empty(store.Load()); Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>网页不能用任意路径、展示名、空值或非规范身份删除最近记录.</summary>
    /// <param name="id">不受支持的身份文本.</param>
    [Theory]
    [InlineData("")]
    [InlineData("report.pbip")]
    [InlineData("C:\\private\\report.pbix")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void DeleteRejectsNonCanonicalIdentity(string id)
    {
        var store = new LocalRecentProjects(root);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => store.Delete(id, default)).Code);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>只清理已核验的本次临时目录, 不访问真实用户配置.</summary>
    public void Dispose()
    {
        UserDataStore.Check(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
