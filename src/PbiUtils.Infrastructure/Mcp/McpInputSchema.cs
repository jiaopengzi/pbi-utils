// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpInputSchema.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 等价转换 MCP 对外输入 schema 的表示, 保留参数域和原始机器契约.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>仅适配 JSON Schema 2020-12 的发布表示, 不参与运行时参数验证或授权.</summary>
internal static class McpInputSchema
{
    private static readonly string[] JsonTypes = ["null", "boolean", "object", "array", "number", "string"];
    private static readonly string[] SchemaMaps = ["properties", "patternProperties", "dependentSchemas", "$defs"];
    private static readonly string[] SchemaArrays = ["allOf", "anyOf", "oneOf", "prefixItems"];
    private static readonly string[] SchemaValues = ["items", "contains", "not", "propertyNames", "if", "then", "else", "additionalProperties", "unevaluatedProperties", "unevaluatedItems", "contentSchema"];

    /// <summary>复制并转换 schema, 不修改导出器或 bridge 所拥有的原始 JSON.</summary>
    /// <param name="schema">受信任的工具输入 schema, 使用 JSON Schema 2020-12.</param>
    /// <returns>独立的可移植表示; JSON 解析、结构读取或序列化异常直接传播, 不降级为空约束.</returns>
    internal static JsonElement CreatePortable(JsonElement schema)
        => JsonSerializer.SerializeToElement(Rewrite(JsonNode.Parse(schema.GetRawText()), null));

    /// <summary>只访问规范规定的子 schema 位置, enum/const/default/examples 等业务字面值保持原样.</summary>
    /// <param name="node">当前副本节点.</param>
    /// <param name="keyword">所属 schema 关键字, 用于保留标准布尔开关和 not 下的恒真对象.</param>
    /// <returns>同一节点或语义等价替代; 无效类型声明的读取错误向调用方传播.</returns>
    private static JsonNode? Rewrite(JsonNode? node, string? keyword)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var allowed))
        {
            if (keyword is "additionalProperties" or "unevaluatedProperties" or "unevaluatedItems") return node;
            return allowed ? TypeUnion(JsonTypes) : new JsonObject { ["not"] = new JsonObject() };
        }
        if (node is not JsonObject schema) return node;
        if (schema.Count == 0) return keyword == "not" ? schema : TypeUnion(JsonTypes);

        // 不移动既有子 schema, 保持本地 $ref 的 JSON Pointer、条件和组合规则指向不变.
        foreach (var key in SchemaMaps)
            if (schema[key] is JsonObject map)
                foreach (var property in map.ToArray()) map[property.Key] = Rewrite(property.Value, key);
        foreach (var key in SchemaArrays)
            if (schema[key] is JsonArray array)
                for (var index = 0; index < array.Count; index++)
                {
                    var child = array[index];
                    var rewritten = Rewrite(child, key);
                    // JsonArray 不能重新挂载仍属于自身的节点; 原地转换无须重复赋值.
                    if (!ReferenceEquals(child, rewritten)) array[index] = rewritten;
                }
        foreach (var key in SchemaValues)
            if (schema.TryGetPropertyValue(key, out var child)) schema[key] = Rewrite(child, key);

        if (schema["type"] is JsonArray types)
        {
            var union = TypeUnion(types.Select(type => type!.GetValue<string>()));
            schema.Remove("type");
            if (schema.ContainsKey("anyOf"))
            {
                // 类型并集与原 anyOf 必须同时满足, 不能覆盖原分支或把约束放宽为二者任选其一.
                if (schema["allOf"] is not JsonArray conjunction) schema["allOf"] = conjunction = [];
                conjunction.Add(union);
            }
            else
            {
                var branches = union["anyOf"];
                union.Remove("anyOf");
                schema["anyOf"] = branches;
            }
        }
        return schema;
    }

    /// <summary>以单类型分支表达原类型并集; number 已包含 integer, 完整 JSON 域无需额外整数分支.</summary>
    /// <param name="types">原有类型顺序或完整 JSON 类型域.</param>
    /// <returns>全新的 anyOf 对象, 不共享有父节点的 JsonNode.</returns>
    private static JsonObject TypeUnion(IEnumerable<string> types)
        => new() { ["anyOf"] = new JsonArray(types.Select(type => (JsonNode)new JsonObject { ["type"] = type }).ToArray()) };
}
