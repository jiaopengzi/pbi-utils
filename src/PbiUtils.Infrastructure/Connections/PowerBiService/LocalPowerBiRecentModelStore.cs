// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\PowerBiService\LocalPowerBiRecentModelStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按真实认证账号和云保存最近成功模型连接, 复用文件锁、严格校验与原子替换.

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

/// <summary>只保存稳定目录身份和使用时间, 不保存令牌、邮箱、地址或会话句柄.</summary>
public sealed class LocalPowerBiRecentModelStore(string directory, ILogger<LocalPowerBiRecentModelStore>? logger = null) : IPowerBiRecentModelStore
{
    private readonly ILogger log = logger ?? NullLogger<LocalPowerBiRecentModelStore>.Instance;
    private const int MaximumBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string FilePath => Path.Combine(directory, "service-recent-models.json");
    private sealed record Entry(string Id, string AccountScope, string EnvironmentName, string WorkspaceId, string ModelId, DateTimeOffset LastUsedAt);
    private sealed record Document(int SchemaVersion, Entry[] Items);

    /// <summary>读取指定真实认证身份的云分区, 未登录身份与非受支持云明确拒绝.</summary>
    public Task<IReadOnlyList<StoredPowerBiServiceModel>> LoadAsync(string accountId, string environmentName, CancellationToken token) => BusinessLog.RunAsync<IReadOnlyList<StoredPowerBiServiceModel>>(log, "ServiceRecentModels.Load", () =>
        Task.Run<IReadOnlyList<StoredPowerBiServiceModel>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var scope = Scope(accountId, environmentName);
            return Read().Where(item => item.AccountScope == scope && item.EnvironmentName == environmentName)
                .Select(item => new StoredPowerBiServiceModel(item.Id, item.WorkspaceId, item.ModelId, item.LastUsedAt)).ToArray();
        }, token));

    /// <summary>共享锁内合并最新记录, 每个账号及云最多20条, 总量最多400条并按时间保留.</summary>
    public Task SaveAsync(string accountId, string environmentName, string workspaceId, string modelId, CancellationToken token) => BusinessLog.RunAsync(log, "ServiceRecentModels.Save", () => Task.Run(() =>
    {
        var scope = Scope(accountId, environmentName);
        if (!Identifier(workspaceId) || !Identifier(modelId)) throw new AppException("INVALID_SERVICE_MODEL", "模型已失效, 请重新获取并选择", 3);
        token.ThrowIfCancellationRequested(); UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var current = Read(); var id = Identity(scope, environmentName, workspaceId, modelId);
        var partition = new[] { new Entry(id, scope, environmentName, workspaceId, modelId, DateTimeOffset.UtcNow) }
            .Concat(current.Where(item => item.AccountScope == scope && item.EnvironmentName == environmentName && item.Id != id)).Take(20);
        var items = partition.Concat(current.Where(item => item.AccountScope != scope || item.EnvironmentName != environmentName))
            .OrderByDescending(item => item.LastUsedAt).Take(400).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, items), Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        token.ThrowIfCancellationRequested();
        try { UserDataStore.Write(FilePath, bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("SERVICE_RECENT_MODELS_SAVE_FAILED", "最近云端模型保存失败, 原记录保留; 可继续使用模型目录连接", 3); }
    }, token));

    /// <summary>共享锁内只删除当前真实身份及云的匹配记录, 不访问远端模型或其他账号分区.</summary>
    /// <param name="accountId">后台认证取得的稳定账号身份.</param>
    /// <param name="environmentName">后台当前认证的固定云名称.</param>
    /// <param name="id">已保存且有界的最近记录身份.</param>
    /// <param name="token">取消提交前准备, 原子保存开始后不再取消.</param>
    /// <param name="committing">原子保存前的账号复核及宿主取消裁决.</param>
    /// <returns>是否实际删除当前分区的一条记录.</returns>
    /// <exception cref="AppException">身份无效、记录损坏、文件不可写或锁冲突.</exception>
    public Task<bool> DeleteAsync(string accountId, string environmentName, string id, CancellationToken token, Action? committing = null) => BusinessLog.RunAsync(log, "ServiceRecentModels.Delete", () => Task.Run(() =>
    {
        var scope = Scope(accountId, environmentName); RecentRecordDeletionRequest.ValidateId(id);
        token.ThrowIfCancellationRequested(); UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var current = Read();
        var items = current.Where(item => item.Id != id || item.AccountScope != scope || item.EnvironmentName != environmentName).ToArray();
        token.ThrowIfCancellationRequested();
        if (items.Length == current.Length) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, items), Json);
        token.ThrowIfCancellationRequested(); committing?.Invoke();
        try { UserDataStore.Write(FilePath, bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("SERVICE_RECENT_MODELS_SAVE_FAILED", "最近云端模型保存失败, 原记录保留; 可继续使用模型目录连接", 3); }
        return true;
    }, token));

    /// <summary>限制文档大小、版本、字段及身份, 损坏文件保留且禁止后续覆盖.</summary>
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
            if (document.SchemaVersion != 1 || document.Items is null || document.Items.Length > 400) throw new JsonException();
            foreach (var item in document.Items)
                if (item is null || !Hash(item.AccountScope) || !PowerBiEnvironment.IsSupported(item.EnvironmentName)
                    || !Identifier(item.WorkspaceId) || !Identifier(item.ModelId) || item.LastUsedAt == default
                    || item.Id != Identity(item.AccountScope, item.EnvironmentName, item.WorkspaceId, item.ModelId)) throw new JsonException();
            if (document.Items.Select(item => item.Id).Distinct().Count() != document.Items.Length
                || document.Items.GroupBy(item => (item.AccountScope, item.EnvironmentName)).Any(group => group.Count() > 20)) throw new JsonException();
            return [.. document.Items.OrderByDescending(item => item.LastUsedAt)];
        }
        catch (Exception error) when (error is JsonException or AppException) { throw Invalid(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("SERVICE_RECENT_MODELS_UNREADABLE", "无法读取最近云端模型, 原记录保留; 可继续使用模型目录连接", 3); }
    }

    /// <summary>真实账号身份只用于不可逆分区, 不持久化输入邮箱或认证会话.</summary>
    private static string Scope(string accountId, string environmentName)
    {
        if (!Identifier(accountId) || !PowerBiEnvironment.IsSupported(environmentName))
            throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        return Digest(accountId);
    }
    /// <summary>目录标识有界且不接受控制字符, 不以名称作为身份.</summary>
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
    /// <summary>限制分区为规范 SHA256 文本, 不接受原始账号字段.</summary>
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    /// <summary>绑定账号、云和两个稳定目录标识, 不使用随机会话句柄或展示名.</summary>
    private static string Identity(string scope, string environmentName, string workspaceId, string modelId) => Digest(scope + "\0" + environmentName + "\0" + workspaceId + "\0" + modelId);
    /// <summary>使用固定 UTF-8 摘要生成本机记录身份.</summary>
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>固定提示不包含账号、模型标识、路径或原始文件内容.</summary>
    private static AppException Invalid() => new("INVALID_SERVICE_RECENT_MODELS", "最近云端模型记录损坏或版本不支持, 原文件保留; 可继续使用模型目录连接", 3);
}
