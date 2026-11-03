// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitHub\GitHubUdfArchive.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在内存中逐项核验 GitHub commit ZIP 与完整树的文件集合、模式和 blob 哈希.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.GitHub;

/// <summary>不解压到磁盘、不执行仓库内容, 所有文件均验证后只保留包和适用许可.</summary>
internal static class GitHubUdfArchive
{
    /// <summary>要求唯一固定根及树中全部文件; 拒绝漏项、额外项、链接、伪造内容和压缩炸弹.</summary>
    internal static IReadOnlyList<GitUdfFile> Read(byte[] bytes, IReadOnlyList<GitUdfTreeEntry> tree, string prefix, CancellationToken cancellationToken)
    {
        var expectedEntries = GitHubUdfZipDirectory.Check(bytes, cancellationToken);
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        cancellationToken.ThrowIfCancellationRequested();
        if (archive.Entries.Count != expectedEntries) throw GitUdfCatalogSource.Invalid();
        var rootSlash = archive.Entries[0].FullName.IndexOf('/');
        if (rootSlash < 0) throw GitUdfCatalogSource.Invalid();
        var actualPrefix = archive.Entries[0].FullName[..(rootSlash + 1)];
        if (!actualPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)) throw GitUdfCatalogSource.Invalid();
        var expected = tree.ToDictionary(item => item.Path, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in expected.Keys)
        {
            var parent = path;
            while (parent.Contains('/')) { parent = parent[..parent.LastIndexOf('/')]; directories.Add(parent); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GitUdfFile>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(actualPrefix, StringComparison.Ordinal)) throw GitUdfCatalogSource.Invalid();
            var name = entry.FullName[actualPrefix.Length..];
            var directory = entry.FullName.EndsWith('/');
            if (directory && name.Length > 0) name = name[..^1];
            if (name.Length == 0 && entry.FullName.Length != prefix.Length) throw GitUdfCatalogSource.Invalid();
            if (name.Length != 0) UdfRepositoryContent.ValidatePath(name);
            if (!seen.Add(name) || name.Length == 0 && !directory) throw GitUdfCatalogSource.Invalid();
            var mode = (entry.ExternalAttributes >> 16) & 0xffff;
            var kind = mode & 0xf000;
            if ((entry.ExternalAttributes & 0x400) != 0 || kind != 0 && kind != (directory ? 0x4000 : 0x8000) ||
                !directory && (entry.ExternalAttributes & 0x10) != 0) throw GitUdfCatalogSource.Invalid();
            if (directory)
            {
                if (entry.Length != 0 || name.Length != 0 && !directories.Contains(name)) throw GitUdfCatalogSource.Invalid();
                continue;
            }
            if (!expected.TryGetValue(name, out var file) || !found.Add(name) || entry.Length != file.Size ||
                entry.Length > DaxLibPackageReader.MaxFileBytes || (total += entry.Length) > GitUdfFetch.MaxContentBytes ||
                entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length > entry.CompressedLength * 200) ||
                kind == 0x8000 && ((mode & 0x49) != 0) != (file.Mode == "100755")) throw GitUdfCatalogSource.Invalid();
            using var content = entry.Open();
            using var output = new MemoryStream(file.Size);
            // SHA-1 是 Git 对象寻址算法, 这里只核对固定 HTTPS 树中的既定对象 ID.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes("blob " + file.Size.ToString(CultureInfo.InvariantCulture) + "\0"));
            var buffer = new byte[81920];
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + read > file.Size) throw GitUdfCatalogSource.Invalid();
                hash.AppendData(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
            }
            if (output.Length != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Hash) throw GitUdfCatalogSource.Invalid();
            if (UdfRepositoryContent.IsLfsPointer(output.GetBuffer().AsSpan(0, file.Size))) throw GitUdfCatalogSource.Invalid();
            if (GitUdfCatalogSource.Relevant(name)) result.Add(new(name, file.Mode, output.ToArray()));
        }
        if (found.Count != expected.Count) throw GitUdfCatalogSource.Invalid();
        return result;
    }

}
