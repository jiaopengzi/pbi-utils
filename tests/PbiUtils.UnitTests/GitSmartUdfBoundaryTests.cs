// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitSmartUdfBoundaryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 针对 Git pkt-line、浅获取、压缩流和 delta 指令的相邻安全边界回归.

using PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;
using PbiUtils.Infrastructure.Udf.Packages;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>字节级恶意协议输入不得返回部分对象或发生越界分配.</summary>
public sealed class GitSmartUdfBoundaryTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>缺失最终 flush 即使每个引用有效仍须拒绝, 防止截断广告被接受.</summary>
    [Fact]
    public void RejectsAdvertisementWithoutFinalFlush() => Assert.Throws<AppException>(() => GitSmartUdfProtocol.Resolve(Advertisement(finalFlush: false), null));

    /// <summary>默认分支来自 symref 并核对 HEAD, 不猜测 main/master; 没有 HEAD 也可用明确 symref.</summary>
    [Fact]
    public void ResolvesAdvertisedDefaultAndRejectsConflictingHead()
    {
        var resolved = GitSmartUdfProtocol.Resolve(Advertisement(), null);
        Assert.Equal(Hash, resolved.Hash);
        Assert.Throws<AppException>(() => GitSmartUdfProtocol.Resolve(Advertisement(head: Other), null));
        var custom = Advertisement(branch: "release");
        Assert.Equal(Hash, GitSmartUdfProtocol.Resolve(custom, null).Hash);
    }

    /// <summary>同名分支与标签必须显式区分, 精确对象 ID 保持唯一固定身份.</summary>
    [Fact]
    public void RejectsAmbiguousShortRefAndPinsExactObject()
    {
        var bytes = Advertisement(extra: [.. GitSmartUdfFixture.Packet(Other + " refs/tags/main\n")]);
        Assert.Throws<AppException>(() => GitSmartUdfProtocol.Resolve(bytes, "main"));
        Assert.Equal(Hash, GitSmartUdfProtocol.Resolve(bytes, "refs/heads/main").Hash);
        Assert.Equal(Other, GitSmartUdfProtocol.Resolve(bytes, "refs/tags/main").Hash);
        Assert.Equal(Other, GitSmartUdfProtocol.Resolve(bytes, Other.ToUpperInvariant()).Hash);
    }

    /// <summary>能力协商拒绝 SHA-256 和不支持 shallow 的服务端, 不退回完整历史下载.</summary>
    [Theory]
    [InlineData("ofs-delta")]
    [InlineData("shallow object-format=sha256")]
    public void RejectsUnsupportedObjectFormatOrMissingShallow(string capabilities) =>
        Assert.Throws<AppException>(() => GitSmartUdfProtocol.Resolve(Advertisement(capabilities: capabilities), null));

    /// <summary>pkt-line 特殊控制码、非法十六进制和声明截断必须失败.</summary>
    [Theory]
    [InlineData("zzzz")]
    [InlineData("0001")]
    [InlineData("0002")]
    [InlineData("ffffx")]
    [InlineData("0008x")]
    public void RejectsMalformedPacketLength(string value)
    {
        var offset = 0;
        Assert.Throws<AppException>(() => ReadPacket(Encoding.ASCII.GetBytes(value), ref offset));
    }

    /// <summary>未协商的 side-band、ACK、远端错误正文以及缺失 NAK 不交给 PACK 解析器.</summary>
    [Theory]
    [InlineData("ACK aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("ERR private server details\n")]
    [InlineData("\u0001PACK")]
    public void RejectsUnexpectedUploadPackNegotiation(string line) => Assert.Throws<AppException>(() => GitSmartUdfProtocol.ReadPack(GitSmartUdfFixture.Packet(line)));

    /// <summary>零操作码、复制越界、错误基础长度和未填满结果均安全失败.</summary>
    [Theory]
    [InlineData(new byte[] { 1, 1, 0 })]
    [InlineData(new byte[] { 1, 1, 0x91, 2, 1 })]
    [InlineData(new byte[] { 2, 1, 1, 65 })]
    [InlineData(new byte[] { 1, 2, 1, 65 })]
    [InlineData(new byte[] { 1, 1, 0x80 })]
    public void RejectsMalformedDelta(byte[] delta) => Assert.Throws<AppException>(() => GitSmartUdfDelta.Apply([65], delta, default));

    /// <summary>复制偏移、复制长度和插入字节保持正确次序, 不是只测试纯插入的 delta.</summary>
    [Fact]
    public void AppliesCopyAndInsertDelta() => Assert.Equal("bcd!", Encoding.ASCII.GetString(GitSmartUdfDelta.Apply("abcde"u8.ToArray(), [5, 4, 0x91, 1, 3, 1, 33], default)));

    /// <summary>PACK 校验和正确也不能接受缺失 zlib trailer 或极端压缩炸弹.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectsTruncatedZlibAndCompressionBomb(bool truncated, bool bomb)
    {
        var bytes = Pack(bomb ? new byte[1024 * 1024] : "a blob payload"u8.ToArray(), truncate: truncated);
        Assert.Throws<AppException>(() => GitSmartUdfPack.Read(bytes, default));
    }

    /// <summary>对象头声称超过单文件上限时, 分配及 zlib 展开前失败.</summary>
    [Fact]
    public void RejectsOversizedObjectBeforeInflating() => Assert.Throws<AppException>(() => GitSmartUdfPack.Read(Pack([], claimedLength: DaxLibPackageReader.MaxFileBytes + 1), default));

    /// <summary>未开始解析即可响应取消, 不将取消转换成损坏来源.</summary>
    [Fact]
    public void PackPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => GitSmartUdfPack.Read([], cancellation.Token));
    }

    /// <summary>构造独立广告输入, 不读取被测代码的序列化结果.</summary>
    private static byte[] Advertisement(bool finalFlush = true, string head = Hash, string branch = "main", string capabilities = "shallow ofs-delta", byte[]? extra = null) =>
        [.. GitSmartUdfFixture.Packet("# service=git-upload-pack\n"), .. "0000"u8,
            .. GitSmartUdfFixture.Packet(head + " HEAD\0" + capabilities + " symref=HEAD:refs/heads/" + branch + "\n"),
            .. GitSmartUdfFixture.Packet(Hash + " refs/heads/" + branch + "\n"), .. extra ?? [], .. (finalFlush ? "0000"u8.ToArray() : [])];

    /// <summary>void 包装避免将 ref struct span 从异常断言委托返回.</summary>
    private static void ReadPacket(byte[] bytes, ref int offset) => _ = GitSmartUdfProtocol.Packet(bytes, ref offset);

    /// <summary>生成单 blob PACK 并可在重算总校验和前截断独立 zlib, 区分两层完整性.</summary>
    private static byte[] Pack(byte[] content, bool truncate = false, int? claimedLength = null)
    {
        using var output = new MemoryStream(); output.Write("PACK"u8);
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(integer, 2); output.Write(integer);
        BinaryPrimitives.WriteInt32BigEndian(integer, 1); output.Write(integer);
        var size = claimedLength ?? content.Length; var first = 0x30 | (size & 15); size >>= 4;
        output.WriteByte((byte)(first | (size > 0 ? 128 : 0)));
        while (size > 0) { var part = size & 127; size >>= 7; output.WriteByte((byte)(part | (size > 0 ? 128 : 0))); }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(content);
        output.Write(truncate ? compressed.ToArray()[..^4] : compressed.ToArray());
        output.Write(SHA1.HashData(output.ToArray())); return output.ToArray();
    }
}
