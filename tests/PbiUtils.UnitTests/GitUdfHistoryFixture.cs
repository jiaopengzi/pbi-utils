// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitUdfHistoryFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 构造独立 SHA1 Git 历史对象与无 blob PACK, 供目录提交时间边界验证.

using PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Infrastructure.Udf;

namespace PbiUtils.UnitTests;

/// <summary>对象构造不调用产品解析器; 文件可来自完整快照或仅历史树.</summary>
internal sealed class GitUdfHistoryFixture
{
    internal Dictionary<string, GitSmartUdfObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>按 Git 目录排序计算整棵树, 包内任意文件均影响目录对象 ID.</summary>
    internal string Tree(IReadOnlyDictionary<string, byte[]> files, string prefix = "")
    {
        using var content = new MemoryStream();
        foreach (var name in files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name + (files.ContainsKey(prefix + name) ? "" : "/"), StringComparer.Ordinal))
        {
            var path = prefix + name;
            var directory = !files.ContainsKey(path);
            var hash = directory ? Tree(files, path + "/") : Add(3, files[path]);
            content.Write(Encoding.UTF8.GetBytes((directory ? "40000" : "100644") + " " + name + "\0"));
            content.Write(Convert.FromHexString(hash));
        }
        return Add(2, content.ToArray());
    }

    /// <summary>提交时间与作者时间可独立设置, parent 顺序不重排.</summary>
    internal string Commit(string tree, long seconds, string[]? parents = null, long authorSeconds = 1)
        => Add(1, Encoding.UTF8.GetBytes($"tree {tree}\n" + string.Concat((parents ?? []).Select(parent => $"parent {parent}\n"))
            + $"author Fixture <fixture@example.test> {authorSeconds} +0800\ncommitter Fixture <fixture@example.test> {seconds} -0700\n\nFixture\n"));

    /// <summary>SHA1 包含对象类型和字节数; 不同提交/树均有独立身份.</summary>
    internal string Add(int type, byte[] content)
    {
        var name = type switch { 1 => "commit", 2 => "tree", 3 => "blob", _ => "tag" };
        var hash = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes(name + " " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0"), .. content]));
        Objects[hash] = new(type, content, 0);
        return hash;
    }

    /// <summary>标准 PACK 可过滤 blob, 仍保留所有父提交与树以独立验证时间.</summary>
    internal byte[] Pack(bool blobs = false)
    {
        using var output = new MemoryStream(); output.Write("PACK"u8);
        var entries = Objects.Values.Where(value => blobs || value.Type != 3).ToArray();
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(integer, 2); output.Write(integer);
        BinaryPrimitives.WriteInt32BigEndian(integer, entries.Length); output.Write(integer);
        foreach (var entry in entries)
        {
            var size = entry.Content.Length;
            output.WriteByte((byte)((entry.Type << 4) | (size & 15) | (size > 15 ? 128 : 0))); size >>= 4;
            while (size > 0) { var next = size & 127; size >>= 7; output.WriteByte((byte)(next | (size > 0 ? 128 : 0))); }
            using var compressed = new ZLibStream(output, CompressionLevel.Optimal, true); compressed.Write(entry.Content);
        }
        output.Write(SHA1.HashData(output.ToArray())); return output.ToArray();
    }
}
