// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfPack.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在内存验证 Git PACK 校验和、独立 zlib 对象、delta 依赖和全部对象 SHA-1.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>不接受 thin pack; 所有基础对象必须存在, 原始及 delta 展开均有独立累计预算.</summary>
internal static class GitSmartUdfPack
{
    internal const int MaxDeltaDepth = 64;

    /// <summary>验证 PACK v2/v3 和 SHA-1 尾部, 展开 ofs/ref delta 后返回按真实对象 ID 索引的只读对象.</summary>
    internal static IReadOnlyDictionary<string, GitSmartUdfObject> Read(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is < 32 || bytes.LongLength > GitUdfFetch.MaxContentBytes || !bytes.AsSpan(0, 4).SequenceEqual("PACK"u8)
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4)) is not (2 or 3)
            || !SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 20))) throw GitUdfCatalogSource.Invalid();
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        if (count is 0 or > GitUdfFetch.MaxEntries * 2 + 32) throw GitUdfCatalogSource.Invalid();
        var entries = new Dictionary<int, Packed>();
        var objects = new Dictionary<string, GitSmartUdfObject>(StringComparer.Ordinal);
        var offset = 12;
        long rawTotal = 0, resolvedTotal = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = offset;
            var first = Byte(bytes, ref offset);
            var type = (first >> 4) & 7;
            long length = first & 15;
            var shift = 4;
            var next = first;
            while ((next & 128) != 0)
            {
                if (shift > 25) throw GitUdfCatalogSource.Invalid();
                next = Byte(bytes, ref offset); length |= (long)(next & 127) << shift; shift += 7;
            }
            if (type is not (1 or 2 or 3 or 4 or 6 or 7) || length > DaxLibPackageReader.MaxFileBytes || (rawTotal += length) > GitUdfFetch.MaxContentBytes) throw GitUdfCatalogSource.Invalid();
            string? baseHash = null;
            var baseOffset = -1;
            if (type == 6)
            {
                var encoded = Byte(bytes, ref offset); long distance = encoded & 127;
                while ((encoded & 128) != 0)
                {
                    if (distance > bytes.Length) throw GitUdfCatalogSource.Invalid();
                    encoded = Byte(bytes, ref offset); distance = ((distance + 1) << 7) | (uint)(encoded & 127);
                }
                if (distance < 1 || distance > start - 12) throw GitUdfCatalogSource.Invalid();
                baseOffset = start - (int)distance;
                if (!entries.ContainsKey(baseOffset)) throw GitUdfCatalogSource.Invalid();
            }
            else if (type == 7)
            {
                if (offset + 20 > bytes.Length - 20) throw GitUdfCatalogSource.Invalid();
                baseHash = Convert.ToHexStringLower(bytes.AsSpan(offset, 20)); offset += 20;
            }
            var content = Inflate(bytes, ref offset, (int)length, cancellationToken);
            var entry = new Packed(type, content, baseOffset, baseHash);
            if (type <= 4) entry.Resolved = Publish(type, content, 0, objects, ref resolvedTotal);
            entries.Add(start, entry);
        }
        if (offset != bytes.Length - 20) throw GitUdfCatalogSource.Invalid();
        var pending = entries.Values.Where(entry => entry.Resolved is null).ToList();
        for (var pass = 0; pending.Count > 0 && pass <= MaxDeltaDepth; pass++)
        {
            var resolved = 0;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = pending[index];
                var basis = entry.BaseHash is { } hash ? objects.GetValueOrDefault(hash) : entries[entry.BaseOffset].Resolved;
                if (basis is null) continue;
                if (basis.Depth >= MaxDeltaDepth) throw GitUdfCatalogSource.Invalid();
                var content = GitSmartUdfDelta.Apply(basis.Content, entry.Content, cancellationToken);
                entry.Resolved = Publish(basis.Type, content, basis.Depth + 1, objects, ref resolvedTotal);
                pending.RemoveAt(index); resolved++;
            }
            if (resolved == 0) throw GitUdfCatalogSource.Invalid();
        }
        if (pending.Count != 0) throw GitUdfCatalogSource.Invalid();
        return objects;
    }

    /// <summary>对象寻址包含类型和精确长度; 重复对象拒绝, delta 结果也计入完整展开预算.</summary>
    private static GitSmartUdfObject Publish(int type, byte[] bytes, int depth, Dictionary<string, GitSmartUdfObject> objects, ref long total)
    {
        if ((total += bytes.LongLength) > GitUdfFetch.MaxContentBytes) throw GitUdfCatalogSource.Invalid();
        var name = type switch { 1 => "commit", 2 => "tree", 3 => "blob", 4 => "tag", _ => throw GitUdfCatalogSource.Invalid() };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes(name + " " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0")); hash.AppendData(bytes);
        var identity = Convert.ToHexStringLower(hash.GetHashAndReset());
        var result = new GitSmartUdfObject(type, bytes, depth);
        if (!objects.TryAdd(identity, result)) throw GitUdfCatalogSource.Invalid();
        return result;
    }

    /// <summary>单字节输入避免 ZLibStream 预读下一个对象; 精确长度、压缩比和 Adler32 均由读取边界核验.</summary>
    private static byte[] Inflate(byte[] bytes, ref int offset, int expected, CancellationToken cancellationToken)
    {
        using var input = new GitSmartUdfZlibInput(bytes, offset, bytes.Length - 20, cancellationToken);
        try
        {
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, true);
            var result = new byte[expected]; var written = 0;
            while (written < expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = zlib.Read(result.AsSpan(written));
                if (read == 0) throw GitUdfCatalogSource.Invalid();
                written += read;
            }
            if (zlib.ReadByte() != -1 || input.Consumed < 6 || expected > (long)input.Consumed * 200
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + input.Consumed - 4, 4)) != Adler32(result, cancellationToken)) throw GitUdfCatalogSource.Invalid();
            offset += input.Consumed;
            return result;
        }
        catch (InvalidDataException) { throw GitUdfCatalogSource.Invalid(); }
    }

    /// <summary>.NET 解码器可对缺失 trailer 返回 EOF, 因此独立验证 zlib Adler32, 分块避免整数溢出.</summary>
    private static uint Adler32(byte[] bytes, CancellationToken cancellationToken)
    {
        uint first = 1, second = 0;
        for (var offset = 0; offset < bytes.Length; offset += 5552)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Min(bytes.Length, offset + 5552);
            for (var index = offset; index < limit; index++) { first += bytes[index]; second += first; }
            first %= 65521; second %= 65521;
        }
        return (second << 16) | first;
    }

    /// <summary>读包元数据时始终保留尾部校验和, 截断不得误作后续对象.</summary>
    private static byte Byte(byte[] bytes, ref int offset) => offset < bytes.Length - 20 ? bytes[offset++] : throw GitUdfCatalogSource.Invalid();

    /// <summary>尚未展开的对象只持有有界 delta 程序与准确基础身份.</summary>
    private sealed class Packed(int type, byte[] content, int baseOffset, string? baseHash)
    {
        internal int Type { get; } = type;
        internal byte[] Content { get; } = content;
        internal int BaseOffset { get; } = baseOffset;
        internal string? BaseHash { get; } = baseHash;
        internal GitSmartUdfObject? Resolved { get; set; }
    }
}

/// <summary>经 pack 校验和及 Git 对象指纹验证的内存对象, 深度约束 delta 链.</summary>
internal sealed record GitSmartUdfObject(int Type, byte[] Content, int Depth);
