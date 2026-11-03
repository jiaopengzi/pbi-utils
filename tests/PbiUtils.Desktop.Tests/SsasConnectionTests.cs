// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\SsasConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证原生 Windows 凭据生命周期, TCP 探针, 安全错误码和永久 Debug 日志过滤.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Collections;
using System.ComponentModel;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class SsasConnectionTests
{
    /// <summary>指定身份由原生范围持有, 连接串和地址 DTO 均不序列化账号密码.</summary>
    [Fact]
    public void ExplicitCredentialIsBackendOnlyAndConnectionPropertiesAreSafe()
    {
        var current = new DbConnectionStringBuilder { ConnectionString = SsasConnection.ConnectionString(new("server:2383", "catalog")) };
        Assert.Equal("SSPI", current["Integrated Security"]);
        Assert.Equal("False", current["Persist Security Info"]);
        Assert.False(current.ContainsKey("Password"));
        Assert.False(current.ContainsKey("User ID"));
        using var secret = Secret("fake;\"test-password");
        using var credential = new RemoteWindowsCredential("server\\test-user", secret);
        using var address = new RemoteModelAddress("server:2383", "catalog", credential);
        var supplied = new DbConnectionStringBuilder { ConnectionString = SsasConnection.ConnectionString(address) };
        Assert.False(supplied.ContainsKey("User ID"));
        Assert.False(supplied.ContainsKey("Password"));
        Assert.Equal("Negotiate", supplied["SSPI"]);
        Assert.Equal("Pkt Privacy", supplied["Protection Level"]);
        Assert.DoesNotContain("test-user", supplied.ConnectionString);
        Assert.DoesNotContain("test-password", supplied.ConnectionString);
        // 不能在握手前把用户可能提供的 ID 当作目录显示名称; 实际模型由 TOM 唯一定位.
        Assert.False(supplied.ContainsKey("Initial Catalog"));
        var json = JsonSerializer.Serialize(address);
        Assert.DoesNotContain("Credential", json);
        Assert.DoesNotContain("test-user", json);
        Assert.DoesNotContain("test-password", json);
        Assert.DoesNotContain("test-user", address.ToString());
        Assert.DoesNotContain("test-password", address.ToString());
    }

    /// <summary>拒绝空密码和未限定的身份, 不尝试 Windows 登录或修改账号.</summary>
    [Fact]
    public void RejectsBlankPasswordAndAmbiguousIdentity()
    {
        using var blank = new SecureString();
        using var nonblank = Secret("fake");
        Assert.Equal("INVALID_WINDOWS_PASSWORD", Assert.Throws<AppException>(() => new RemoteWindowsCredential("server\\test-user", blank)).Code);
        Assert.Equal("INVALID_WINDOWS_ACCOUNT", Assert.Throws<AppException>(() => new RemoteWindowsCredential("test-user", nonblank)).Code);
        using var upn = new RemoteWindowsCredential("user@example.test", nonblank);
        using var address = new RemoteModelAddress("powerbi://api.powerbi.com/v1.0/myorg/test", "catalog", upn);
        Assert.Equal("INVALID_AUTHENTICATION_MODE", Assert.Throws<AppException>(() => ConnectedModelReader.ValidateRemoteAddress(address)).Code);
    }

    /// <summary>后端目标独立拥有密码副本, 失败撤销、成功切换和断开都释放, 不破坏调用者副本.</summary>
    [Fact]
    public void RegisteredTargetsOwnAndReleaseCredentialCopies()
    {
        using var secret = Secret("fake-password");
        using var credential = new RemoteWindowsCredential("server\\test-user", secret);
        var reader = new ConnectedModelReader(new NoFiles());
        var first = reader.RegisterRemote(new("server", "catalog", credential));
        var firstCopy = RegisteredCredential(reader, first);
        Assert.NotSame(credential, firstCopy);
        reader.Forget(first);
        Assert.Throws<ObjectDisposedException>(() => firstCopy.Use((_, _) => true));
        Assert.True(credential.Use((_, _) => true));
        var old = reader.RegisterRemote(new("server", "catalog", credential));
        var oldCopy = RegisteredCredential(reader, old);
        var retained = reader.RegisterRemote(new("server", "catalog", credential));
        var retainedCopy = RegisteredCredential(reader, retained);
        reader.Retain(retained);
        Assert.Throws<ObjectDisposedException>(() => oldCopy.Use((_, _) => true));
        Assert.True(retainedCopy.Use((_, _) => true));
        credential.Dispose();
        Assert.True(retainedCopy.Use((_, _) => true));
        reader.Disconnect();
        Assert.Throws<ObjectDisposedException>(() => retainedCopy.Use((_, _) => true));
    }

    /// <summary>默认和固定端口有明确范围, 命名实例不会被错误探测默认端口.</summary>
    [Theory]
    [InlineData("server:0")]
    [InlineData("server:65536")]
    [InlineData("server:99999")]
    public void RejectsInvalidPorts(string server) => Assert.Equal("INVALID_SERVER",
        Assert.Throws<AppException>(() => ConnectedModelReader.ValidateRemoteAddress(new(server, "catalog"))).Code);

    /// <summary>未知异常只提供安全字段, 网络和 Windows 身份错误不依赖可能含密码的正文.</summary>
    [Theory]
    [InlineData(10060, true, "MODEL_NETWORK_TIMEOUT")]
    [InlineData(10061, true, "MODEL_CONNECTION_REFUSED")]
    [InlineData(11001, true, "MODEL_SERVER_NOT_FOUND")]
    [InlineData(10054, true, "MODEL_HANDSHAKE_RESET")]
    [InlineData(1326, false, "MODEL_AUTHENTICATION_FAILED")]
    [InlineData(1327, false, "MODEL_ACCOUNT_RESTRICTED")]
    [InlineData(1331, false, "MODEL_ACCOUNT_RESTRICTED")]
    public void ClassifiesSafeNativeCodes(int nativeCode, bool socket, string expected)
    {
        Exception inner = socket ? new SocketException(nativeCode) : new Win32Exception(nativeCode, "private-user private-password private-server");
        var failure = SsasConnection.Failure("SSAS 连接", new Exception("private-model private-password", inner));
        Assert.Equal(expected, failure.Code);
        Assert.DoesNotContain("0x", failure.Message);
        Assert.DoesNotContain("Exception", failure.Message);
        Assert.DoesNotContain("安全详情", failure.Message);
        Assert.True(failure.Message.Length <= 40);
        Assert.DoesNotContain("private-", failure.Message);
    }

    /// <summary>真实回环 TCP 探针不读取模型, Debug 点即时过滤且失败日志不包含地址或异常正文.</summary>
    [Fact]
    public async Task TcpProbeAndPermanentDebugLogsRespectLevelAndRedaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-ssas-logging-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using (var runtime = new LoggingRuntime(root, "test"))
            {
                var logger = runtime.Factory.CreateLogger("SsasTest");
                await SsasConnection.ProbeAsync($"127.0.0.1:{port}", logger, default);
                using (var accepted = await listener.AcceptTcpClientAsync()) { }
                new LoggingService(runtime).Save("Debug", default);
                await SsasConnection.ProbeAsync($"127.0.0.1:{port}", logger, default);
                using (var accepted = await listener.AcceptTcpClientAsync()) { }
                SsasConnection.LogFailure(logger, "SdkConnect", new Win32Exception(1326, "private-user private-password private-server"));
                await SsasConnection.ProbeAsync("server\\instance", logger, default);
            }
            var entries = Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(1, entries.Count(e => e.TryGetProperty("Stage", out var stage) && stage.GetString() == "TcpProbeStarted"));
            Assert.Contains(entries, e => e.TryGetProperty("NativeErrorCode", out var code) && code.GetInt32() == 1326);
            var text = string.Join('\n', entries.Select(e => e.GetRawText()));
            Assert.Contains("TcpProbeSkipped", text);
            Assert.Contains("NamedInstanceUsesSdkDiscovery", text);
            Assert.DoesNotContain("127.0.0.1", text);
            Assert.DoesNotContain("private-", text);
            Assert.DoesNotContain("server\\\\instance", text);
        }
        finally { listener.Stop(); Directory.Delete(root, true); }
    }

    /// <summary>外层取消保持取消语义, TCP 拒绝能在 SDK 前返回安全错误并清理目标.</summary>
    [Fact]
    public async Task ProbeCancellationAndRefusedConnectionAreDifferentFailures()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SsasConnection.ProbeAsync("127.0.0.1:2383", NullLogger.Instance, new(true)));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var reader = new ConnectedModelReader(new NoFiles());
        var service = new ModelConnectionService(reader);
        var failure = await Assert.ThrowsAsync<AppException>(() => service.RemoteAsync(new($"127.0.0.1:{port}", "catalog"), default));
        Assert.Equal("MODEL_CONNECTION_REFUSED", failure.Code);
        Assert.Equal(("server", 2383), SsasConnection.TcpEndpoint("server"));
        Assert.Null(SsasConnection.TcpEndpoint("server\\instance"));
    }

    /// <summary>构造仅用于测试的密码, 不读取用户凭据.</summary>
    private static SecureString Secret(string value)
    {
        var password = new SecureString(); foreach (var character in value) password.AppendChar(character); return password;
    }

    /// <summary>观察真实目标持有的副本, 用于校验释放而不连接网络或暴露秘密.</summary>
    private static RemoteWindowsCredential RegisteredCredential(ConnectedModelReader reader, string id)
    {
        var targets = (IDictionary)typeof(ConnectedModelReader).GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        var target = targets[id]!;
        return (RemoteWindowsCredential)target.GetType().GetProperty("Credential")!.GetValue(target)!;
    }

    /// <summary>本组不能意外读取用户 PBIP 文件.</summary>
    private sealed class NoFiles : IProjectReader
    {
        /// <summary>禁止文件路径进入这些在线连接测试.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => throw new InvalidOperationException();
    }
}
