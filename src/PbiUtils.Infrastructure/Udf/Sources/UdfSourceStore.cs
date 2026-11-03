// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Sources\UdfSourceStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原子保存宿主分配的第三方函数来源, 严格保留未知和损坏配置.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Sources;

/// <summary>配置只含来源名称与 HTTP(S) 仓库, 不保存凭据, 保存和读取均不联网.</summary>
public sealed class UdfSourceStore(string? userDataRoot = null) : IUdfSourceRegistry
{
    private readonly string path = Path.Combine(userDataRoot ?? RuntimeEnvironment.UserDataDirectory(), "udf", "sources.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static IReadOnlyList<UdfSourceEntry> Builtins => UdfSourceDefaults.Builtins;

    /// <inheritdoc />
    public Task<IReadOnlyList<UdfSourceEntry>> ListAsync(CancellationToken cancellationToken) => Run<IReadOnlyList<UdfSourceEntry>>(() => [.. Builtins, .. Read()], cancellationToken);

    /// <inheritdoc />
    public async Task<UdfSourceEntry> GetAsync(string id, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == id) ?? throw new AppException("UDF_SOURCE_MISSING", "函数来源不存在, 请重新选择");

    /// <inheritdoc />
    public Task<UdfSourceEntry> SaveAsync(string name, string baseUrl, string? id, string? baseline, CancellationToken cancellationToken, Action? committing = null, string protocol = "git", string? gitRef = null) => Run(() =>
    {
        ValidateProtocol(protocol, gitRef);
        var normalizedName = Name(name); var normalizedUrl = GitHubUdfRepositoryUri.Normalize(baseUrl).AbsoluteUri;
        using var gate = Gate();
        var items = Read().ToList();
        UdfSourceEntry? old = null;
        if (id is not null) { old = items.SingleOrDefault(item => item.Id == id) ?? throw Missing(); CheckBaseline(old, baseline); }
        else if (baseline is not null) throw Changed();
        if (items.Any(item => item.Id != id && (item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) || SameEndpoint(item, normalizedUrl, protocol, gitRef)))
            || Builtins.Any(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) || SameEndpoint(item, normalizedUrl, protocol, gitRef)))
            throw new AppException("UDF_SOURCE_DUPLICATE", "来源名称或地址已存在, 请使用其他名称和地址");
        if (old is null && items.Count >= 32) throw new AppException("UDF_SOURCE_LIMIT", "自定义函数来源最多 32 项");
        var saved = new Source(id ?? "source:" + Guid.NewGuid().ToString("N"), normalizedName, normalizedUrl, protocol, gitRef);
        var entry = Entry(saved);
        if (old is not null) items.Remove(old);
        items.Add(entry);
        var bytes = Serialize(items);
        cancellationToken.ThrowIfCancellationRequested();
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return entry;
    }, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null) => Run(() =>
    {
        using var gate = Gate();
        var items = Read().ToList(); var current = items.SingleOrDefault(item => item.Id == id) ?? throw Missing(); CheckBaseline(current, baseline);
        items.Remove(current);
        var bytes = Serialize(items);
        cancellationToken.ThrowIfCancellationRequested();
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return true;
    }, cancellationToken);

    /// <summary>文件锁与内容读取都位于后台, 不用用户显示名称构造路径.</summary>
    private FileStream Gate()
    { UserDataStore.Check(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); UserDataStore.Check(path); return UserDataStore.Lock(path + ".lock"); }

    /// <summary>严格读取小型配置, 所有保存前先验证现有文件.</summary>
    private UdfSourceEntry[] Read()
    {
        UserDataStore.Check(path);
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024) throw Invalid();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        try
        {
            using var document = UdfJson.Parse(bytes);
            var saved = document.RootElement.Deserialize<Saved>(Json);
            if (saved is null || saved.SchemaVersion != 3 || saved.Items is null || saved.Items.Length > 32) throw Invalid();
            var result = saved.Items.Select(source =>
            {
                if (source is null || !UdfValidation.IsSourceId(source.Id) || !source.Id.StartsWith("source:", StringComparison.Ordinal)
                    || source.Name != Name(source.Name) || source.BaseUrl != GitHubUdfRepositoryUri.Normalize(source.BaseUrl).AbsoluteUri) throw Invalid();
                ValidateProtocol(source.Protocol, source.GitRef);
                return Entry(source);
            }).ToArray();
            if (result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Length
                || result.Concat(Builtins).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length + Builtins.Count
                || result.Concat([Builtins[0]]).Select(item => item.Protocol + "\n" + item.BaseUrl + "\n" + item.GitRef).Distinct(StringComparer.Ordinal).Count() != result.Length + 1) throw Invalid();
            return result;
        }
        catch (JsonException) { throw Invalid(); }
        catch (AppException error) when (error.Code != "UDF_SOURCE_STORE_INVALID") { throw Invalid(); }
    }

    /// <summary>提交前验证完整序列化预算, 不产生无法由同一读取边界恢复的配置.</summary>
    private static byte[] Serialize(IEnumerable<UdfSourceEntry> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(3, [.. entries.Select(item => new Source(item.Id, item.Name, item.BaseUrl!, item.Protocol, item.GitRef))]), Json);
        return bytes.Length <= 64 * 1024 ? bytes : throw new AppException("UDF_SOURCE_STORE_LIMIT", "函数来源完整配置超出大小限制, 请缩短名称或地址");
    }
    /// <summary>每个条目的基线只绑定自身身份和规范内容, 其他来源编辑不使其失效.</summary>
    private static UdfSourceEntry Entry(Source source) => new(source.Id, source.Name, source.BaseUrl,
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source, Json))), "custom", source.Protocol, source.GitRef);
    /// <summary>限制纯显示名称, 拒绝控制字符和无意义空白.</summary>
    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && name == name.Trim() && !name.Any(char.IsControl)
        ? name : throw new AppException("UDF_SOURCE_INVALID", "请填写有效来源名称和 HTTP(S) 仓库地址");
    /// <summary>基线缺失、变化和内置身份不能作为用户来源修改.</summary>
    private static void CheckBaseline(UdfSourceEntry current, string? baseline) { if (baseline is null || current.Baseline != baseline) throw Changed(); }
    /// <summary>所有同步磁盘工作均在后台执行, 提交后的取消不改报结果.</summary>
    private static Task<T> Run<T>(Func<T> operation, CancellationToken cancellationToken) => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return operation(); }, cancellationToken);
    /// <summary>保留旧文件并报告不能理解的格式.</summary>
    private static AppException Invalid() => new("UDF_SOURCE_STORE_INVALID", "函数来源配置损坏或版本未知, 已保留原文件");
    /// <summary>目标身份不再存在.</summary>
    private static AppException Missing() => new("UDF_SOURCE_MISSING", "用户函数来源不存在或为不可修改的内置来源");
    /// <summary>旧选择和旧编辑不能改写新配置.</summary>
    private static AppException Changed() => new("UDF_SOURCE_CHANGED", "函数来源已变化, 请刷新并重新选择");
    /// <summary>快照提交与来源更新使用同一短时跨进程配置锁, 防止检查后配置漂移.</summary>
    internal FileStream LockConfiguration() => Gate();
    /// <summary>同一规范仓库和 ref 视为重复来源, 不混用不同引用的配置基线.</summary>
    private static bool SameEndpoint(UdfSourceEntry entry, string url, string protocol, string? gitRef) => entry.BaseUrl == url && entry.Protocol == protocol && entry.GitRef == gitRef;
    /// <summary>远端只支持 Git 仓库, ref 使用受控适配器的统一语法约束.</summary>
    private static void ValidateProtocol(string protocol, string? gitRef)
    {
        if (protocol != "git") throw new AppException("UDF_SOURCE_PROTOCOL_INVALID", "函数来源协议或 Git 引用无效");
        GitUdfCatalogSource.ValidateReference(gitRef);
    }
    private sealed record Source(string Id, string Name, string BaseUrl, string Protocol, string? GitRef = null);
    private sealed record Saved(int SchemaVersion, Source[] Items);
}
