// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfTree.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从固定 Git commit 验证完整内存树、路径、对象模式及普通文件内容.

using System.Text;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>所有树边均由固定 commit 的对象 ID 认证, 不因 packages 子集完整而省略其余安全验证.</summary>
internal static class GitSmartUdfTree
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>剥离有限注解标签后读取完整固定提交树, 返回受限包/许可文件及最终 commit.</summary>
    internal static (IReadOnlyList<GitUdfFile> Files, string Commit, string Tree) Read(IReadOnlyDictionary<string, GitSmartUdfObject> objects, string fixedObject,
        CancellationToken cancellationToken)
    {
        var commit = fixedObject;
        var root = Object(objects, commit);
        for (var depth = 0; root.Type == 4; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth >= 16) throw GitUdfCatalogSource.Invalid();
            var target = FirstHash(root.Content, "object ");
            var newline = root.Content.AsSpan().IndexOf((byte)'\n');
            var remainder = root.Content.AsSpan(newline + 1);
            var nextLine = remainder.IndexOf((byte)'\n');
            if (nextLine < 0) throw GitUdfCatalogSource.Invalid();
            var type = Encoding.ASCII.GetString(remainder[..nextLine]);
            root = Object(objects, target);
            if (type != (root.Type == 1 ? "type commit" : root.Type == 4 ? "type tag" : "")) throw GitUdfCatalogSource.Invalid();
            commit = target;
        }
        if (root.Type != 1) throw GitUdfCatalogSource.Invalid();
        var tree = FirstHash(root.Content, "tree ");
        var pending = new Queue<(string Hash, string Prefix, int Depth)>();
        pending.Enqueue((tree, "", 0));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GitUdfFile>();
        long total = 0;
        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Object(objects, item.Hash);
            if (directory.Type != 2 || item.Depth > 64) throw GitUdfCatalogSource.Invalid();
            var bytes = directory.Content;
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var space = bytes.AsSpan(offset).IndexOf((byte)' ');
                if (space is < 1 or > 6) throw GitUdfCatalogSource.Invalid();
                var mode = Encoding.ASCII.GetString(bytes, offset, space); offset += space + 1;
                var nul = bytes.AsSpan(offset).IndexOf((byte)0);
                if (nul is < 1 or > 1024 || bytes.Length - offset - nul - 1 < 20) throw GitUdfCatalogSource.Invalid();
                string name;
                try { name = Utf8.GetString(bytes, offset, nul); }
                catch (DecoderFallbackException) { throw GitUdfCatalogSource.Invalid(); }
                if (name.Contains('/')) throw GitUdfCatalogSource.Invalid();
                offset += nul + 1;
                var hash = Convert.ToHexStringLower(bytes.AsSpan(offset, 20)); offset += 20;
                var path = item.Prefix + name;
                UdfRepositoryContent.ValidatePath(path);
                if (paths.Count >= GitUdfFetch.MaxEntries || !paths.Add(path)) throw GitUdfCatalogSource.Invalid();
                var target = Object(objects, hash);
                if (mode is "40000" or "040000")
                {
                    if (target.Type != 2) throw GitUdfCatalogSource.Invalid();
                    pending.Enqueue((hash, path + "/", item.Depth + 1)); continue;
                }
                if (mode is not ("100644" or "100755") || target.Type != 3 || GitUdfCatalogSource.Relevant(path) && mode != "100644"
                    || target.Content.Length > DaxLibPackageReader.MaxFileBytes || (total += target.Content.LongLength) > GitUdfFetch.MaxContentBytes
                    || UdfRepositoryContent.IsLfsPointer(target.Content)) throw GitUdfCatalogSource.Invalid();
                if (GitUdfCatalogSource.Relevant(path)) result.Add(new(path, mode, target.Content));
            }
        }
        if (result.Count == 0) throw GitUdfCatalogSource.Invalid();
        return (result, commit, tree);
    }

    /// <summary>首行必须是精确对象头, 不将消息正文或空白容错转换为另一个树/目标.</summary>
    private static string FirstHash(byte[] content, string prefix)
    {
        var newline = content.AsSpan().IndexOf((byte)'\n');
        if (newline != prefix.Length + 40 || !content.AsSpan(0, prefix.Length).SequenceEqual(Encoding.ASCII.GetBytes(prefix))) throw GitUdfCatalogSource.Invalid();
        var value = Encoding.ASCII.GetString(content, prefix.Length, 40);
        return GitSmartUdfProtocol.IsHash(value) ? value.ToLowerInvariant() : throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>所有基础、标签、commit、tree 和 blob 都须包含在已验证 PACK 中.</summary>
    private static GitSmartUdfObject Object(IReadOnlyDictionary<string, GitSmartUdfObject> objects, string hash) => objects.TryGetValue(hash, out var value)
        ? value : throw GitUdfCatalogSource.Invalid();
}
