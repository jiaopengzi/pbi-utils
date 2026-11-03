// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessHostProtocolTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享后台传输容量、恶意帧预算和严格字段, 不提升业务输入上限.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class BusinessHostProtocolTests
{
    /// <summary>原core MCP可返回16MiB值, 不能被旧桌面IPC的4MiB上限截断.</summary>
    [Fact]
    public async Task LargeStructuredResultRetainsOriginalCapacity()
    {
        var original = new string('x', 16 * 1024 * 1024 - 1024);
        using var stream = new MemoryStream();
        await BusinessHostProtocol.WriteAsync(stream, new BusinessHostFrame("result", Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Data: JsonSerializer.SerializeToElement(original)), default);
        stream.Position = 0;
        var result = await BusinessHostProtocol.ReadAsync<BusinessHostFrame>(stream, default);
        Assert.Equal(original, result.Data!.Value.GetString());
    }

    /// <summary>非法长度在分配正文前拒绝, 不退化为等候无限数据.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(20971521)]
    public async Task InvalidLengthFailsBeforeReadingBody(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        Assert.Equal("BUSINESS_HOST_PROTOCOL_INVALID", (await Assert.ThrowsAsync<AppException>(() =>
            BusinessHostProtocol.ReadAsync<BusinessHostFrame>(stream, default))).Code);
    }

    /// <summary>未知或重复字段不能改变消息解释, 拒绝后不运行任何业务.</summary>
    [Theory]
    [InlineData("{\"type\":\"hello\",\"version\":1,\"options\":{\"kind\":\"mcp\",\"allowWrite\":false},\"admin\":true}")]
    [InlineData("{\"type\":\"hello\",\"version\":1,\"options\":{\"kind\":\"mcp\",\"allowWrite\":false,\"allowWrite\":true}}")]
    public async Task ExtraAndDuplicateFieldsAreRejected(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header); stream.Write(body); stream.Position = 0;
        Assert.Equal("BUSINESS_HOST_PROTOCOL_INVALID", (await Assert.ThrowsAsync<AppException>(() =>
            BusinessHostProtocol.ReadAsync<BusinessHostHello>(stream, default))).Code);
    }
}
