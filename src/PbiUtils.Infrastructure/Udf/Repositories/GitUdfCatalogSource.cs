// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitUdfCatalogSource.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将固定 Git commit 的完整 packages 布局解析为受验证的函数目录.

using PbiUtils.Application.Udf;
using PbiUtils.Core;
using System.Text.Json;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories.GitHub;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>仅显式更新获取无需登录的 HTTP(S) 仓库快照; 不调用 Git 或执行仓库内容.</summary>
public sealed class GitUdfCatalogSource
{
    private readonly UdfSourceEntry source;
    private readonly HttpClient? client;

    /// <summary>绑定来源配置与可选调用方客户端; userDataRoot 保留共享构造契约, 下载不创建用户目录.</summary>
    public GitUdfCatalogSource(string userDataRoot, UdfSourceEntry source, HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        if (source.Protocol != "git" || source.BaseUrl is null) throw Invalid();
        _ = GitHubUdfRepositoryUri.Normalize(source.BaseUrl);
        ValidateReference(source.GitRef);
        this.source = source;
        this.client = client;
    }

    /// <summary>ref 只能为提交、分支或标签, 不允许 refspec、选项或表达式; 空值表示远端 HEAD.</summary>
    public static void ValidateReference(string? gitRef)
    {
        if (gitRef is null) return;
        if (gitRef.Length is 0 or > 256 || gitRef.StartsWith('-') || gitRef.StartsWith('/') || gitRef.EndsWith('/') || gitRef.EndsWith('.') ||
            gitRef.Contains("..", StringComparison.Ordinal) || gitRef.Contains("@{", StringComparison.Ordinal) || gitRef.Contains("//", StringComparison.Ordinal) ||
            gitRef.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "~^:?*[\\".Contains(character)) ||
            gitRef == "@" || gitRef.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))) throw Invalid();
    }

    /// <summary>获取一个固定 commit 的全部包及历史版本; 失败/取消不返回部分目录, 不发布用户缓存.</summary>
    public async Task<IReadOnlyList<UdfPackage>> DownloadAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken) =>
        (await DownloadSnapshotAsync(progress, cancellationToken).ConfigureAwait(false)).Packages;

    /// <summary>完整包解析与来源 commit 一起返回给目录发布者; CPU 解析在线程池执行, 失败不返回部分结果.</summary>
    internal async Task<UdfRepositoryDownload> DownloadSnapshotAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(8));
        try
        {
            var snapshot = await GitUdfFetch.DownloadAsync(source, client, progress, bounded.Token).ConfigureAwait(false);
            var packages = await Task.Run(() => ReadSnapshot(snapshot.Files, progress, bounded.Token), bounded.Token).ConfigureAwait(false);
            packages = await GitUdfHistory.EnrichAsync(snapshot, packages, client, bounded.Token).ConfigureAwait(false);
            bounded.Token.ThrowIfCancellationRequested();
            return new(packages, snapshot.RepositoryUrl, snapshot.RequestedRef, snapshot.ResolvedCommit);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = new AppException("UDF_GIT_TIMEOUT", "Git 来源更新超时, 原目录保持不变");
            UdfCleanupNotice.Transfer(error, failure);
            throw failure;
        }
        catch (HttpRequestException) { throw new AppException("UDF_GIT_NETWORK", "Git 仓库网络或证书连接失败, 原目录保持不变"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Invalid(); }
        catch (IOException) { throw new AppException("UDF_GIT_NETWORK", "Git 仓库网络读取失败, 原目录保持不变"); }
    }

    /// <summary>预检完整布局、唯一身份与含祖先许可的累计解析字节后读取普通 blob, 超限或冲突整批拒绝.</summary>
    internal static IReadOnlyList<UdfPackage> ReadSnapshot(IReadOnlyList<GitUdfFile> snapshot, IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Count > 20000) throw Invalid();
        var files = new Dictionary<string, GitUdfFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in snapshot)
        {
            UdfRepositoryContent.ValidatePath(file.Path);
            if (file.Mode != "100644" ||
                file.Content.Length > DaxLibPackageReader.MaxFileBytes || (total += file.Content.Length) > GitUdfFetch.MaxContentBytes || !files.TryAdd(file.Path, file)) throw Invalid();
        }
        var manifests = files.Values.Where(file => file.Path.StartsWith("packages/", StringComparison.Ordinal) && file.Path.EndsWith("/manifest.daxlib", StringComparison.Ordinal)).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        if (manifests.Length is 0 or > 4096) throw Invalid();
        foreach (var file in files.Values.Where(file => file.Path.StartsWith("packages/", StringComparison.Ordinal)))
        {
            var parts = file.Path.Split('/');
            if (parts.Length >= 5 && !files.ContainsKey(string.Join('/', parts[..4]) + "/manifest.daxlib")) throw Invalid();
        }
        var candidates = new List<(string Id, string Version, Dictionary<string, byte[]> Files)>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = manifest.Path.Split('/');
            if (parts.Length != 5 || parts[1].Length != 1 || parts[1][0] is < 'a' or > 'z' || char.ToLowerInvariant(parts[2][0]) != parts[1][0]) throw Invalid();
            var id = parts[2];
            var version = parts[3];
            UdfValidation.PackageIdentity(id, version);
            if (!identities.Add(id + "@" + version)) throw Invalid();
            var prefix = string.Join('/', parts[..4]) + "/";
            var packageFiles = files.Values.Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(file => file.Path[prefix.Length..], file => file.Content, StringComparer.OrdinalIgnoreCase);
            // 祖先许可使用独立路径保留, 包内同名许可不得覆盖仓库许可或反之.
            var ancestors = new[] { "", "packages/", $"packages/{parts[1]}/", $"packages/{parts[1]}/{id}/" };
            foreach (var ancestor in ancestors)
                foreach (var license in files.Values.Where(file => Parent(file.Path) == ancestor && IsLicense(Path.GetFileName(file.Path))))
                    if (!packageFiles.TryAdd("repository/" + license.Path, license.Content)) throw Invalid();
            // 祖先许可会逐包解码并保留, 必须将每次逻辑展开计入同一预算, 不能只计算仓库唯一字节.
            foreach (var content in packageFiles.Values)
                if ((expandedBytes += content.LongLength) > GitUdfFetch.MaxContentBytes)
                    throw new AppException("UDF_GIT_LIMIT", "Git 来源展开后的完整目录超过解析容量限制");
            candidates.Add((id, version, packageFiles));
        }
        if (candidates.Select(package => package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1024) throw Invalid();
        var result = new List<UdfPackage>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(DaxLibPackageReader.ReadFiles(candidate.Files, candidate.Id, candidate.Version));
            progress?.Report(new("git.parse", result.Count, manifests.Length));
        }
        return result;
    }

    /// <summary>只读取 Markdown README、标准包部件、图片和文本许可, 其他仓库源码不下载到内存.</summary>
    internal static bool Relevant(string path) => path.StartsWith("packages/", StringComparison.Ordinal) || !path.Contains('/') && IsLicense(path);

    /// <summary>保留与 ZIP 入口一致的许可扩展名约束.</summary>
    private static bool IsLicense(string path) => (path.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || path.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)) && Path.GetExtension(path).ToLowerInvariant() is "" or ".md" or ".txt" or ".text" or ".rst";

    /// <summary>仓库路径始终使用 '/', 不依赖平台目录分隔符.</summary>
    private static string Parent(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
    /// <summary>稳定错误不暴露远端路径、内容或地址.</summary>
    internal static AppException Invalid() => new("UDF_GIT_INVALID", "Git 来源布局、引用或安全限制不符合要求");
}

/// <summary>固定 commit 中经过模式验证的只读文件, 不表示本地磁盘路径.</summary>
internal sealed record GitUdfFile(string Path, string Mode, byte[] Content);

/// <summary>完整包目录及独立来源信息, commit 不参与已有包内容哈希.</summary>
internal sealed record UdfRepositoryDownload(IReadOnlyList<UdfPackage> Packages, string RepositoryUrl, string? RequestedRef, string ResolvedCommit);
