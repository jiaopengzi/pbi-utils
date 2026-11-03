// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Input\JsonInputValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对共享存储与命令参数执行严格 JSON 字段校验, 保持不同入口解析一致.

using System.Text.Json;

namespace PbiUtils.Infrastructure.Input;

/// <summary>跨 CLI、宿主和存储复用的 JSON 输入校验.</summary>
public static class JsonInputValidation
{
    /// <summary>递归拒绝嵌套对象的重复 JSON 字段, 保持各调用方的解析结果一致.</summary>
    /// <param name="value">由调用方按现有深度和大小预算解析的 JSON 值.</param>
    /// <exception cref="JsonException">任一对象包含同名字段时拒绝整个输入.</exception>
    public static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { if (!names.Add(field.Name)) throw new JsonException(); RejectDuplicates(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
