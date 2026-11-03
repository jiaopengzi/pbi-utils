// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\LocalRemoteConnectionStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 宿主最近远端连接及逐连接 DPAPI 密码存储, 单文件原子写入与跨实例合并.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Connections;

/// <summary>供应用连接界面显示的本机记录, 不包含密码或密码密文.</summary>
public sealed record RecentRemoteConnection(string Id, string Server, string Database, string AuthenticationMode, string UserName, bool HasSavedPassword, DateTimeOffset LastUsedAt);

/// <summary>最多20条连接, 密码按目标和身份绑定 CurrentUser DPAPI, 不向网页或第三方返回已有密码.</summary>
public sealed class LocalRemoteConnectionStore(string directory, ILogger<LocalRemoteConnectionStore>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<LocalRemoteConnectionStore>.Instance;
    private const int MaximumBytes = 262144;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string FilePath => Path.Combine(directory, "remote-connections.json");
    private sealed record Entry(string Id, string Server, string Database, string AuthenticationMode, string UserName, string? ProtectedPassword, DateTimeOffset LastUsedAt);
    private sealed record Document(int SchemaVersion, Entry[] Items);

    /// <summary>返回可显示的连接元数据, 密文不进入 DTO; 损坏文件保留并明确报错.</summary>
    public IReadOnlyList<RecentRemoteConnection> Load() => BusinessLog.Run<IReadOnlyList<RecentRemoteConnection>>(log, "RemoteConnections.Load", () =>
        Read().Select(item => new RecentRemoteConnection(item.Id, item.Server, item.Database, item.AuthenticationMode, item.UserName, item.ProtectedPassword is not null, item.LastUsedAt)).ToArray());

    /// <summary>在用户提交连接时记录, 包含尚未成功的尝试以减少重输; 不联网.</summary>
    /// <param name="address">已经宿主校验并授权的目标和可选凭据.</param>
    /// <param name="savePassword">是否以当前 Windows 用户加密保存密码.</param>
    /// <param name="token">保存前与原子替换前的取消.</param>
    /// <returns>本机记录的受限标识.</returns>
    public string Save(RemoteModelAddress address, bool savePassword, CancellationToken token) => BusinessLog.Run(log, "RemoteConnections.Save", () =>
    {
        RemoteModelAddressPolicy.Validate(address); token.ThrowIfCancellationRequested();
        var name = address.Credential?.Use((user, _) => user) ?? "";
        var server = address.Server.Trim(); var database = address.Database.Trim(); var mode = address.Credential is null ? "current" : "windows";
        var id = Identity(server, database, mode, name);
        var cipher = savePassword && address.Credential is not null ? Encrypt(address.Credential, id) : null;
        Mutate(items => [.. new[] { new Entry(id, server, database, mode, name, cipher, DateTimeOffset.UtcNow) }.Concat(items.Where(item => item.Id != id)).Take(20)], token);
        BusinessLog.RemotePreferenceSaved(log, mode, cipher is not null);
        return id;
    });

    /// <summary>只为已选中的同一目标记录恢复密码, 不回填网页密码框.</summary>
    /// <param name="id">已保存的64字符连接身份.</param>
    /// <returns>调用者需要释放的凭据; 未保存密码时返回 null.</returns>
    public RemoteWindowsCredential? ReadCredential(string id) => BusinessLog.Run(log, "RemoteConnections.ReadCredential", () =>
    {
        var entry = Find(Read(), id);
        if (entry.ProtectedPassword is null) return null;
        byte[]? plain = null; char[]? characters = null;
        try
        {
            plain = ProtectedData.Unprotect(Convert.FromBase64String(entry.ProtectedPassword), Entropy(entry.Id), DataProtectionScope.CurrentUser);
            if (plain.Length is < 1 or > 4096) throw new CryptographicException();
            characters = new UTF8Encoding(false, true).GetChars(plain);
            using var secret = new SecureString(); foreach (var character in characters) secret.AppendChar(character);
            return new RemoteWindowsCredential(entry.UserName, secret);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or DecoderFallbackException or AppException)
        { throw new AppException("REMOTE_PASSWORD_UNREADABLE", "无法用当前 Windows 用户解密保存的密码, 请重新输入或清除已保存密码", 3); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (characters is not null) Array.Clear(characters);
        }
    });

    /// <summary>保留连接信息, 原子清除已保存密码; 不影响当前模型的内存凭据.</summary>
    public void ClearPassword(string id) => BusinessLog.Run(log, "RemoteConnections.ClearPassword", () =>
        Mutate(items => { _ = Find(items, id); return [.. items.Select(item => item.Id == id ? item with { ProtectedPassword = null } : item)]; }, default));

    /// <summary>删除用户选中的最近记录和密文, 不接受文件路径或通配符.</summary>
    public void Delete(string id) => BusinessLog.Run(log, "RemoteConnections.Delete", () =>
        Mutate(items => { _ = Find(items, id); return [.. items.Where(item => item.Id != id)]; }, default));

    /// <summary>锁内读取最新状态并原子写入, 失败或取消保留原文件.</summary>
    private void Mutate(Func<Entry[], Entry[]> update, CancellationToken token)
    {
        UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, update(Read())), Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        token.ThrowIfCancellationRequested();
        try { UserDataStore.Write(FilePath, bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new AppException("REMOTE_CONNECTION_SAVE_FAILED", "最近连接保存失败, 原记录保留", 3); }
    }

    /// <summary>同一验证用于加载、保存和恢复; 未知版本、额外字段及损坏内容不静默覆盖.</summary>
    private Entry[] Read()
    {
        UserDataStore.Check(FilePath);
        if (!File.Exists(FilePath)) return [];
        try
        {
            if (new FileInfo(FilePath).Length > MaximumBytes) throw Invalid();
            var bytes = File.ReadAllBytes(FilePath);
            using var parsed = JsonDocument.Parse(bytes); JsonInputValidation.RejectDuplicates(parsed.RootElement);
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new JsonException();
            if (document.SchemaVersion != 1 || document.Items is null || document.Items.Length > 20) throw new JsonException();
            foreach (var item in document.Items)
            {
                if (item is null || item.Server is null || item.Database is null || item.UserName is null || item.AuthenticationMode is not ("current" or "windows") || item.LastUsedAt == default) throw new JsonException();
                RemoteModelAddressPolicy.Validate(new(item.Server, item.Database));
                if (item.Server != item.Server.Trim() || item.Database != item.Database.Trim()) throw new JsonException();
                if (item.AuthenticationMode == "windows")
                {
                    if (item.Server.Contains("://", StringComparison.Ordinal) || RemoteWindowsCredential.NormalizeUserName(item.UserName) != item.UserName) throw new JsonException();
                }
                else if (item.UserName.Length != 0 || item.ProtectedPassword is not null) throw new JsonException();
                if (item.Id != Identity(item.Server, item.Database, item.AuthenticationMode, item.UserName)) throw new JsonException();
                if (item.ProtectedPassword is not null && (item.ProtectedPassword.Length > 12000 || Convert.FromBase64String(item.ProtectedPassword).Length is < 1 or > 8192)) throw new JsonException();
            }
            if (document.Items.Select(item => item.Id).Distinct().Count() != document.Items.Length) throw new JsonException();
            return document.Items;
        }
        catch (Exception error) when (error is JsonException or FormatException or AppException) { throw Invalid(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new AppException("REMOTE_CONNECTIONS_UNREADABLE", "无法读取最近连接, 原记录保留", 3); }
    }

    /// <summary>加密只在受控回调内处理临时密码缓冲区, 结束后清零.</summary>
    private static string Encrypt(RemoteWindowsCredential credential, string id) => credential.Use((_, secret) =>
    {
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secret); var characters = new char[secret.Length]; byte[]? plain = null;
        try
        {
            Marshal.Copy(pointer, characters, 0, characters.Length);
            plain = new UTF8Encoding(false, true).GetBytes(characters);
            return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy(id), DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or EncoderFallbackException) { throw new AppException("REMOTE_PASSWORD_SAVE_FAILED", "Windows 密码加密失败, 原记录保留", 3); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); Array.Clear(characters); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    });

    /// <summary>只解析既有受限标识, 不接受任意记录或路径.</summary>
    private static Entry Find(Entry[] items, string id) => items.FirstOrDefault(item => item.Id == id) ?? throw new AppException("REMOTE_CONNECTION_NOT_FOUND", "最近连接记录不存在", 3);
    /// <summary>目标、数据库、模式和账号共同隔离密码, 主机和 Windows 身份忽略大小写.</summary>
    private static string Identity(string server, string database, string mode, string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server.ToUpperInvariant() + "\0" + database + "\0" + mode + "\0" + name.ToUpperInvariant())));
    /// <summary>绑定连接身份的独立附加熵.</summary>
    private static byte[] Entropy(string id) => Encoding.UTF8.GetBytes("pbi-utils/remote-windows/" + id);
    /// <summary>损坏错误不包含内容或路径, 原文件保留.</summary>
    private static AppException Invalid() => new("INVALID_REMOTE_CONNECTIONS", "最近连接记录损坏或版本不支持, 原文件保留; 可取消记住连接后继续手工输入", 3);
}
