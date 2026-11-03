// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitHub\GitHubUdfZipDirectory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在 .NET 物化 ZIP 条目前有界核对标准 EOCD、中央目录数量及记录边界.

using System.Buffers.Binary;

using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.GitHub;

/// <summary>只支持普通单磁盘 ZIP, 无分配扫描目录, 不让伪造数量绕过条目预算.</summary>
internal static class GitHubUdfZipDirectory
{
    /// <summary>先核对真实中央目录数量与范围再返回数量; ZIP64、多磁盘、截断或取消明确失败.</summary>
    internal static int Check(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length < 22 || bytes.LongLength > GitUdfFetch.MaxContentBytes) throw GitUdfCatalogSource.Invalid();
        var end = FindEnd(bytes, cancellationToken);
        var count = U16(bytes, end + 10);
        var size = U32(bytes, end + 12);
        var offset = U32(bytes, end + 16);
        if (U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0 || U16(bytes, end + 8) != count ||
            count is 0 or > GitUdfFetch.MaxEntries + 1 || size == uint.MaxValue || offset == uint.MaxValue ||
            (long)offset + size != end || end >= 20 && U32(bytes, end - 20) == 0x07064b50) throw GitUdfCatalogSource.Invalid();
        var position = (int)offset;
        var actual = 0;
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++actual > count || end - position < 46 || U32(bytes, position) != 0x02014b50) throw GitUdfCatalogSource.Invalid();
            var nameLength = U16(bytes, position + 28);
            var extraLength = U16(bytes, position + 30);
            var commentLength = U16(bytes, position + 32);
            var recordLength = 46 + nameLength + extraLength + commentLength;
            var local = U32(bytes, position + 42);
            if (U16(bytes, position + 6) >= 45 || U16(bytes, position + 34) != 0 || nameLength is 0 or > 2048 ||
                U32(bytes, position + 20) == uint.MaxValue || U32(bytes, position + 24) == uint.MaxValue ||
                local == uint.MaxValue || local > offset || offset - local < 30 || recordLength > end - position ||
                U32(bytes, (int)local) != 0x04034b50) throw GitUdfCatalogSource.Invalid();
            CheckExtra(bytes, position + 46 + nameLength, extraLength, cancellationToken);
            position += recordLength;
        }
        if (actual != count || position != end) throw GitUdfCatalogSource.Invalid();
        cancellationToken.ThrowIfCancellationRequested();
        return actual;
    }

    /// <summary>EOCD 只可能位于末尾 65535 字节注释之前, 不扫描整个压缩包或忽略尾随内容.</summary>
    private static int FindEnd(byte[] bytes, CancellationToken cancellationToken)
    {
        var minimum = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        for (var offset = bytes.Length - 22; offset >= minimum; offset--)
        {
            if ((offset & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (U32(bytes, offset) != 0x06054b50) continue;
            // 与 .NET 一样选择倒序遇到的首个签名, 不可跳过坏候选再验证另一个目录.
            if (offset + 22 + U16(bytes, offset + 20) != bytes.Length) throw GitUdfCatalogSource.Invalid();
            return offset;
        }
        throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>扩展字段按自己的有界长度前进; ZIP64 字段不能用普通 EOCD 数量掩盖.</summary>
    private static void CheckExtra(byte[] bytes, int position, int length, CancellationToken cancellationToken)
    {
        var end = position + length;
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - position < 4) throw GitUdfCatalogSource.Invalid();
            var kind = U16(bytes, position);
            var size = U16(bytes, position + 2);
            if (kind == 1 || size > end - position - 4) throw GitUdfCatalogSource.Invalid();
            position += 4 + size;
        }
    }

    /// <summary>调用方先验证记录固定长度, 小端读取不分配临时缓冲区.</summary>
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    /// <summary>调用方先验证记录固定长度, 小端读取不分配临时缓冲区.</summary>
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
