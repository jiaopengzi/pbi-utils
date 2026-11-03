// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LocalUdfCommitTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证本地 UDF 可靠回执钩子只在完整校验后触发, 钩子失败不会修改定义.

using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class LocalUdfCommitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-udf-commit-" + Guid.NewGuid().ToString("N"));

    /// <summary>唯一性、精确基线和回执写入失败都发生在真实存储副作用之前.</summary>
    [Fact]
    public async Task ValidatesBeforeCommitAndReceiptFailurePreservesOriginalDefinition()
    {
        var store = new LocalUdfStore(root);
        var definition = new LocalUdfDefinition(1, "Local.Checked", "1.0.0", "() => 1", "", []);
        var commits = 0;
        var created = await store.CreateAsync(definition, default, () => commits++);
        Assert.Equal(1, commits);
        await Assert.ThrowsAsync<AppException>(() => store.CreateAsync(definition, default, () => commits++));
        await Assert.ThrowsAsync<AppException>(() => store.UpdateAsync(created.Id, "stale", definition, default, () => commits++));
        Assert.Equal(1, commits);
        var changed = definition with { Expression = "() => 2" };
        Assert.Equal("RECEIPT_SAVE_FAILED", (await Assert.ThrowsAsync<AppException>(() => store.UpdateAsync(created.Id, created.Baseline, changed, default,
            () => throw new AppException("RECEIPT_SAVE_FAILED", "controlled")))).Code);
        var unchanged = await store.GetAsync(created.Id, default);
        Assert.Equal(created.Baseline, unchanged.Baseline);
        Assert.Equal(created.Definition.Expression, unchanged.Definition.Expression);
        var updated = await store.UpdateAsync(created.Id, created.Baseline, changed, default, () => commits++);
        await Assert.ThrowsAsync<AppException>(() => store.DeleteAsync(updated.Id, created.Baseline, default, () => commits++));
        Assert.Equal(2, commits);
        await store.DeleteAsync(updated.Id, updated.Baseline, default, () => commits++);
        Assert.Equal(3, commits);
        Assert.Empty(await store.ListAsync(default));
    }

    /// <summary>只清理本测试持有的随机隔离根.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
