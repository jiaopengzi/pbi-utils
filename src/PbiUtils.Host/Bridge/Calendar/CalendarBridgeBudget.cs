// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Calendar\CalendarBridgeBudget.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为明确的日期内容分段开放有界消息, 其他字段保持原预算.

using System.Text;
using System.Text.Json;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Calendar;

/// <summary>大消息仍须通过原方法的严格形状、业务配额和目标身份校验.</summary>
internal static class CalendarBridgeBudget
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);
    internal const int Normal = 65536;
    internal const int Absolute = 4 * 1024 * 1024;

    /// <summary>按完整信封计量, 大包只允许日历段或无路径能力的纯文本解析.</summary>
    internal static void Validate(string? method, JsonElement parameters, int bytes)
    {
        if (bytes > Absolute) throw TooLarge();
        if (bytes <= Normal) return;
        JsonElement calendar = default;
        if (method == "calendar.preview") parameters.TryGetProperty("calendar", out calendar);
        else if (method == "scaffold.updateDraft" && parameters.TryGetProperty("section", out var section)
            && section.ValueKind == JsonValueKind.String && section.GetString() == "calendar")
            parameters.TryGetProperty("value", out calendar);
        else if (method == "configuration.apply" && parameters.TryGetProperty("section", out var applying)
            && applying.ValueKind == JsonValueKind.String && applying.GetString() == "calendar"
            && parameters.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
            request.TryGetProperty("calendar", out calendar);
        else if (method == "calendar.parseWorkdays" && parameters.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String && Encoding.UTF8.GetByteCount(text.GetString()!) <= 2 * 1024 * 1024)
        {
            if (bytes - Size(text) <= Normal) return;
        }
        if (calendar.ValueKind != JsonValueKind.Object || bytes - Size(calendar) > Normal) throw TooLarge();
    }

    /// <summary>分发器直接调用也不能绕过同一分段上限.</summary>
    internal static void Parameters(string method, JsonElement parameters) => Validate(method, parameters, Size(parameters));
    /// <summary>按实际请求身份与连接代际计量完整响应, 不用最大身份长度减少短 ID 的可用容量.</summary>
    /// <param name="id">实际响应身份, null 仍按 JSON null 的真实字节数计量.</param>
    /// <param name="result">待返回的业务结果.</param>
    /// <param name="connectionRevision">本次响应实际使用的宿主连接代际.</param>
    /// <exception cref="AppException">含转义的完整信封超过 4 MiB 时拒绝.</exception>
    internal static void PreviewResult(string? id, object? result, long connectionRevision)
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { protocolVersion = 1, id, connectionRevision, result }, ResponseJson)) > Absolute)
            throw TooLarge();
    }
    private static int Size(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText());
    private static AppException TooLarge() => new("MESSAGE_TOO_LARGE", "消息超出该方法的内容大小限制");
}
