// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfFetch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过无凭据标准 Git smart HTTP 获取固定提交并验证全部树对象.

using PbiUtils.Application.Udf;

using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>传输仅驻留内存, 不创建 checkout、不加载系统 Git 配置、不执行仓库内容.</summary>
internal static class GitSmartUdfFetch
{
    /// <summary>使用显式仓库的同源受限连接, 广告解析后所有数据只绑定固定对象 ID.</summary>
    internal static async Task<GitUdfDownload> DownloadAsync(UdfSourceEntry source, Uri repository, HttpClient? client,
        IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken)
    {
        using var owned = client is null ? UdfSourceNetwork.CreateClient(repository) : null;
        var http = new GitSmartUdfHttp(client ?? owned!, repository);
        progress?.Report(new("git.fetch", 0, 3));
        var advertisement = await http.AdvertiseAsync(cancellationToken).ConfigureAwait(false);
        var reference = GitSmartUdfProtocol.Resolve(advertisement, source.GitRef);
        progress?.Report(new("git.fetch", 1, 3));
        var response = await http.FetchAsync(GitSmartUdfProtocol.Request(reference), cancellationToken).ConfigureAwait(false);
        progress?.Report(new("git.fetch", 2, 3));
        var (files, commit, tree) = await Task.Run(() =>
        {
            var objects = GitSmartUdfPack.Read(GitSmartUdfProtocol.ReadPack(response), cancellationToken);
            return GitSmartUdfTree.Read(objects, reference.Hash, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("git.fetch", 3, 3));
        return new(files, repository.AbsoluteUri, source.GitRef, commit, tree);
    }
}
