// FilePath    : pbi-utils\src\PbiUtils.Application\Input\StructuredInput.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 桥接内嵌 JSON 的有界解析与重复字段拒绝规则.

using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Core;

namespace PbiUtils.Application.Input;

/// <summary>桥接内嵌 JSON 的共同边界, 不接受未知、重复或过深字段.</summary>
public static class StructuredInput
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 8 };

    /// <summary>解析最多 30000 字符的类型化内容, 只返回安全错误.</summary>
    /// <param name="value">最多 30000 字符且深度不超过 8 的嵌入 JSON.</param>
    /// <returns>拒绝未知属性及任意层级重复属性后的类型化输入.</returns>
    /// <exception cref="AppException">长度、深度、属性、类型或 JSON 格式无效.</exception>
    public static T Parse<T>(string value)
    {
        if (value is null || value.Length > 30000) throw new AppException("VALIDATION_ERROR", "批次过大, 请减少选择数量或内容后重试");
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException();
        }
        catch (JsonException) { throw new AppException("VALIDATION_ERROR", "请求字段、类型或格式无效"); }
    }

    /// <summary>递归拒绝重复属性, 防止不同解析器采用不同值.</summary>
    /// <param name="value">已经受同一深度限制解析的 JSON 节点.</param>
    /// <exception cref="JsonException">任意对象包含大小写不敏感的重复属性.</exception>
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
