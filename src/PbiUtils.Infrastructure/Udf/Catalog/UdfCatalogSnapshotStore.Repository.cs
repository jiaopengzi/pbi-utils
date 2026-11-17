// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogSnapshotStore.Repository.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将固定仓库提交追溯绑定不可变代和原子历史指针, 与内容基线分离.

using PbiUtils.Application.Udf;

namespace PbiUtils.Infrastructure.Udf.Catalog;

internal sealed partial class UdfCatalogSnapshotStore
{
    /// <summary>校验同代追溯文件的摘要与登记来源; 损坏或错源时拒绝读取, 不回退其他代.</summary>
    private UdfRepositoryRevision ReadRepository(string generation, string digest)
    {
        if (!Hash(digest)) throw Invalid();
        var bytes = ReadBounded(Path.Combine(generation, "repository.json"), 4096);
        if (Digest(bytes) != digest) throw Invalid();
        var repository = Deserialize<UdfRepositoryRevision>(bytes);
        ValidateRepository(repository);
        return repository;
    }

    /// <summary>追溯必须绑定登记的仓库与 ref, 内置 DAXLib 也接受预设镜像; commit 只接受完整小写 SHA1.</summary>
    private void ValidateRepository(UdfRepositoryRevision repository)
    {
        if (repository is null || ((repository.RepositoryUrl != source.BaseUrl || repository.RequestedRef != source.GitRef) && !IsDaxLibMirror(repository))
            || repository.ResolvedCommit is not { Length: 40 } || !repository.ResolvedCommit.All(char.IsAsciiHexDigitLower)) throw Invalid();
    }

    /// <summary>只将内置来源中固定镜像仓库及固定 ref 的追溯认作自动切换.</summary>
    private bool IsDaxLibMirror(UdfRepositoryRevision repository) => source == UdfSourceDefaults.Official
        && repository.RepositoryUrl == UdfSourceDefaults.DaxLibMirror.BaseUrl && repository.RequestedRef == UdfSourceDefaults.DaxLibMirror.GitRef;
}

/// <summary>一代实际下载来源; 不把commit或获取时间混入函数包的规范内容hash.</summary>
internal sealed record UdfRepositoryRevision(string RepositoryUrl, string? RequestedRef, string ResolvedCommit);
