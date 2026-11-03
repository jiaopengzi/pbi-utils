// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfDelta.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 有界执行 Git pack delta 的复制与插入指令, 不包含仓库代码执行能力.

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>delta 只能从准确基础对象复制字节或插入已下载字节, 每条指令检查范围与取消.</summary>
internal static class GitSmartUdfDelta
{
    /// <summary>验证基础/结果声明长度, 拒绝越界复制、零操作码、超量展开和残缺指令.</summary>
    internal static byte[] Apply(byte[] basis, byte[] delta, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (Variable(delta, ref offset) != basis.Length) throw GitUdfCatalogSource.Invalid();
        var length = Variable(delta, ref offset);
        if (length > DaxLibPackageReader.MaxFileBytes) throw GitUdfCatalogSource.Invalid();
        var result = new byte[length]; var written = 0;
        while (offset < delta.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = Byte(delta, ref offset);
            if (operation == 0) throw GitUdfCatalogSource.Invalid();
            if ((operation & 128) == 0)
            {
                if (operation > delta.Length - offset || operation > result.Length - written) throw GitUdfCatalogSource.Invalid();
                delta.AsSpan(offset, operation).CopyTo(result.AsSpan(written)); offset += operation; written += operation;
                continue;
            }
            long copyOffset = 0; var size = 0;
            for (var index = 0; index < 4; index++)
                if ((operation & (1 << index)) != 0) copyOffset |= (long)Byte(delta, ref offset) << (index * 8);
            for (var index = 0; index < 3; index++)
                if ((operation & (16 << index)) != 0) size |= Byte(delta, ref offset) << (index * 8);
            if (size == 0) size = 65536;
            if (copyOffset > basis.Length || size > basis.Length - copyOffset || size > result.Length - written) throw GitUdfCatalogSource.Invalid();
            basis.AsSpan((int)copyOffset, size).CopyTo(result.AsSpan(written)); written += size;
        }
        if (written != result.Length) throw GitUdfCatalogSource.Invalid();
        return result;
    }

    /// <summary>delta 长度是 little-endian 7-bit 无符号整数, 在分配前限制到 int 范围.</summary>
    private static int Variable(byte[] bytes, ref int offset)
    {
        long value = 0; var shift = 0;
        byte next;
        do
        {
            if (shift > 28) throw GitUdfCatalogSource.Invalid();
            next = Byte(bytes, ref offset); value |= (long)(next & 127) << shift; shift += 7;
        } while ((next & 128) != 0);
        return value <= int.MaxValue ? (int)value : throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>残缺 delta 指令不能从数组外读取或默认为零.</summary>
    private static byte Byte(byte[] bytes, ref int offset) => offset < bytes.Length ? bytes[offset++] : throw GitUdfCatalogSource.Invalid();
}
