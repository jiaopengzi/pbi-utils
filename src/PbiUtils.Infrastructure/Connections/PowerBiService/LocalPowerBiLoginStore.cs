// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\PowerBiService\LocalPowerBiLoginStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保存最近20条 Service 邮箱及环境, 复用用户目录锁和原子替换, 不保存令牌.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Connections.PowerBiService;

/// <summary>与 SSAS 最近连接相同的本机存储规则; 邮箱和环境为元数据, 密码及令牌不进入此文件.</summary>
public sealed class LocalPowerBiLoginStore(string directory, ILogger<LocalPowerBiLoginStore>? logger = null) : IPowerBiLoginStore
{
    private readonly ILogger log = logger ?? NullLogger<LocalPowerBiLoginStore>.Instance;
    private const int MaximumBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string FilePath => Path.Combine(directory, "service-connections.json");
    private sealed record Entry(string Id, string Email, string EnvironmentName, DateTimeOffset LastUsedAt);
    private sealed record Document(int SchemaVersion, Entry[] Items);

    /// <summary>在线程池读取元数据, 不联网; 取消和损坏文件均明确报错, 不自动重建.</summary>
    public Task<IReadOnlyList<RecentPowerBiLogin>> LoadAsync(CancellationToken token) => BusinessLog.RunAsync<IReadOnlyList<RecentPowerBiLogin>>(log, "ServiceConnections.Load", () =>
        Task.Run<IReadOnlyList<RecentPowerBiLogin>>(() => { token.ThrowIfCancellationRequested(); return Read().Select(e => new RecentPowerBiLogin(e.Id, e.Email, e.EnvironmentName, e.LastUsedAt)).ToArray(); }, token));

    /// <summary>锁内合并最新文件, 按邮箱与云去重并保留最近20条实际提交的尝试.</summary>
    public Task SaveAsync(string email, string environmentName, CancellationToken token) => BusinessLog.RunAsync(log, "ServiceConnections.Save", () => Task.Run(() =>
    {
        email = PowerBiServiceConnection.ValidateEmail(email);
        if (!PowerBiEnvironment.IsSupported(environmentName)) throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "请选择受支持的登录环境");
        var id = Identity(email, environmentName);
        Mutate(items => [.. new[] { new Entry(id, email, environmentName, DateTimeOffset.UtcNow) }.Concat(items.Where(item => item.Id != id)).Take(20)], token);
    }, token));

    /// <summary>只删除用户选中的既有记录, 不接受路径或改变当前认证会话.</summary>
    public Task DeleteAsync(string id, CancellationToken token) => BusinessLog.RunAsync(log, "ServiceConnections.Delete", () => Task.Run(() =>
        Mutate(items =>
        {
            if (!items.Any(item => item.Id == id)) throw new AppException("SERVICE_CONNECTION_NOT_FOUND", "最近登录记录不存在", 3);
            return [.. items.Where(item => item.Id != id)];
        }, token), token));

    /// <summary>沿用 SSAS 的共享文件锁和原子替换, 原子提交前的失败或取消保留旧文件.</summary>
    private void Mutate(Func<Entry[], Entry[]> update, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, update(Read())), Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        token.ThrowIfCancellationRequested();
        try { UserDataStore.Write(FilePath, bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new AppException("SERVICE_CONNECTION_SAVE_FAILED", "最近登录保存失败, 原记录保留; 可取消记住登录后重试", 3); }
    }

    /// <summary>限制大小、字段、版本与身份; 损坏或额外敏感字段不自动覆盖或忽略.</summary>
    private Entry[] Read()
    {
        UserDataStore.Check(FilePath);
        if (!File.Exists(FilePath)) return [];
        try
        {
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > MaximumBytes) throw Invalid();
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var parsed = JsonDocument.Parse(bytes); JsonInputValidation.RejectDuplicates(parsed.RootElement);
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new JsonException();
            if (document.SchemaVersion != 1 || document.Items is null || document.Items.Length > 20) throw new JsonException();
            foreach (var item in document.Items)
            {
                if (item is null || item.Email is null || !PowerBiEnvironment.IsSupported(item.EnvironmentName)
                    || PowerBiServiceConnection.ValidateEmail(item.Email) != item.Email || item.LastUsedAt == default
                    || item.Id != Identity(item.Email, item.EnvironmentName)) throw new JsonException();
            }
            if (document.Items.Select(item => item.Id).Distinct().Count() != document.Items.Length) throw new JsonException();
            return document.Items;
        }
        catch (Exception error) when (error is JsonException or AppException) { throw Invalid(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new AppException("SERVICE_CONNECTIONS_UNREADABLE", "无法读取最近登录, 原记录保留; 可取消记住登录后手工输入", 3); }
    }

    /// <summary>邮箱忽略大小写去重, 不同云仍为不同记录; 标识不包含明文邮箱.</summary>
    private static string Identity(string email, string environmentName) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToUpperInvariant() + "\0" + environmentName)));
    /// <summary>固定提示不包含文件路径或原始内容, 允许不记住选择后手工登录.</summary>
    private static AppException Invalid() => new("INVALID_SERVICE_CONNECTIONS", "最近登录记录损坏或版本不支持, 原文件保留; 可取消记住登录后手工输入", 3);
}
