// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LocalUdfTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证本地函数稳定身份、基线和损坏保护.

using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>隔离临时目录中的真实文件回归.</summary>
public sealed class LocalUdfTests
{
    /// <summary>函数名不区分大小写, 精确 tag 的预发行大小写与构建元数据仍是不同版本身份.</summary>
    [Fact]
    public async Task ExactVersionTagsRemainOrdinal()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            foreach (var tag in new[] { "1.0.0-beta", "1.0.0-BETA", "1.0.0+build", "1.0.0+BUILD" })
                await store.CreateAsync(new(1, "Local.One", tag, "() => 1", "", []), default);
            Assert.Equal(4, (await store.ListAsync(default)).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>外部产生同名同 tag 的重复记录时不能任意挑选 GUID, 列表与后续写入均失败并保留记录.</summary>
    [Fact]
    public async Task ExistingDuplicateTagsAreNotSilentlyCollapsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var first = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var second = await store.CreateAsync(first.Definition with { Version = "2.0.0" }, default);
            var path = Path.Combine(root, "udf", "local", second.Id + ".json");
            var duplicate = (await File.ReadAllTextAsync(path)).Replace("2.0.0", "1.0.0", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, duplicate);
            Assert.Equal("UDF_STORE_INVALID", (await Assert.ThrowsAsync<AppException>(() => store.ListAsync(default))).Code);
            await Assert.ThrowsAsync<AppException>(() => store.CreateAsync(first.Definition with { Version = "3.0.0" }, default));
            Assert.Equal(duplicate, await File.ReadAllTextAsync(path));
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>同名不同版本拥有不同稳定 ID, 同名大小写变体的同精确版本不能重复创建.</summary>
    [Fact]
    public async Task SameNameVersionsAreDistinctAndDuplicateTagIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var first = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var second = await store.CreateAsync(first.Definition with { Version = "2.0.0" }, default);
            var failure = await Assert.ThrowsAsync<AppException>(() => store.CreateAsync(first.Definition with { Name = "LOCAL.one", Expression = "() => 9" }, default));
            Assert.Equal("UDF_LOCAL_VERSION_EXISTS", failure.Code);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(first.Baseline, (await store.GetAsync(first.Id, default)).Baseline);
            Assert.Equal(2, (await store.ListAsync(default)).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>更新版本或重命名遇到其他同名同 tag 时保持两个原记录, 自己保持同 tag 的编辑仍允许.</summary>
    [Fact]
    public async Task UpdateCannotCollideWithAnotherExactLocalVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var first = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var second = await store.CreateAsync(first.Definition with { Version = "2.0.0" }, default);
            var failure = await Assert.ThrowsAsync<AppException>(() => store.UpdateAsync(second.Id, second.Baseline, first.Definition with { Name = "local.ONE" }, default));
            Assert.Equal("UDF_LOCAL_VERSION_EXISTS", failure.Code);
            Assert.Equal(first.Baseline, (await store.GetAsync(first.Id, default)).Baseline); Assert.Equal(second.Baseline, (await store.GetAsync(second.Id, default)).Baseline);
            var updated = await store.UpdateAsync(first.Id, first.Baseline, first.Definition with { Expression = "() => 3" }, default);
            Assert.Equal(first.Id, updated.Id); Assert.Equal("1.0.0", updated.Definition.Version);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>两个实例同时创建同名同 tag, 独占锁内最多一个胜出, 不产生两个稳定身份.</summary>
    [Fact]
    public async Task ConcurrentDuplicateCreatesHaveOneWinner()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            async Task<bool> Create(string name)
            {
                try { await new LocalUdfStore(root).CreateAsync(new(1, name, "1.0.0", "() => 1", "", []), default); return true; }
                catch (AppException) { return false; }
            }
            var results = await Task.WhenAll(Create("Local.One"), Create("local.ONE"));
            Assert.Single(results, value => value); Assert.Single(await new LocalUdfStore(root).ListAsync(default));
            Assert.Equal("UDF_LOCAL_VERSION_EXISTS", (await Assert.ThrowsAsync<AppException>(() => new LocalUdfStore(root).CreateAsync(new(1, "LOCAL.ONE", "1.0.0", "() => 1", "", []), default))).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>其他记录损坏时无法证明名称唯一, 新建和更新均失败且保留全部原字节.</summary>
    [Fact]
    public async Task CorruptSiblingBlocksUniquenessChecksWithoutPartialWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var first = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var damaged = Path.Combine(root, "udf", "local", Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(damaged, "{\"schemaVersion\":999}");
            await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(first.Definition with { Name = "Local.New" }, default));
            await Assert.ThrowsAnyAsync<Exception>(() => store.UpdateAsync(first.Id, first.Baseline, first.Definition with { Expression = "() => 2" }, default));
            Assert.Equal(first.Baseline, (await store.GetAsync(first.Id, default)).Baseline); Assert.Equal("{\"schemaVersion\":999}", await File.ReadAllTextAsync(damaged));
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(damaged)!, "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>创建更新删除均绑定稳定 ID 与当前基线.</summary>
    [Fact]
    public async Task CrudRejectsStaleBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var one = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "说明", []), default);
            var two = await store.UpdateAsync(one.Id, one.Baseline, one.Definition with { Expression = "() => 2" }, default);
            Assert.Equal(one.Id, two.Id); Assert.NotEqual(one.Baseline, two.Baseline);
            await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync(one.Id, one.Baseline, default));
            await store.DeleteAsync(two.Id, two.Baseline, default);
            Assert.Empty(await store.ListAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>未知存储版本保留原文件并拒绝覆盖.</summary>
    [Fact]
    public async Task CorruptEntryIsPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var one = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var path = Path.Combine(root, "udf", "local", one.Id + ".json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":999}");
            await Assert.ThrowsAnyAsync<Exception>(() => store.UpdateAsync(one.Id, one.Baseline, one.Definition, default));
            Assert.Equal("{\"schemaVersion\":999}", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>并发同基线更新最多一次成功, 不覆盖胜出内容.</summary>
    [Fact]
    public async Task ConcurrentUpdatesHaveOneWinner()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var entry = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            async Task<bool> Update(string expression)
            {
                try { await new LocalUdfStore(root).UpdateAsync(entry.Id, entry.Baseline, entry.Definition with { Expression = expression }, default); return true; }
                catch (PbiUtils.Core.AppException) { return false; }
            }
            var results = await Task.WhenAll(Update("() => 2"), Update("() => 3"));
            Assert.Single(results, value => value);
            Assert.NotEqual(entry.Baseline, (await store.GetAsync(entry.Id, default)).Baseline);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>提交前取消及 UTF8 超额不创建文件.</summary>
    [Fact]
    public async Task CancellationAndUtf8LimitLeaveNoEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var definition = new LocalUdfDefinition(1, "Local.One", "1.0.0", "() => 1", "", []);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(definition, new CancellationToken(true)));
            await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(definition with { Expression = "() => \"" + new string('中', 11000) + "\"" }, default));
            Assert.Empty(await store.ListAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>外部有效改写也必须使旧删除基线失效.</summary>
    [Fact]
    public async Task ExternalModificationInvalidatesDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var entry = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var path = Path.Combine(root, "udf", "local", entry.Id + ".json");
            var json = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, json.Replace("1.0.0", "1.0.1", StringComparison.Ordinal));
            await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync(entry.Id, entry.Baseline, default));
            Assert.Equal("1.0.1", (await store.GetAsync(entry.Id, default)).Definition.Version);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>本地传递依赖以稳定 ID 与精确基线绑定, 修改依赖后旧选择失效.</summary>
    [Fact]
    public async Task LocalDependencyUsesStableIdAndBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var dependency = await store.CreateAsync(new(1, "Local.Two", "1.0.0", "() => 2", "", []), default);
            var entry = await store.CreateAsync(new(1, "Local.One", "1.0.0", "() => Local.Two()", "", [new("local", dependency.Id, "1.0.0", "Local.Two", dependency.Baseline)]), default);
            using var sources = new UdfSourceProvider(root, _ => throw new InvalidOperationException("Local-only resolution must not request HTTP"));
            var provider = sources.DefaultProvider;
            var resolver = new UdfDependencyResolver(provider, store);
            PbiUtils.Application.Udf.UdfSelection[] selection = [new("local", entry.Id, "1.0.0", ["Local.One"], Baseline: entry.Baseline)];
            Assert.Equal(2, (await resolver.ResolveAsync(selection, default)).Functions.Count);
            await store.UpdateAsync(dependency.Id, dependency.Baseline, dependency.Definition with { Expression = "() => 3" }, default);
            await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync(selection, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
