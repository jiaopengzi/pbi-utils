// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在线连接地址、断开身份和伪造进程的边界验证, 不访问远端网络.
using PbiUtils.Windows.Models.Connected;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;
namespace PbiUtils.Desktop.Tests;

public sealed class ModelConnectionTests
{
    /// <summary>拒绝连接串注入、凭据和未知 URL 协议, 校验发生在建立连接之前.</summary>
    [Theory]
    [InlineData("server;Password=secret")]
    [InlineData("https://server.example/xmla")]
    [InlineData("powerbi://user@api.powerbi.com/v1.0/myorg/test")]
    [InlineData("powerbi://api.powerbi.com.evil.test/test")]
    [InlineData("server\nother")]
    [InlineData("powerbi://api.powerbi.com:8443/v1.0/myorg/test")]
    [InlineData("powerbi://api.powerbi.com/not-an-xmla-workspace")]
    public void RejectsUntrustedConnectionParameters(string server)
    {
        var reader = new ConnectedModelReader(new NoFiles());
        Assert.Equal("INVALID_SERVER", Assert.Throws<AppException>(() => reader.RegisterRemote(new(server, "model"))).Code);
    }
    /// <summary>已断开的宿主身份不能再连接, URI 中没有数据库地址.</summary>
    [Fact]
    public async Task DisconnectInvalidatesIdentityAndRejectsForgedPid()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var service = new ModelConnectionService(reader);
        var id = reader.RegisterRemote(new("server.example:2383", "catalog"));
        Assert.DoesNotContain("server.example", id);
        service.Disconnect();
        Assert.Equal("MODEL_DISCONNECTED", (await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(id, default))).Code);
        Assert.Equal("INVALID_INSTANCE", (await Assert.ThrowsAsync<AppException>(() => service.LocalAsync(-1, default))).Code);
        Assert.Equal("INSTANCE_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => service.LocalAsync(int.MaxValue, default))).Code);
    }
    /// <summary>成功切换到文件或其他目标后, 旧宿主注册不能继续读取.</summary>
    [Fact]
    public async Task SwitchingInvalidatesPreviousRegistrations()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var first = reader.RegisterRemote(new("server\\instance", "catalog"));
        var second = reader.RegisterRemote(new("powerbi://api.powerbi.com/v1.0/myorg/team", "model"));
        reader.Retain(second);
        Assert.Equal("MODEL_DISCONNECTED", (await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(first, default))).Code);
        reader.Retain(null);
        Assert.Equal("MODEL_DISCONNECTED", (await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(second, default))).Code);
    }
    private sealed class NoFiles : IProjectReader
    {
        /// <summary>本组仅验证在线身份, 不能意外读取本机文件.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
