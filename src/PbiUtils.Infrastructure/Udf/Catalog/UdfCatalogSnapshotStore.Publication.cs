// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogSnapshotStore.Publication.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原子发布唯一当前目录, 严格验证当前指针并保护并发修订.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Udf.Catalog;

internal sealed partial class UdfCatalogSnapshotStore
{
    /// <summary>完整验证候选后只发布当前代, 回读确认后才退役旧发布代; committing 后排空.</summary>
    internal Snapshot Publish(byte[] candidate, string? expectedRevision, Action? committing, CancellationToken token)
    {
        var previous = ReadHead();
        if (previous?.Revision != expectedRevision) throw Changed();
        var staged = Deserialize<Pointer>(candidate) with { RetrievedAt = DateTimeOffset.UtcNow };
        ValidatePointer(staged, candidate: true);
        using (var snapshot = ReadGeneration(staged)) snapshot.ReadAll(token);
        var activated = staged with { SchemaVersion = 3, ActivationId = Guid.NewGuid().ToString("N") };
        Activate(activated, previous?.Revision, committing, token);
        var published = ReadPublished();
        try { RetirePublished(previous?.Head, activated.Generation); return published; }
        catch (Exception cleanup)
        {
            published.Dispose();
            throw new AppException("UDF_CATALOG_CLEANUP_FAILED", "函数缓存已更新, 旧缓存清理失败 (" + cleanup.GetType().Name + ").", 4);
        }
    }

    /// <summary>提交前最后重核完整指针; 写入后不响应取消或自动恢复旧指针.</summary>
    private void Activate(Pointer activated, string? expectedRevision, Action? committing, CancellationToken token)
    {
        if (ReadHead()?.Revision != expectedRevision) throw Changed();
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        // 原子写的末段也可能在发布后抛错; 此时只能只读核验, 不能删代或重放.
        try { UserDataStore.Write(pointer, JsonSerializer.SerializeToUtf8Bytes(activated, Json)); }
        catch (Exception exception) when (exception is AppException or IOException or UnauthorizedAccessException) { throw Unconfirmed(); }
    }

    /// <summary>指针已写后回读失败属于待核验, 不能报告提交前失败或重放.</summary>
    private Snapshot ReadPublished()
    {
        try { return Read() ?? throw Invalid(); }
        catch (Exception exception) when (exception is AppException or IOException or JsonException) { throw Unconfirmed(); }
    }

    /// <summary>读取当前发布指针, 以完整字节摘要作为修订; 未知格式拒绝读取.</summary>
    private (Pointer Head, string Revision)? ReadHead()
    {
        UserDataStore.Check(pointer);
        if (!File.Exists(pointer)) return null;
        try
        {
            var bytes = ReadBounded(pointer, 128 * 1024);
            var head = Deserialize<Pointer>(bytes);
            ValidatePointer(head);
            return (head, Digest(bytes));
        }
        catch (Exception exception) when (exception is JsonException or IOException or FormatException or ArgumentException or NullReferenceException)
        { throw Invalid(); }
    }

    /// <summary>仅接受当前发布 schema3 或本轮候选 schema1, 候选不能用作当前代.</summary>
    private static void ValidatePointer(Pointer head, bool candidate = false)
    {
        if ((candidate ? head.SchemaVersion != 1 : head.SchemaVersion != 3) || !Generation(head.Generation) || !Hash(head.Baseline) || !Hash(head.ManifestHash) || head.Baseline != head.ManifestHash
            || !Hash(head.RepositoryHash)) throw Invalid();
        if (candidate)
        {
            if (head.ActivationId is not null) throw Invalid();
            return;
        }
        if (!Generation(head.ActivationId)) throw Invalid();
    }

    /// <summary>内部代标识也拒绝大小写/路径变体.</summary>
    private static bool Generation(string? generation) => generation is { Length: 32 } && Guid.TryParseExact(generation, "N", out _) && generation.All(char.IsAsciiHexDigitLower);
    /// <summary>内容相等也不能覆盖另一轮激活的指针.</summary>
    private static AppException Changed() => new("UDF_CATALOG_CHANGED", "函数目录在操作期间已变化, 请重新读取并核验选择", 3);
    /// <summary>禁止以自动回滚或重放掩盖已写入指针的不确定回读.</summary>
    private static AppException Unconfirmed() => new("UDF_CATALOG_UNCONFIRMED", "函数目录指针已提交但回读未确认, 请只读重新核验当前目录, 不要重放本次操作", 3);
    private sealed record Pointer(int SchemaVersion, string Generation, string Baseline, string ManifestHash, DateTimeOffset RetrievedAt, string RepositoryHash,
        string? ActivationId = null);
}
