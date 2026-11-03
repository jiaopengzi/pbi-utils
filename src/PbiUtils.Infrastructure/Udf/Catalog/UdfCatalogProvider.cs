// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogProvider.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将生产浏览限定于轻量本地索引, 显式更新持有发布锁且固定读者拥有明确释放边界.

using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Repositories;
using PbiUtils.Infrastructure.Udf.Sources;

namespace PbiUtils.Infrastructure.Udf.Catalog;

/// <summary>来源绑定不跨缓存回退; 只有显式刷新创建网络客户端.</summary>
internal sealed class UdfCatalogProvider(string userDataRoot, UdfSourceEntry source, UdfSourceStore sources,
    Func<UdfSourceEntry, HttpClient>? clientFactory) : IUdfCatalogProvider, IUdfCatalogSnapshotSource
{
    private readonly UdfCatalogSnapshotStore store = new(userDataRoot, source);

    /// <inheritdoc />
    public async Task<UdfCatalogStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(snapshot =>
            {
                // 状态入口保留完整性核验; 包分页直接绑定轻量索引, 不经过此入口.
                snapshot?.ReadAll(cancellationToken);
                return snapshot?.Status ?? new(false, null, null, 0, 0, 0);
            }, cancellationToken, required: false).ConfigureAwait(false);
        }
        catch (AppException error) when (error.Code == "UDF_CATALOG_UPDATE_REQUIRED")
        {
            await CheckSourceAsync(cancellationToken).ConfigureAwait(false);
            return new(false, null, null, 0, 0, 0);
        }
    }

    /// <inheritdoc />
    public async Task<IUdfCatalogSnapshotProvider> OpenSnapshotAsync(CancellationToken cancellationToken)
    {
        await CheckSourceAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await Task.Run(store.Read, cancellationToken).ConfigureAwait(false)
            ?? throw new AppException("UDF_CATALOG_UNINITIALIZED", "此来源尚无完整本地目录, 请先显式更新来源", 3);
        try
        {
            await CheckSourceAsync(cancellationToken).ConfigureAwait(false);
            return new UdfCatalogSnapshotReader(snapshot);
        }
        catch { snapshot.Dispose(); throw; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken)
    {
        using var reader = await OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await reader.SearchAsync(query, includePrerelease, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        using var reader = await OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetVersionsAsync(packageId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        using var reader = await OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetAsync(packageId, version, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken)
    {
        using var reader = await OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await reader.QueryPackagesAsync(query, expectedBaseline, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken)
    {
        using var reader = await OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetIconAsync(packageId, version, iconKey, expectedBaseline, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UdfCatalogStatus> RefreshAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken, Action? committing = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        await CheckSourceAsync(token).ConfigureAwait(false);
        using var gate = await Task.Run(store.LockRefresh, token).ConfigureAwait(false);
        var original = await Task.Run(() => store.ReadRefreshRevision(token), token).ConfigureAwait(false);
        progress?.Report(new("catalog", 0, 0));
        using var client = clientFactory?.Invoke(source);
        var download = await new GitUdfCatalogSource(userDataRoot, source, client).DownloadSnapshotAsync(progress, token).ConfigureAwait(false);
        var packages = download.Packages;
        progress?.Report(new("validate", packages.Count, packages.Count));
        var repository = new UdfRepositoryRevision(download.RepositoryUrl, download.RequestedRef, download.ResolvedCommit);
        var candidate = await Task.Run(() => store.Stage(packages, repository, token), token).ConfigureAwait(false);
        Exception? firstFailure = null;
        try
        {
            using var configurationGate = await Task.Run(sources.LockConfiguration, token).ConfigureAwait(false);
            await CheckSourceAsync(token).ConfigureAwait(false);
            using var published = await Task.Run(() => store.Publish(candidate, original, committing, token), CancellationToken.None).ConfigureAwait(false);
            return published.Status;
        }
        catch (Exception error) { firstFailure = error; throw; }
        finally
        {
            // 成功发布代已跨越提交边界, 即使指针随后被外部移除也不能再当作未发布候选删除.
            if (firstFailure is not null) await Task.Run(() => store.CleanupCandidate(candidate, firstFailure), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>来源复核前后固定一代索引, 旧提供者不能继续读取已删除或改变的配置.</summary>
    private async Task<T> ReadAsync<T>(Func<UdfCatalogSnapshotStore.Snapshot?, T> read, CancellationToken token, bool required = true)
    {
        await CheckSourceAsync(token).ConfigureAwait(false);
        var result = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var snapshot = store.Read();
            if (required && snapshot is null) throw new AppException("UDF_CATALOG_UNINITIALIZED", "此来源尚无完整本地目录, 请先显式更新来源", 3);
            var observed = read(snapshot); token.ThrowIfCancellationRequested(); return observed;
        }, token).ConfigureAwait(false);
        await CheckSourceAsync(token).ConfigureAwait(false);
        return result;
    }

    /// <summary>旧来源配置不能读取或发布到新的来源绑定, 不按名字迁移缓存.</summary>
    private async Task CheckSourceAsync(CancellationToken token)
    {
        var observed = await sources.GetAsync(source.Id, token).ConfigureAwait(false);
        if (observed != source) throw new AppException("UDF_SOURCE_CHANGED", "函数来源已变化, 请重新选择来源", 3);
    }
}
