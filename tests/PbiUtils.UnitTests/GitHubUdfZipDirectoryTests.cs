// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitHubUdfZipDirectoryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证归档在 .NET 构造中央目录对象前拒绝伪造数量、ZIP64 和截断结构.

using PbiUtils.Infrastructure.Udf.Repositories.GitHub;
using PbiUtils.Infrastructure.Udf.Repositories;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用真实小归档修改目录元数据, 不需要分配攻击规模的条目对象.</summary>
public sealed class GitHubUdfZipDirectoryTests
{
    /// <summary>EOCD 声明不能小于或大于实际中央目录数量, 也不能绕过硬上限或使用 ZIP64 标记.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(20002)]
    [InlineData(65535)]
    public void RejectsForgedEocdCount(int count)
    {
        var bytes = Archive();
        var end = bytes.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8, 2), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10, 2), (ushort)count);
        Assert.Throws<AppException>(() => Read(bytes));
    }

    /// <summary>正常单文件 ZIP 与合法注释可用, 目录核对不得拒绝普通官方归档格式.</summary>
    [Fact]
    public void AcceptsStandardArchiveWithComment()
    {
        var bytes = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2, 2), 3);
        Assert.Single(Read([.. bytes, 1, 2, 3]));
    }

    /// <summary>注释中的较晚伪 EOCD 不能让预检与 .NET 选择不同的中央目录.</summary>
    [Fact]
    public void RejectsLaterEocdSignatureWithInvalidCommentLength()
    {
        var bytes = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2, 2), 22);
        var fake = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(fake, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(fake.AsSpan(20, 2), 1);
        Assert.Throws<AppException>(() => GitHubUdfZipDirectory.Check([.. bytes, .. fake], default));
    }

    /// <summary>多磁盘、ZIP64、越界目录范围及截断的中央记录必须在 .NET 目录物化前失败.</summary>
    [Theory]
    [InlineData("disk")]
    [InlineData("directory-disk")]
    [InlineData("disk-count")]
    [InlineData("zip64-offset")]
    [InlineData("zip64-size")]
    [InlineData("zip64-record")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("record-length")]
    [InlineData("record-signature")]
    [InlineData("truncated-eocd")]
    public void RejectsUnsupportedOrTruncatedDirectory(string fault)
    {
        var bytes = Archive();
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4));
        switch (fault)
        {
            case "disk": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 4, 2), 1); break;
            case "directory-disk": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 6, 2), 1); break;
            case "disk-count": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8, 2), 0); break;
            case "zip64-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16, 4), uint.MaxValue); break;
            case "zip64-size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12, 4), uint.MaxValue); break;
            case "zip64-record": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 6, 2), 45); break;
            case "offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16, 4), (uint)bytes.Length); break;
            case "size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12, 4), (uint)bytes.Length); break;
            case "record-length": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 28, 2), ushort.MaxValue); break;
            case "record-signature": bytes[central] = 0; break;
            case "truncated-eocd": bytes = bytes[..^1]; break;
        }
        Assert.Throws<AppException>(() => Read(bytes));
    }

    /// <summary>真实超量成员归档先受 EOCD 上限约束, 不先创建所有 ZipArchiveEntry.</summary>
    [Fact]
    public void RejectsManyTinyEntries()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            for (var index = 0; index < GitUdfFetch.MaxEntries + 2; index++) archive.CreateEntry("repository-commit/" + index);
        Assert.Throws<AppException>(() => GitHubUdfZipDirectory.Check(output.ToArray(), default));
    }

    /// <summary>非零的小计数不能让扫描器忽略后续中央记录并交给框架一次性物化.</summary>
    [Fact]
    public void RejectsActualCountAboveForgedNonzeroCount()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        { archive.CreateEntry("repository-commit/LICENSE"); archive.CreateEntry("repository-commit/NOTICE"); }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10, 2), 1);
        Assert.Throws<AppException>(() => GitHubUdfZipDirectory.Check(bytes, default));
    }

    /// <summary>即使普通 EOCD 未使用 ZIP64 哨兵, 隐藏的 ZIP64 扩展和截断扩展也明确拒绝.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsZip64AndTruncatedExtraFields(bool zip64)
    {
        var original = Archive();
        var end = original.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16, 4));
        byte[] extra = zip64 ? [1, 0, 0, 0] : [2, 0, 1];
        var bytes = new byte[original.Length + extra.Length];
        original.AsSpan(0, end).CopyTo(bytes);
        extra.CopyTo(bytes, end);
        original.AsSpan(end).CopyTo(bytes.AsSpan(end + extra.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 30, 2), (ushort)extra.Length);
        var newEnd = end + extra.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(newEnd + 12, 4), (uint)(newEnd - central));
        Assert.Throws<AppException>(() => GitHubUdfZipDirectory.Check(bytes, default));
    }

    /// <summary>预取消在解析 EOCD 或任何 .NET 目录读取前生效.</summary>
    [Fact]
    public void HonorsCancellationBeforeDirectoryMaterialization() => Assert.ThrowsAny<OperationCanceledException>(() => Read(Archive(), new CancellationToken(true)));

    /// <summary>生成一个普通许可文件作为结构修改前的完整有效基线.</summary>
    private static byte[] Archive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        { using var stream = archive.CreateEntry("repository-commit/LICENSE").Open(); stream.Write("MIT"u8); }
        return output.ToArray();
    }

    /// <summary>正式归档读取器核对完整文件集和真实 Git blob, 不以虚构哈希绕过解析.</summary>
    private static IReadOnlyList<GitUdfFile> Read(byte[] bytes, CancellationToken cancellationToken = default) =>
        GitHubUdfArchive.Read(bytes, [new("LICENSE", "100644", Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes("blob 3\0MIT"))), 3)], "repository-commit/", cancellationToken);
}
