// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpDesktopContractTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP 覆盖全部桌面业务、拒绝内部授权通道并严格限制本机传输帧.

using PbiUtils.Host.Bridge.Protocol;
using System.Buffers.Binary;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class McpDesktopContractTests
{
    /// <summary>所有桌面业务均可发现, GUI控制和已有独立共享工具显式列出, 新业务遗漏必须失败.</summary>
    [Fact]
    public void CatalogCoversEveryBusinessMethodAndExcludesPrivateControls()
    {
        string[] internalControls = ["ui.dialogRespond", "request.cancel", "app.resetUserData", "project.connectExternalTool", "learning.update", "learning.close", "mcp.connections", "mcp.disconnect", "mcp.setAdmission", "updates.status", "updates.save", "updates.open"];
        // 更新查询由独立 updates_check 复用共享用例; 不重复暴露可打开浏览器或改变GUI偏好的桌面工具.
        // UpdatesCommandTests 与全工具机器清单核验该独立入口的发现、只读权限、实际查询和失败分类.
        string[] sharedQueries = ["updates.check"];
        Assert.Equal(BridgeMethods.All.Except(internalControls).Except(sharedQueries).Order(), McpDesktopCatalog.All.Select(item => item.Method).Order());
        Assert.All(internalControls.Concat(sharedQueries), method => Assert.False(McpDesktopCatalog.TryGet(method, out _)));
        Assert.All(McpDesktopCatalog.All, operation =>
        {
            Assert.Equal("object", operation.InputSchema.GetProperty("type").GetString());
            Assert.False(operation.InputSchema.GetProperty("additionalProperties").GetBoolean());
        });
        Assert.True(McpDesktopCatalog.TryGet("model.edit", out var edit));
        Assert.False(edit.ReadOnly);
        Assert.True(McpDesktopCatalog.TryGet("project.connectLocal", out var connection));
        Assert.False(connection.ReadOnly);
    }

    /// <summary>远端连接工具只接受无秘密地址, 当前 Windows 身份无需本地表单; 修改仍需启动写权限.</summary>
    [Fact]
    public void RemoteConnectionSchemaContainsNoSecretOrFormInput()
    {
        Assert.True(McpDesktopCatalog.TryGet("project.connectRemote", out var connection));
        Assert.Equal(["server", "database"], connection.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["server", "database"], connection.InputSchema.GetProperty("required").EnumerateArray().Select(property => property.GetString()));
        Assert.False(connection.ReadOnly);
        Assert.True(McpDesktopCatalog.TryGet("remote.mutate", out var mutation));
        Assert.False(mutation.ReadOnly);
    }

    /// <summary>帧完整往返保留中文、结构和文本, 不依赖控制台编码.</summary>
    [Fact]
    public async Task PipeFrameRoundTripsUtf8()
    {
        using var stream = new MemoryStream();
        await BusinessHostProtocol.WriteAsync(stream, new { method = "calendar.preview", parameters = new { text = "日期表" } }, CancellationToken.None);
        stream.Position = 0;
        var result = await BusinessHostProtocol.ReadAsync<JsonElement>(stream, CancellationToken.None);
        Assert.Equal("日期表", result.GetProperty("parameters").GetProperty("text").GetString());
        Assert.Equal(stream.Length, stream.Position);
    }

    /// <summary>长度恶意值必须在正文分配/读取前拒绝, 不接受负数或巨大帧.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(BusinessHostProtocol.MaximumBytes + 1)]
    public async Task InvalidFrameLengthIsRejected(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<AppException>(() => BusinessHostProtocol.ReadAsync<JsonElement>(stream, CancellationToken.None));
        Assert.Equal("BUSINESS_HOST_PROTOCOL_INVALID", error.Code);
    }

    /// <summary>超限写不能留半个有效请求, 接收方不得看到误导性的成功头.</summary>
    [Fact]
    public async Task OversizedResultWritesNothing()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<AppException>(() => BusinessHostProtocol.WriteAsync(stream, new { text = new string('x', BusinessHostProtocol.MaximumBytes) }, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    /// <summary>已关闭的短帧是失败, 不能回传空成功对象.</summary>
    [Fact]
    public async Task TruncatedFrameFails()
    {
        using var stream = new MemoryStream([8, 0, 0, 0, 123, 125]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => BusinessHostProtocol.ReadAsync<JsonElement>(stream, CancellationToken.None));
    }

}
