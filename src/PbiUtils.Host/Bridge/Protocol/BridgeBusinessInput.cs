// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Protocol\BridgeBusinessInput.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用业务桥接的严格字段读取和既有错误, 不持有草稿、目录或权限.

using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

namespace PbiUtils.Host.Bridge.Protocol;

/// <summary>业务输入显式组合 UDF 选择的可省略版本规则, 不采用新建请求的可省略配置段.</summary>
internal static class BridgeBusinessInput
{
    internal static readonly JsonSerializerOptions Json = UdfInputJson.Create();

    /// <summary>严格 DTO 反序列化, 不输出原始用户正文.</summary>
    /// <typeparam name="T">固定业务 DTO, 不由网页指定类型.</typeparam>
    /// <param name="value">已核验重复字段和消息预算的 JSON 值.</param>
    /// <returns>完整 DTO; 缺失、未知字段和类型错误保持原校验错误.</returns>
    internal static T Read<T>(JsonElement value)
    {
        try { return value.Deserialize<T>(Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }
    /// <summary>可选部分允许明确 null, 其他值按严格 DTO 读取.</summary>
    /// <typeparam name="T">业务允许为空的固定配置段类型.</typeparam>
    /// <param name="value">原始配置段值.</param>
    /// <returns>显式 null 或完整配置段; 非法值按原校验错误拒绝.</returns>
    internal static T? Nullable<T>(JsonElement value) where T : class => value.ValueKind == JsonValueKind.Null ? null : Read<T>(value);
    /// <summary>空参方法不接受任意附加授权字段.</summary>
    /// <param name="value">入口已经核验为对象的参数.</param>
    /// <exception cref="AppException">对象包含任何字段时拒绝.</exception>
    internal static void Empty(JsonElement value) { if (value.EnumerateObject().Any()) throw Invalid(); }
    /// <summary>读取必需字符串字段, 缺失或类型错误为输入错误.</summary>
    /// <param name="value">入口已经核验为对象的业务输入.</param>
    /// <param name="name">固定必需字段名称.</param>
    /// <returns>原始字符串值, 不据此授予任何路径或来源权限.</returns>
    internal static string String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid();
    /// <summary>固定校验错误.</summary>
    internal static AppException Invalid() => new("VALIDATION_ERROR", "脚手架输入字段或类型无效");
    /// <summary>缺少宿主受限能力时不伪装成功.</summary>
    internal static AppException Unsupported() => new("UNSUPPORTED", "当前宿主未提供该脚手架能力", 3);
}
