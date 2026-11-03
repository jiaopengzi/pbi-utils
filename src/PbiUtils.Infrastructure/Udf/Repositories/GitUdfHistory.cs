// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitUdfHistory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 全部 Git 来源使用标准有界历史请求补充版本目录提交时间, 不接入平台日期 API.

using PbiUtils.Application.Udf;

using PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>显式更新期间单次下载固定 commit 历史, 不改变包可执行内容身份, 浏览只使用持久化快照.</summary>
internal static class GitUdfHistory
{
    /// <summary>仅 enrich 完整快照, 网络/对象/取消失败向上传播; 可证明的浅历史不足保留 null.</summary>
    internal static async Task<IReadOnlyList<UdfPackage>> EnrichAsync(GitUdfDownload snapshot, IReadOnlyList<UdfPackage> packages,
        HttpClient? client, CancellationToken token)
    {
        var repository = GitHubUdfRepositoryUri.Normalize(snapshot.RepositoryUrl);
        // GitHub 标准 clone 路径显式带 .git 以避免301; 来源身份与原快照 URL 保持独立.
        if (repository.Host == "github.com") repository = new Uri(repository.AbsoluteUri + ".git");
        using var owned = client is null ? UdfSourceNetwork.CreateGitClient(repository) : null;
        var http = new GitSmartUdfHttp(client ?? owned!, repository);
        var advertisement = await http.AdvertiseAsync(token).ConfigureAwait(false);
        var reference = GitSmartUdfProtocol.Resolve(advertisement, snapshot.ResolvedCommit);
        var response = await http.FetchAsync(GitSmartUdfProtocol.Request(reference, history: true), token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var (pack, shallow) = GitSmartUdfProtocol.ReadHistoryPack(response);
            var objects = GitSmartUdfPack.Read(pack, token);
            var directories = snapshot.Files.Where(file => file.Path.StartsWith("packages/", StringComparison.Ordinal)
                && file.Path.EndsWith("/manifest.daxlib", StringComparison.Ordinal)).OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => file.Path[..^"/manifest.daxlib".Length]).ToArray();
            if (directories.Length != packages.Count) throw GitUdfCatalogSource.Invalid();
            var dates = GitUdfHistoryReader.Read(objects, snapshot.ResolvedCommit, snapshot.RootTree, directories, shallow, token);
            return (IReadOnlyList<UdfPackage>)[.. packages.Select((package, index) => package with { CommittedAt = dates[directories[index]] })];
        }, token).ConfigureAwait(false);
    }
}
