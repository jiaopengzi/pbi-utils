// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessHostPeerDetachTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用受控双向传输固定远端主动关闭和本地 detach 写入的竞态, 保留真实排空终态.

using PbiUtils.Core;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>不使用延时或真实进程, 明确控制已在入站缓冲的终态何时可读.</summary>
public sealed class BusinessHostPeerDetachTests
{
    /// <summary>detach 写失败且对端一直不结束入站时必须有界失败, 正常业务排空不受该故障期限影响.</summary>
    [Fact]
    public async Task ClosedDetachWriteWithStalledReaderFailsWithinBound()
    {
        using var stream = new ClosedWriteStream([]);
        await using var peer = new BusinessHostPeer(stream, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, null);
        peer.Start();
        var detaching = peer.DetachAsync();
        try
        {
            await stream.WriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // 永不放开 read 门闩, 验证故障路径自身有界; 外层 15 秒只防止红测挂住测试进程.
            Assert.Equal("BUSINESS_HOST_CONNECTION_LOST", (await Assert.ThrowsAsync<AppException>(
                () => detaching.WaitAsync(TimeSpan.FromSeconds(15)))).Code);
            Assert.True(peer.Completion.IsCompleted);
        }
        finally
        {
            await peer.DisposeAsync();
            try { await detaching; } catch (AppException) { }
        }
    }

    /// <summary>关闭控制帧写失败不能丢弃已发送的正常/失败终态; 没有终态的 EOF 仍明确失败.</summary>
    [Theory]
    [InlineData("success", null)]
    [InlineData("failure", "BUSINESS_HOST_DRAIN_FAILED")]
    [InlineData("eof", "BUSINESS_HOST_CONNECTION_LOST")]
    public async Task ClosedDetachWriteStillObservesRemoteDrainOutcome(string outcome, string? expectedError)
    {
        var generation = Guid.NewGuid().ToString("N");
        var context = Guid.NewGuid().ToString("N");
        using var buffer = new MemoryStream();
        if (outcome != "eof")
            await BusinessHostProtocol.WriteAsync(buffer, new BusinessHostFrame("detached", generation, context,
                Error: outcome == "failure" ? new("BUSINESS_HOST_DRAIN_FAILED", "确定性测试排空失败", 4) : null), default);
        using var stream = new ClosedWriteStream(buffer.ToArray());
        await using var peer = new BusinessHostPeer(stream, generation, context, null, null);
        peer.Start();
        var detaching = peer.DetachAsync();
        await stream.WriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // 写失败已经发生, reader 此前被门闩阻止; 现在才交付远端已发送的 detached 或真正 EOF.
        stream.ReleaseReads();
        if (expectedError is null) await detaching.WaitAsync(TimeSpan.FromSeconds(2));
        else Assert.Equal(expectedError, (await Assert.ThrowsAsync<AppException>(() => detaching)).Code);
        if (outcome != "eof")
        {
            if (expectedError is null) await peer.DetachAsync();
            else Assert.Equal(expectedError, (await Assert.ThrowsAsync<AppException>(() => peer.DetachAsync())).Code);
            Assert.Equal(1, stream.WriteAttempts);
        }
    }

    /// <summary>模拟服务端已关闭写方向, 入站终态仍等待本地 reader 消费; Dispose 不制造成功.</summary>
    private sealed class ClosedWriteStream(byte[] response) : Stream
    {
        private readonly TaskCompletionSource readAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream input = new(response);
        internal TaskCompletionSource WriteAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteAttempts { get; private set; }
        internal void ReleaseReads() => readAllowed.TrySetResult();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>门闩交付缓冲帧; 若产品提前 FailTransport 取消 reader, 测试如实观察丢失终态.</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await readAllowed.Task.WaitAsync(cancellationToken);
            return await input.ReadAsync(buffer, cancellationToken);
        }

        /// <summary>精确模拟对已关闭远端发送 detach 时的写失败, 不提前放开入站读取.</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { WriteAttempts++; WriteAttempted.TrySetResult(); return ValueTask.FromException(new IOException("closed-write-test")); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
