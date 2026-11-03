// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfZlibInput.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 限制 zlib 单次输入为一字节, 确保 PACK 对象的压缩边界可准确计数.

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>ZLibStream 不公开未消费输入, 因此禁止预读跨越当前对象, 不使用反射或原生依赖.</summary>
internal sealed class GitSmartUdfZlibInput(byte[] bytes, int start, int end, CancellationToken cancellationToken) : Stream
{
    internal int Consumed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => end - start;
    public override long Position { get => Consumed; set => throw new NotSupportedException(); }

    /// <summary>每次最多提供一个字节并检查取消, 到 PACK 尾部前停止.</summary>
    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0 || start + Consumed >= end) return 0;
        buffer[0] = bytes[start + Consumed++]; return 1;
    }

    /// <summary>兼容 Stream 的数组读取入口, 保留同一单字节边界.</summary>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <summary>该内存输入流只读, Flush 无待写入状态.</summary>
    public override void Flush() { }
    /// <summary>禁止改变已验证的流位置.</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>输入流不提供修改能力.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>输入流不提供写入能力.</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
