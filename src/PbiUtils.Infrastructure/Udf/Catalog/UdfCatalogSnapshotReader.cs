// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogSnapshotReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定不可变目录索引, 按包轻量分页并按需验证完整包正文.

using System.Collections.ObjectModel;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Udf.Catalog;

/// <summary>索引固定同一代, 正文每次按需读取并复制, 不暴露内部可变集合.</summary>
internal sealed class UdfCatalogSnapshotReader(UdfCatalogSnapshotStore.Snapshot snapshot) : IUdfCatalogSnapshotProvider
{
    /// <inheritdoc />
    public void Dispose() => snapshot.Dispose();

    /// <inheritdoc />
    public UdfCatalogStatus Status { get; } = snapshot.Status;

    /// <inheritdoc />
    public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null || query.Length > 256) throw Invalid();
        IReadOnlyList<UdfPackageSummary> result = UdfCatalogSnapshotStore.LatestEntries(snapshot.Entries, includePrerelease)
            .Where(package => Match(package.Id, query) || Match(package.Description!, query)
                || package.Functions!.Any(function => Match(function.Name, query) || Match(function.Description, query)))
            .Select(package => new UdfPackageSummary(package.Id, package.Version, package.Description!)).ToArray();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
        cancellationToken.ThrowIfCancellationRequested(); UdfValidation.PackageIdentity(packageId, "0.0.0");
        return Task.FromResult<IReadOnlyList<string>>(snapshot.Entries.Where(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).Select(package => package.Version).ToArray());
    }

    /// <inheritdoc />
    public async Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
        cancellationToken.ThrowIfCancellationRequested(); UdfValidation.PackageIdentity(packageId, version);
        var entry = snapshot.Entries.SingleOrDefault(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && package.Version == version)
            ?? throw new AppException("UDF_PACKAGE_MISSING", "本地完整目录中不存在所选精确函数版本, 请显式更新来源", 3);
        var package = await Task.Run(() => snapshot.Load(entry), cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return package with
        {
            LicenseFiles = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(package.LicenseFiles)),
            Dependencies = Array.AsReadOnly(package.Dependencies.ToArray()),
            Metadata = package.Metadata is { } metadata ? metadata with { Tags = [.. metadata.Tags] } : null,
            Functions = Array.AsReadOnly(package.Functions.Select(function => function with
            {
                Annotations = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(function.Annotations))
            }).ToArray())
        };
    }

    /// <inheritdoc />
    public Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken)
    {
        CheckBaseline(expectedBaseline); cancellationToken.ThrowIfCancellationRequested();
        if (query is null || query.Query is null || query.Query.Length > 256 || query.Offset is < 0 or > 1024 || query.Limit is < 1 or > 500 || query.PackageVersions?.Count > 1024) throw Invalid();
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.PackageVersions ?? new Dictionary<string, string>())
        {
            UdfValidation.PackageIdentity(pair.Key, pair.Value);
            if (!overrides.TryAdd(pair.Key, pair.Value) || !snapshot.Entries.Any(item => item.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) && item.Version == pair.Value)) throw Invalid();
            if (UdfValidation.IsPrerelease(pair.Value) && !query.IncludePrerelease) throw new AppException("UDF_PRERELEASE_PERMISSION", "显示预发行标签需要明确启用预发行选项");
        }
        // 有稳定版始终展示稳定版; beta-only 候选仅供发现, 选择仍须明确 tag 与独立预发行许可.
        var chosen = snapshot.Entries.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => overrides.TryGetValue(group.Key, out var version) ? group.Single(item => item.Version == version)
                : UdfCatalogSnapshotStore.LatestEntries(group.ToArray(), false).SingleOrDefault()
                    ?? (query.IncludePrerelease ? UdfCatalogSnapshotStore.LatestEntries(group.ToArray(), true).SingleOrDefault() : null)).Where(item => item is not null);
        var needle = query.Query.Trim();
        var all = chosen.Where(package => PackageMatches(package!, needle))
            .OrderBy(package => package!.Id, StringComparer.OrdinalIgnoreCase).ThenBy(package => package!.Id, StringComparer.Ordinal).ToArray();
        if (all.Length > 1024 || query.Offset > all.Length) throw Invalid();
        var items = new List<UdfCatalogPackage>();
        var page = new UdfPackagePage(items, all.Length, query.Offset < all.Length ? query.Offset : null, Status);
        foreach (var entry in all.Skip(query.Offset).Take(query.Limit))
        {
            ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var package = entry!;
            // 日期来自同代已验证的精确版本目录最后修改提交, 未知保持 null, 不以仓库 HEAD 或缓存时间代替.
            items.Add(new(package.Id, package.Version, package.Hash!, package.Description!, package.Authors!, [.. package.Tags!], package.CommittedAt, package.IconKey, package.Functions!.Length));
            var next = query.Offset + items.Count;
            var candidate = new UdfPackagePage(items, all.Length, next < all.Length ? next : null, Status);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, JsonSerializerOptions.Web).Length > 48 * 1024)
            {
                items.RemoveAt(items.Count - 1);
                if (items.Count == 0) throw new AppException("UDF_CATALOG_RESPONSE_LIMIT", "单个函数包摘要超出显示大小限制", 3);
                break;
            }
            page = candidate;
        }
        return Task.FromResult(page);
    }

    /// <summary>包名、说明、作者、分类和包内函数均参与模糊匹配, 多次命中仍只返回同一个包.</summary>
    private static bool PackageMatches(UdfCatalogSnapshotStore.PackageFile package, string query) =>
        UdfFunctionNameMatch.Ranges(package.Id, query) is not null || UdfFunctionNameMatch.Ranges(package.Description!, query) is not null ||
        UdfFunctionNameMatch.Ranges(package.Authors!, query) is not null || package.Tags!.Any(tag => UdfFunctionNameMatch.Ranges(tag, query) is not null) ||
        package.Functions!.Any(function => UdfFunctionNameMatch.Ranges(function.Name, query) is not null || UdfFunctionNameMatch.Ranges(function.Description, query) is not null);

    /// <inheritdoc />
    public async Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken)
    {
        CheckBaseline(expectedBaseline);
        if (iconKey is not { Length: 64 } || iconKey.Any(character => !char.IsAsciiHexDigitLower(character))) throw Invalid();
        var entry = snapshot.Entries.SingleOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && item.Version == version);
        if (entry?.IconKey != iconKey) throw new AppException("UDF_ICON_CHANGED", "函数库图标身份已变化, 请重新读取目录", 3);
        var package = await GetAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        return new(iconKey, package.Metadata?.IconDataUrl);
    }

    /// <summary>有目录见证时严格匹配当前固定代; 首次浏览、固定版本及本地解析可不提供见证.</summary>
    private void CheckBaseline(string? expected)
    {
        ObjectDisposedException.ThrowIf(snapshot.Lease.SafeFileHandle.IsClosed, this);
        if (expected is null) return;
        if (expected.Length != 64 || expected.Any(character => !char.IsAsciiHexDigitLower(character))) throw Invalid();
        if (expected != Status.Baseline) throw new AppException("UDF_CATALOG_CHANGED", "函数目录在读取期间已更新, 请重新读取并核验选择", 3);
    }
    /// <summary>提供者完整搜索使用大小写不敏感的纯文本匹配.</summary>
    private static bool Match(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
    /// <summary>非法分页或身份不通过裁剪/取整变为有效.</summary>
    private static AppException Invalid() => new("INVALID_ARGUMENT", "函数目录查询参数无效");
}
