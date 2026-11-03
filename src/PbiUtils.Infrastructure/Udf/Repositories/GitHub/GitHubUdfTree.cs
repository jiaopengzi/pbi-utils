// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitHub\GitHubUdfTree.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证固定 GitHub 树的完整性、对象类型、路径唯一性及容量预算.

using System.Text.Json;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.GitHub;

/// <summary>完整树既约束包文件也约束其余仓库文件, 避免归档省略整个版本而仍成功.</summary>
internal static class GitHubUdfTree
{
    /// <summary>拒绝截断树、链接、子模块和冲突路径, 返回所有普通文件的准确 blob 身份.</summary>
    internal static IReadOnlyList<GitUdfTreeEntry> Read(JsonElement root, string expectedTree, CancellationToken cancellationToken)
    {
        if (root.GetProperty("sha").GetString() != expectedTree || root.GetProperty("truncated").ValueKind != JsonValueKind.False ||
            root.GetProperty("tree").ValueKind != JsonValueKind.Array) throw GitUdfCatalogSource.Invalid();
        var array = root.GetProperty("tree");
        if (array.GetArrayLength() is 0 or > GitUdfFetch.MaxEntries) throw GitUdfCatalogSource.Invalid();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var hierarchy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GitUdfTreeEntry>();
        long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = item.GetProperty("path").GetString()!;
            UdfRepositoryContent.ValidatePath(path);
            var ancestor = path;
            while (true)
            {
                if (hierarchy.TryGetValue(ancestor, out var prior) && prior != ancestor) throw GitUdfCatalogSource.Invalid();
                hierarchy[ancestor] = ancestor;
                if (!ancestor.Contains('/')) break;
                ancestor = ancestor[..ancestor.LastIndexOf('/')];
            }
            var mode = item.GetProperty("mode").GetString();
            var type = item.GetProperty("type").GetString();
            var hash = item.GetProperty("sha").GetString();
            if (hash is null || hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw GitUdfCatalogSource.Invalid();
            var directory = type == "tree" && mode == "040000";
            if (!paths.TryAdd(path, directory)) throw GitUdfCatalogSource.Invalid();
            if (directory) continue;
            if (type != "blob" || mode is not ("100644" or "100755") ||
                GitUdfCatalogSource.Relevant(path) && mode != "100644" || !item.GetProperty("size").TryGetInt32(out var size) ||
                size < 0 || size > DaxLibPackageReader.MaxFileBytes || (total += size) > GitUdfFetch.MaxContentBytes) throw GitUdfCatalogSource.Invalid();
            result.Add(new(path, mode, hash.ToLowerInvariant(), size));
        }
        foreach (var path in paths.Keys)
        {
            var parent = path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (paths.TryGetValue(parent, out var directory) && !directory) throw GitUdfCatalogSource.Invalid();
            }
        }
        if (result.Count == 0) throw GitUdfCatalogSource.Invalid();
        return result;
    }

}

/// <summary>固定树中的普通文件元数据, 哈希是 Git blob SHA-1 而非原始内容 SHA-256.</summary>
internal sealed record GitUdfTreeEntry(string Path, string Mode, string Hash, int Size);
