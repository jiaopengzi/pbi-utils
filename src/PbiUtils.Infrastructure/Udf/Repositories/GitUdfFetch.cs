// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitUdfFetch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按仓库选择固定 GitHub HTTPS 或标准 Git HTTP 获取, 不调用本机 Git.

using System.Text.Json;
using PbiUtils.Application.Udf;

using PbiUtils.Infrastructure.Udf.Repositories.GitHub;
using PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>同一次更新只使用解析得到的固定 commit, 归档完整验证后才交给共享包解析器.</summary>
internal static class GitUdfFetch
{
    internal const long MaxContentBytes = 128L * 1024 * 1024;
    internal const int MaxEntries = 20000;

    /// <summary>使用公开 GitHub API 解析 ref 并验证完整 commit ZIP; 注入客户端由调用方拥有.</summary>
    internal static async Task<GitUdfDownload> DownloadAsync(UdfSourceEntry source, HttpClient? client, IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = GitHubUdfRepositoryUri.Normalize(source.BaseUrl!);
        if (repository.Host != "github.com") return await GitSmartUdfFetch.DownloadAsync(source, repository, client, progress, cancellationToken).ConfigureAwait(false);
        var path = repository.AbsolutePath.Trim('/');
        using var owned = client is null ? UdfSourceNetwork.CreateClient() : null;
        var transport = new GitHubUdfHttp(client ?? owned!);
        var reference = source.GitRef;
        if (reference is null)
        {
            using var metadata = await transport.JsonAsync($"https://api.github.com/repos/{path}", 256 * 1024, cancellationToken).ConfigureAwait(false);
            reference = String(metadata.RootElement, "default_branch");
            GitUdfCatalogSource.ValidateReference(reference);
        }
        progress?.Report(new("git.fetch", 0, 3));
        using var commitData = await transport.JsonAsync($"https://api.github.com/repos/{path}/commits/{Uri.EscapeDataString(reference)}", 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var commit = Hash(commitData.RootElement, "sha");
        var commitTree = commitData.RootElement.GetProperty("commit").GetProperty("tree");
        var treeHash = Hash(commitTree, "sha");
        if (reference.Length == 40 && reference.All(Uri.IsHexDigit) && !reference.Equals(commit, StringComparison.OrdinalIgnoreCase)) throw GitUdfCatalogSource.Invalid();
        progress?.Report(new("git.fetch", 1, 3));
        using var tree = await transport.JsonAsync($"https://api.github.com/repos/{path}/git/trees/{treeHash}?recursive=1", 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var entries = await Task.Run(() => GitHubUdfTree.Read(tree.RootElement, treeHash, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(new("git.fetch", 2, 3));
        var archive = await transport.ArchiveAsync($"https://codeload.github.com/{path}/zip/{commit}", cancellationToken).ConfigureAwait(false);
        var prefix = path.Split('/')[1] + "-" + commit + "/";
        var files = await Task.Run(() => GitHubUdfArchive.Read(archive, entries, prefix, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(new("git.fetch", 3, 3));
        return new(files, repository.AbsoluteUri.TrimEnd('/'), source.GitRef, commit, treeHash);
    }

    /// <summary>JSON 必需文本字段保持严格类型, 不把空值或对象转换成 ref.</summary>
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : throw GitUdfCatalogSource.Invalid();

    /// <summary>GitHub 当前对象 ID 为 SHA-1, 统一小写但不截断任何远端值.</summary>
    private static string Hash(JsonElement element, string name)
    {
        var hash = String(element, name);
        return hash.Length == 40 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : throw GitUdfCatalogSource.Invalid();
    }
}

/// <summary>经过归档完整性核对的固定仓库快照, 尚未发布缓存.</summary>
internal sealed record GitUdfDownload(IReadOnlyList<GitUdfFile> Files, string RepositoryUrl, string? RequestedRef, string ResolvedCommit, string RootTree);
