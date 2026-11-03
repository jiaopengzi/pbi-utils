// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Updates\LocalProductUpdateStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立更新设置与各来源检查记录的严格有界原子存储.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application.Updates;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Updates;

/// <summary>设置锁只保护短文件提交, 网络检查使用另外的来源专用锁.</summary>
public sealed class LocalProductUpdateStore(string root) : IProductUpdateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };
    private readonly string path = Path.Combine(root, "product-updates.json");

    /// <summary>读取设置及当前或指定来源缓存; 损坏、不支持格式均明确失败并保留原文件.</summary>
    public ProductUpdateState Read(string? source = null)
    {
        if (source is not null) ProductUpdatePolicy.ValidateSource(source);
        var document = Load();
        return State(document, source);
    }

    /// <summary>锁内以最新设置合并本次补丁, 保留其他实例未涉及字段和各来源检查; 原子替换前可取消.</summary>
    public ProductUpdateState Save(ProductUpdateSettingsPatch patch, CancellationToken token, Action? committing = null)
    {
        ProductUpdatePolicy.ValidatePatch(patch);
        token.ThrowIfCancellationRequested();
        Prepare();
        using var lease = UserDataStore.Lock(Path.Combine(root, "product-updates.lock"));
        var current = Load();
        var settings = new ProductUpdateSettings(patch.AutomaticCheck ?? current.Settings.AutomaticCheck, patch.Source ?? current.Settings.Source);
        var updated = current with { Settings = settings };
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, Json);
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return State(updated, null);
    }

    /// <summary>同源网络请求跨进程互斥, 不阻塞设置保存或其他来源; 冲突抛 USER_DATA_BUSY.</summary>
    public IDisposable AcquireCheck(string source)
    {
        ProductUpdatePolicy.ValidateSource(source);
        Prepare();
        return UserDataStore.Lock(Path.Combine(root, "product-updates-" + source + ".lock"));
    }

    /// <summary>锁内合并仅本来源记录, 自动关闭或切源后不提交迟到结果; 取消保留原文件.</summary>
    public bool SaveCheck(ProductUpdateCheck check, bool automatic, CancellationToken token)
    {
        ValidateCheck(check);
        token.ThrowIfCancellationRequested();
        Prepare();
        using var lease = UserDataStore.Lock(Path.Combine(root, "product-updates.lock"));
        var current = Load();
        if (automatic && (!current.Settings.AutomaticCheck || current.Settings.Source != check.Source)) return false;
        current.Checks[check.Source] = check;
        token.ThrowIfCancellationRequested();
        UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(current, Json));
        return true;
    }

    /// <summary>复用共享路径检查, 不沿链接创建或访问用户数据.</summary>
    private void Prepare()
    {
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        UserDataStore.Check(root);
    }

    /// <summary>按严格当前 schema 读取有界文件, 重复/未知/缺字段都不自动重建覆盖.</summary>
    private Document Load()
    {
        UserDataStore.Check(path);
        if (!File.Exists(path)) return new(1, new(), new(StringComparer.Ordinal));
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new JsonException();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1024 * 1024) throw new JsonException();
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            Strict(parsed.RootElement);
            Shape(parsed.RootElement, "schemaVersion", "settings", "checks");
            var settings = parsed.RootElement.GetProperty("settings");
            Shape(settings, "automaticCheck", "source");
            if (settings.GetProperty("automaticCheck").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            var checks = parsed.RootElement.GetProperty("checks");
            if (checks.ValueKind != JsonValueKind.Object || checks.EnumerateObject().Count() > 2) throw new JsonException();
            foreach (var entry in checks.EnumerateObject())
            {
                ProductUpdatePolicy.ValidateSource(entry.Name);
                Shape(entry.Value, "source", "currentVersion", "status", "checkedAtUtc", "latestVersion", "releaseUrl", "notes", "assets", "errorCode", "isSimulation");
                if (entry.Value.GetProperty("assets").ValueKind != JsonValueKind.Array) throw new JsonException();
                foreach (var asset in entry.Value.GetProperty("assets").EnumerateArray()) Shape(asset, "dotNetMode", "fileName", "downloadUrl", "size");
            }
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new JsonException();
            if (document.SchemaVersion != 1 || document.Settings is null || document.Checks is null) throw new JsonException();
            ProductUpdatePolicy.ValidateSource(document.Settings.Source);
            foreach (var entry in document.Checks)
            {
                ValidateCheck(entry.Value);
                if (entry.Key != entry.Value.Source) throw new JsonException();
            }
            return document;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or AppException or ArgumentException)
        { throw new AppException("INVALID_UPDATE_SETTINGS", "更新设置无法安全读取, 请保留并检查原配置", 3); }
    }

    /// <summary>固定形状校验防止缺字段静默落入默认值.</summary>
    private static void Shape(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || names.Any(name => !value.TryGetProperty(name, out _)))
            throw new JsonException();
    }

    /// <summary>递归拒绝重复字段, 不允许 JSON 解析覆盖前值.</summary>
    private static void Strict(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new JsonException();
            foreach (var property in properties) Strict(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Strict(item);
    }

    /// <summary>缓存同样校验版本、状态和精确仓库地址, 不信任磁盘链接或任意失败码.</summary>
    private static void ValidateCheck(ProductUpdateCheck check)
    {
        if (check is null) throw new JsonException();
        ProductUpdatePolicy.ValidateSource(check.Source);
        if (!ProductUpdatePolicy.TryVersion(check.CurrentVersion, out _) || check.Status is not ("available" or "current" or "noRelease" or "failed")
            || check.Notes is null || check.Notes.Length > 65536 || check.Assets is null || check.Assets.Length > 2 || check.Assets.Any(asset => asset is null) || check.IsSimulation) throw new JsonException();
        if (check.Status is "available" or "current")
        {
            if (!ProductUpdatePolicy.TryVersion(check.LatestVersion, out var latest) || check.ReleaseUrl != ProductUpdatePolicy.Link(check.Source, check.LatestVersion!, "release") || check.ErrorCode is not null)
                throw new JsonException();
            ProductUpdatePolicy.TryVersion(check.CurrentVersion, out var current);
            if ((latest > current) != (check.Status == "available")) throw new JsonException();
        }
        else if (check.LatestVersion is not null || check.ReleaseUrl is not null || check.Assets.Length != 0 || check.Notes.Length != 0
            || (check.Status == "failed" ? check.ErrorCode is null || !Errors.Contains(check.ErrorCode, StringComparer.Ordinal) : check.ErrorCode is not null)) throw new JsonException();
        if (check.Assets.Select(asset => asset.DotNetMode).Distinct(StringComparer.Ordinal).Count() != check.Assets.Length) throw new JsonException();
        foreach (var asset in check.Assets)
        {
            if (asset.DownloadUrl != ProductUpdatePolicy.Link(check.Source, check.LatestVersion!, asset.DotNetMode)
                || asset.FileName != $"pbi-utils-{check.LatestVersion}-win-x64-{asset.DotNetMode}-setup.exe" || asset.Size is < 0) throw new JsonException();
        }
    }

    private static readonly string[] Errors = ["UPDATE_RATE_LIMITED", "UPDATE_HTTP_FAILED", "UPDATE_TIMEOUT", "UPDATE_INVALID_RESPONSE", "UPDATE_RESPONSE_TOO_LARGE", "UPDATE_PAGE_LIMIT", "UPDATE_NETWORK_FAILED", "UPDATE_CHECK_BUSY"];

    /// <summary>仅返回选择来源的缓存, 不泄漏其他来源的过期显示状态.</summary>
    private static ProductUpdateState State(Document document, string? source) => new(document.Settings, document.Checks.GetValueOrDefault(source ?? document.Settings.Source));

    /// <summary>独立版本化磁盘格式, 不复用 UiSettings 或模型存储.</summary>
    private sealed record Document(int SchemaVersion, ProductUpdateSettings Settings, Dictionary<string, ProductUpdateCheck> Checks);
}
