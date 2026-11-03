// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Settings\LocalUiSettingsStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原子保存当前界面偏好, 显式保存时重建结构完整的旧格式, 损坏文件保持保护.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Settings;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Settings;

/// <summary>界面设置与模型、日志分开保存, 使用共享路径检查和跨进程锁.</summary>
public sealed class LocalUiSettingsStore(string root) : IUiSettingsStore
{
    private readonly string path = Path.Combine(root, "ui-settings.json");

    /// <summary>读取严格有界的配置, 失败使用默认值并保留原文件.</summary>
    /// <returns>实际保存的偏好, 或带警告的中文及显示最近文件默认值.</returns>
    public UiSettings Read() => ReadSettings(out _);

    /// <summary>读取当前格式, 仅将结构完整的已撤下格式标为可重建; 不恢复历史偏好, 损坏内容保持保护.</summary>
    /// <param name="unsupportedVersion">是否为可在显式保存时原子重建的不支持版本, 不读取其历史偏好.</param>
    /// <returns>当前偏好或明确说明原因的默认值.</returns>
    private UiSettings ReadSettings(out bool unsupportedVersion)
    {
        unsupportedVersion = false;
        UserDataStore.Check(path);
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1024) throw new JsonException();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1024) throw new JsonException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
            var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !value.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version)) throw new JsonException();
            if (version != 3)
            {
                // 此处只判断旧文件能否安全重建, 不恢复旧值或提供历史格式读取能力.
                if (version != 2 || properties.Length != 3
                    || !value.TryGetProperty("locale", out var previousLocale) || previousLocale.ValueKind != JsonValueKind.String
                    || !UiSettingsService.Locales.Contains(previousLocale.GetString(), StringComparer.Ordinal)
                    || !value.TryGetProperty("showRecentFiles", out var previousShow) || previousShow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException();
                unsupportedVersion = true;
                return new(Warning: "界面设置版本不受支持, 已使用默认值; 保存偏好时将重建当前配置。");
            }
            if (properties.Length != 4
                || !value.TryGetProperty("locale", out var locale) || locale.ValueKind != JsonValueKind.String
                || !UiSettingsService.Locales.Contains(locale.GetString(), StringComparer.Ordinal)) throw new JsonException();
            if (!value.TryGetProperty("showRecentFiles", out var show) || show.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            if (!value.TryGetProperty("daxFormatStyle", out var style) || style.ValueKind != JsonValueKind.String
                || style.GetString() is not ("long" or "short")) throw new JsonException();
            return new(locale.GetString()!, show.GetBoolean(), style.GetString()!);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(Warning: "界面设置无法读取, 已使用默认值; 原文件已保留。");
        }
    }

    /// <summary>锁内复核当前配置或可安全重建的旧格式, 提交前可取消, 原子替换失败保留原设置.</summary>
    /// <param name="patch">仅包含本次修改的字段, 未提供字段取锁内最新值, 不修改历史记录文件.</param>
    /// <param name="token">提交前取消令牌.</param>
    /// <param name="committing">宿主设置提交边界的回调.</param>
    /// <returns>实际写入的界面偏好.</returns>
    /// <exception cref="AppException">非法语言、配置无法识别或另一实例正在保存.</exception>
    public UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null)
    {
        UiSettingsService.ValidatePatch(patch);
        token.ThrowIfCancellationRequested();
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        using var lease = UserDataStore.Lock(Path.Combine(root, "ui-settings.lock"));
        var current = ReadSettings(out var unsupportedVersion);
        if (current.Warning is not null && !unsupportedVersion) throw new AppException("INVALID_UI_SETTINGS", "语言设置无法安全更新, 请保留并检查原配置", 3);
        token.ThrowIfCancellationRequested();
        var locale = patch.Locale ?? current.Locale;
        var showRecentFiles = patch.ShowRecentFiles ?? current.ShowRecentFiles;
        var daxFormatStyle = patch.DaxFormatStyle ?? current.DaxFormatStyle;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 3, locale, showRecentFiles, daxFormatStyle });
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return new(locale, showRecentFiles, daxFormatStyle);
    }
}
