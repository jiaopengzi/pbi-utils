// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Sources\UdfSourceProvider.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按宿主来源身份和配置基线路由完整本地函数目录.

using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Catalog;

namespace PbiUtils.Infrastructure.Udf.Sources;

/// <summary>每个来源快照独享本地目录, 不把名称相同当成相同来源.</summary>
public sealed class UdfSourceProvider : IUdfSourceProvider, IDisposable
{
    private readonly Dictionary<string, IUdfCatalogProvider> providers = new(StringComparer.Ordinal);
    private readonly Lock sync = new();
    private readonly string userDataRoot;
    private readonly Func<UdfSourceEntry, HttpClient>? clientFactory;
    private readonly UdfSourceStore store;
    private bool disposed;

    /// <summary>只组合固定配置, 构造不读取磁盘、不创建 HTTP 客户端或联网.</summary>
    public UdfSourceProvider(string userDataRoot, Func<UdfSourceEntry, HttpClient>? clientFactory = null)
    {
        this.userDataRoot = userDataRoot; this.clientFactory = clientFactory;
        store = new(userDataRoot); Sources = store;
        DefaultProvider = new UdfCatalogProvider(userDataRoot, UdfSourceDefaults.Official, store, clientFactory);
        providers.Add("daxlib:", DefaultProvider);
    }

    /// <summary>共享官方来源也使用本地目录边界, 不暴露旧网络适配器给生产解析器.</summary>
    public IUdfCatalogProvider DefaultProvider { get; }
    /// <inheritdoc />
    public IUdfSourceRegistry Sources { get; }

    /// <inheritdoc />
    public async Task<IUdfPackageProvider> GetProviderAsync(string source, string? baseline, CancellationToken cancellationToken)
    {
        if (!UdfValidation.IsSourceId(source) || source == "local") throw new AppException("UDF_SOURCE_INVALID", "函数包来源无效");
        var entry = await Sources.GetAsync(source, cancellationToken).ConfigureAwait(false);
        if (entry.Kind == "custom" && (baseline is null || entry.Baseline != baseline)) throw new AppException("UDF_SOURCE_CHANGED", "函数来源已变化, 请刷新并重新选择");
        var key = entry.Id + ":" + entry.Baseline;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!providers.TryGetValue(key, out var provider)) providers.Add(key, provider = new UdfCatalogProvider(userDataRoot, entry, store, clientFactory));
            return provider;
        }
    }

    /// <summary>宿主先取消并排空请求, 每次显式刷新的网络客户端由该刷新自己释放.</summary>
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; providers.Clear(); } }
}
