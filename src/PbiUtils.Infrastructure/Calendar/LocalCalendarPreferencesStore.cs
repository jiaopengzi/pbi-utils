// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\LocalCalendarPreferencesStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 宿主固定目录内严格读取并原子保存独立日期方案, 保留无法识别的原配置.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>独立版本配置仅含字段ID与编号开关, 使用共享路径检查和跨进程文件锁.</summary>
public sealed class LocalCalendarPreferencesStore(string root) : ICalendarPreferencesStore
{
    private const int MaximumBytes = 8192;
    private readonly string path = Path.Combine(root, "calendar-preferences.json");

    /// <summary>有界读取严格白名单形状, 无效配置只返回警告且不改写原文件.</summary>
    public CalendarPreferences Read()
    {
        UserDataStore.Check(path);
        if (!File.Exists(path)) return CalendarPreferencesService.Default();
        try
        {
            // 不根据初始文件长度分配无界数组; 即使外部进程同时追加也只读固定上限.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumBytes + 1];
            var count = stream.ReadAtLeast(bytes, bytes.Length, false);
            if (count > MaximumBytes) throw new JsonException();
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
            var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Length != 3 || properties.Distinct(StringComparer.Ordinal).Count() != 3
                || !value.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1
                || !value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array
                || fields.GetArrayLength() > 63 || fields.EnumerateArray().Any(field => field.ValueKind != JsonValueKind.String)
                || !value.TryGetProperty("numberedColumns", out var numbered) || numbered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException();
            return new(CalendarPreferencesService.NormalizeFields(fields.EnumerateArray().Select(field => field.GetString()!).ToArray()), numbered.GetBoolean());
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
            || error is AppException { Code: "INVALID_CALENDAR_FIELDS" })
        {
            return CalendarPreferencesService.Default("日期字段方案无法读取, 已使用推荐方案; 原文件已保留且未覆盖。");
        }
    }

    /// <summary>锁内重新读取原配置, 不覆盖损坏/未知配置, 原子替换失败保留原值.</summary>
    public CalendarPreferences Save(string[] fields, bool numberedColumns, CancellationToken token, Action? committing = null)
    {
        var normalized = CalendarPreferencesService.NormalizeFields(fields);
        token.ThrowIfCancellationRequested();
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        using var lease = UserDataStore.Lock(Path.Combine(root, "calendar-preferences.lock"));
        if (Read().Warning is not null)
            throw new AppException("INVALID_CALENDAR_PREFERENCES", "日期字段方案无法安全更新, 请保留并检查原配置", 3);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, fields = normalized, numberedColumns });
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return new(normalized, numberedColumns);
    }
}
