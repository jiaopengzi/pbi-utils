// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\RemoteConnectionStoreTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实 Windows DPAPI 最近连接, 目标密码隔离, 原子保存与安全日志.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Net;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class RemoteConnectionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-remote-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "remote-connections.json");

    /// <summary>密码以真实 CurrentUser DPAPI 保存, 重启可恢复, 元数据 DTO 和文件不包含明文密码.</summary>
    [Fact]
    public void RealDpapiRoundTripAndMetadataExcludePlainPasswords()
    {
        const string password = "fake;Password_🔒";
        using var address = Address("server", "database", "server\\test-user", password);
        var store = new LocalRemoteConnectionStore(root);
        var id = store.Save(address, true, default);
        var restarted = new LocalRemoteConnectionStore(root);
        var item = Assert.Single(restarted.Load());
        Assert.Equal(id, item.Id); Assert.True(item.HasSavedPassword);
        Assert.DoesNotContain(password, File.ReadAllText(FilePath));
        Assert.DoesNotContain("ProtectedPassword", JsonSerializer.Serialize(item));
        using var restored = restarted.ReadCredential(id);
        Assert.NotNull(restored);
        Assert.Equal(password, restored.Use((name, secret) => new NetworkCredential(name, secret).Password));
    }

    /// <summary>不同目标、模型和账号隔离密码, 替换密文也不能跨目标解密.</summary>
    [Fact]
    public void PasswordsAreBoundToConnectionIdentity()
    {
        var store = new LocalRemoteConnectionStore(root);
        using var first = Address("server-one", "model", "server-one\\user", "first-secret");
        using var second = Address("server-two", "model", "server-two\\user", "second-secret");
        var firstId = store.Save(first, true, default); var secondId = store.Save(second, true, default);
        Assert.NotEqual(firstId, secondId); Assert.Equal(2, store.Load().Count);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        var entries = json["items"]!.AsArray();
        var ciphertext = entries.Single(e => e!["id"]!.GetValue<string>() == firstId)!["protectedPassword"]!.GetValue<string>();
        entries.Single(e => e!["id"]!.GetValue<string>() == secondId)!["protectedPassword"] = ciphertext;
        File.WriteAllText(FilePath, json.ToJsonString());
        var error = Assert.Throws<AppException>(() => store.ReadCredential(secondId));
        Assert.Equal("REMOTE_PASSWORD_UNREADABLE", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        using var unaffected = store.ReadCredential(firstId); Assert.NotNull(unaffected);
    }

    /// <summary>不保存密码、清除密码及删除记录均持久化, 不改变调用者内存中的凭据.</summary>
    [Fact]
    public void PasswordPreferenceClearAndDeletePersistIndependently()
    {
        var store = new LocalRemoteConnectionStore(root);
        using var address = Address("server", "model", "server\\user", "fake-secret");
        var id = store.Save(address, false, default);
        Assert.False(Assert.Single(store.Load()).HasSavedPassword); Assert.Null(store.ReadCredential(id));
        store.Save(address, true, default); Assert.True(Assert.Single(store.Load()).HasSavedPassword);
        store.ClearPassword(id); Assert.Null(new LocalRemoteConnectionStore(root).ReadCredential(id));
        Assert.True(address.Credential!.Use((_, _) => true));
        store.Delete(id); Assert.Empty(new LocalRemoteConnectionStore(root).Load());
        Assert.Equal("REMOTE_CONNECTION_NOT_FOUND", Assert.Throws<AppException>(() => store.ReadCredential(id)).Code);
    }

    /// <summary>两个适配器每次锁内合并, 同一目标主机大小写去重, 保留最近20条尝试.</summary>
    [Fact]
    public void MultipleInstancesMergeAndLimitRecentAttempts()
    {
        var first = new LocalRemoteConnectionStore(root); var second = new LocalRemoteConnectionStore(root);
        var id = first.Save(new("SERVER", "model"), false, default);
        Assert.Equal(id, second.Save(new("server", "model"), false, default)); Assert.Single(first.Load());
        for (var index = 0; index < 22; index++) (index % 2 == 0 ? first : second).Save(new("server-" + index, "model"), false, default);
        Assert.Equal(20, first.Load().Count); Assert.Equal("server-21", first.Load()[0].Server);
    }

    /// <summary>预先取消和跨实例锁冲突不修改原文件; 损坏记录保留且拒绝被保存覆盖.</summary>
    [Fact]
    public void CancellationLockConflictAndCorruptionPreserveExistingFile()
    {
        var store = new LocalRemoteConnectionStore(root); store.Save(new("server", "model"), false, default);
        var before = File.ReadAllBytes(FilePath);
        Assert.Throws<OperationCanceledException>(() => store.Save(new("another", "model"), false, new(true)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using (UserDataStore.Lock(FilePath + ".lock")) Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => store.Save(new("another", "model"), false, default)).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        File.WriteAllText(FilePath, "{broken");
        Assert.Equal("INVALID_REMOTE_CONNECTIONS", Assert.Throws<AppException>(() => store.Load()).Code);
        Assert.Equal("INVALID_REMOTE_CONNECTIONS", Assert.Throws<AppException>(() => store.Save(new("another", "model"), false, default)).Code);
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    /// <summary>未知版本、重复字段、超限端口和损坏密文被拒绝且不自动修复原文件.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("port")]
    [InlineData("cipher")]
    public void RejectsMalformedStoredMetadata(string mutation)
    {
        var store = new LocalRemoteConnectionStore(root);
        using var address = Address("server", "model", "server\\user", "fake-password"); store.Save(address, true, default);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        if (mutation == "version") json["schemaVersion"] = 2;
        else if (mutation == "port") json["items"]![0]!["server"] = "server:99999";
        else if (mutation == "cipher") json["items"]![0]!["protectedPassword"] = "not base64";
        var content = mutation == "duplicate" ? "{\"schemaVersion\":1,\"schemaVersion\":1,\"items\":[]}" : json.ToJsonString();
        File.WriteAllText(FilePath, content);
        Assert.Equal("INVALID_REMOTE_CONNECTIONS", Assert.Throws<AppException>(() => store.Load()).Code);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    /// <summary>Windows 原生密码不能包含空终止符, 验证发生在保存或 SDK 调用前.</summary>
    [Fact]
    public void RejectsEmbeddedNullPasswordBeforePersistence()
    {
        using var secret = new SecureString(); secret.AppendChar('x'); secret.AppendChar('\0'); secret.AppendChar('y');
        Assert.Equal("INVALID_WINDOWS_PASSWORD", Assert.Throws<AppException>(() => new RemoteWindowsCredential("server\\user", secret)).Code);
        Assert.False(File.Exists(FilePath));
    }

    /// <summary>真实日志不包含目标、账号或密码, XMLA 不保存组织令牌或指定 Windows 账号.</summary>
    [Fact]
    public void StoredCredentialsRemainNativeAndLogsAreRedacted()
    {
        using var runtime = new LoggingRuntime(root, "test"); new LoggingService(runtime).Save("Debug", default);
        var store = new LocalRemoteConnectionStore(root, runtime.Factory.CreateLogger<LocalRemoteConnectionStore>());
        using var address = Address("private-server", "private-model", "private-server\\private-user", "private-password");
        var id = store.Save(address, true, default); _ = store.Load(); using var restored = store.ReadCredential(id);
        store.ClearPassword(id); store.Delete(id);
        using var wrong = Address("powerbi://api.powerbi.com/v1.0/myorg/workspace", "catalog", "server\\user", "fake");
        Assert.Equal("INVALID_AUTHENTICATION_MODE", Assert.Throws<AppException>(() => store.Save(wrong, true, default)).Code);
        var xmla = store.Save(new("powerbi://api.powerbi.com/v1.0/myorg/workspace", "catalog"), true, default);
        Assert.Null(store.ReadCredential(xmla)); Assert.False(Assert.Single(store.Load()).HasSavedPassword);
        runtime.Dispose();
        var content = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines));
        Assert.DoesNotContain("private-", content);
    }

    /// <summary>只构造测试假密码, 不读取用户凭据.</summary>
    private static RemoteModelAddress Address(string server, string database, string user, string password)
    {
        using var secret = new SecureString(); foreach (var character in password) secret.AppendChar(character);
        return new(server, database, new RemoteWindowsCredential(user, secret));
    }

    /// <summary>仅删除本测试独立临时目录, 校验绝对目标仍在临时根下.</summary>
    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
