// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpDesktopRead.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对 MCP 桌面权威快照提供摘要与有界分页, 不裁剪宿主状态或改变业务终态.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>外部读取投影, 仅影响工具显示结果; 完整模型和授权基线仍由宿主持有.</summary>
internal sealed record McpDesktopRead(string Section = "summary", int Offset = 0, int Limit = 25,
    string? Table = null, bool IncludeExpressions = false, string? BaselineId = null, string? ProjectId = null)
{
    private static readonly string[] Sections = ["summary", "tables", "columns", "measures", "functions", "configuration"];
    private static readonly string[] SnapshotKeys = ["projectPath", "projectId", "baselineId", "modelFormat", "reportFormat", "modelVersion", "reportVersion", "warnings", "connection", "hasPendingChanges", "saveResult", "summary"];
    internal const int MaximumBytes = 128 * 1024;

    /// <summary>仅两个只读上下文入口接受投影参数, 写操作始终返回摘要与原保存事实.</summary>
    internal static bool Supports(string method) => method is "project.inspect" or "configuration.context";

    /// <summary>发布与实际校验一致的选项; 后续页必须提供前页完整目标和基线身份.</summary>
    internal static JsonNode Schema(string method)
    {
        var schema = JsonNode.Parse("""
        {
          "type":"object","additionalProperties":false,
          "properties":{
            "section":{"type":"string","enum":["summary","tables","columns","measures","functions","configuration"]},
            "offset":{"type":"integer","minimum":0,"maximum":1000000},
            "limit":{"type":"integer","minimum":1,"maximum":100},
            "table":{"type":"string","minLength":1,"maxLength":512},
            "includeExpressions":{"type":"boolean"},
            "baselineId":{"type":"string","minLength":1,"maxLength":200},
            "projectId":{"type":"string","minLength":1,"maxLength":200}
          },
          "allOf":[
            {"if":{"required":["offset"],"properties":{"offset":{"minimum":1}}},"then":{"required":["section","projectId","baselineId"],"properties":{"section":{"enum":["tables","columns","measures","functions"]}}}},
            {"if":{"required":["section"],"properties":{"section":{"const":"columns"}}},"then":{"required":["table"]}},
            {"if":{"required":["table"]},"then":{"required":["section"],"properties":{"section":{"enum":["tables","columns","measures"]}}}},
            {"if":{"required":["includeExpressions"],"properties":{"includeExpressions":{"const":true}}},"then":{"required":["section"],"properties":{"section":{"enum":["measures","functions"]}}}}
          ]
        }
        """)!;
        if (method != "configuration.context") schema["properties"]!["section"]!["enum"] = JsonSerializer.SerializeToNode(Sections.Where(section => section != "configuration"));
        return schema;
    }

    /// <summary>在启动宿主之前拒绝错误选项, 不把投影字段转发为 bridge 权限或参数.</summary>
    /// <param name="method">白名单业务方法.</param>
    /// <param name="input">完整外部工具参数.</param>
    /// <returns>已冻结的只读选项, 未提供时为摘要.</returns>
    /// <exception cref="AppException">未知字段、类型、分页身份或不适用选项.</exception>
    internal static McpDesktopRead Parse(string method, JsonElement input)
    {
        if (!input.TryGetProperty("read", out var read)) return new();
        if (!Supports(method) || read.ValueKind != JsonValueKind.Object
            || read.EnumerateObject().Any(property => property.Name is not ("section" or "offset" or "limit" or "table" or "includeExpressions" or "baselineId" or "projectId"))) throw Invalid();
        var section = ReadString(read, "section", 32) ?? "summary";
        var offset = ReadInteger(read, "offset", 0, 0, 1_000_000);
        var limit = ReadInteger(read, "limit", 25, 1, 100);
        var table = ReadString(read, "table", 512);
        var baseline = ReadString(read, "baselineId", 200);
        var project = ReadString(read, "projectId", 200);
        var expressions = false;
        if (read.TryGetProperty("includeExpressions", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            expressions = flag.GetBoolean();
        }
        if (!Sections.Contains(section, StringComparer.Ordinal) || section == "configuration" && method != "configuration.context"
            || offset > 0 && (baseline is null || project is null) || section == "columns" && table is null
            || table is not null && section is not ("tables" or "columns" or "measures")
            || expressions && section is not ("measures" or "functions")
            || section is "summary" or "configuration" && offset != 0) throw Invalid();
        return new(section, offset, limit, table, expressions, baseline, project);
    }

    /// <summary>投影成功快照, 失败/待核验结果原样保留; 投影不改变宿主观察代际或副作用事实.</summary>
    /// <param name="result">宿主已分类的业务结果, 支持原始强类型 DTO 与 JSON 值.</param>
    /// <param name="enforceBudget">只读请求执行展示预算; 写入完成后不能用展示失败掩盖已提交事实.</param>
    /// <returns>保持业务分类和目标代际的摘要或分页.</returns>
    /// <exception cref="AppException">分页目标已变化、表不存在或单页超过预算, 不返回伪完整结果.</exception>
    internal McpDesktopResult Apply(McpDesktopResult result, bool enforceBudget = true)
    {
        if (result.IsError || result.NeedsVerification) return result;
        // Host 在同进程返回原始 DTO; 使用与最终 MCP 输出相同的字段命名规则后再识别快照.
        var value = result.Value is JsonElement element ? element : JsonSerializer.SerializeToElement(result.Value, JsonSerializerOptions.Web);
        if (value.ValueKind != JsonValueKind.Object) return result;
        var context = value.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object && project.TryGetProperty("model", out _);
        var snapshot = context ? project : value;
        if (!snapshot.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array) return result;
        if (BaselineId is not null && BaselineId != snapshot.GetProperty("baselineId").GetString()
            || ProjectId is not null && ProjectId != snapshot.GetProperty("projectId").GetString())
            throw new AppException("STALE_BASELINE", "分页目标或模型已变化, 请重新读取摘要");
        JsonObject output;
        if (Section == "summary")
        {
            output = Summary(snapshot);
            if (context)
            {
                var summary = output;
                output = Copy(value, ["targetId", "canWrite", "canGenerateSample", "isCreatedTarget", "noChanges", "canUseMeasureConfig", "measureConfigurationRevision", "measureConfigUnavailableReason", "opened"]);
                summary["readSections"] = JsonSerializer.SerializeToNode(Sections);
                output["project"] = summary;
                output["projection"] = "summary";
            }
        }
        else if (Section == "configuration")
        {
            output = JsonNode.Parse(value.GetRawText())!.AsObject();
            output["project"] = Summary(snapshot);
            output["projection"] = "configuration";
        }
        else output = Page(snapshot, model);
        var projected = JsonSerializer.SerializeToElement(output);
        if (enforceBudget && Encoding.UTF8.GetByteCount(projected.GetRawText()) > MaximumBytes)
            throw new AppException("OUTPUT_TOO_LARGE", "所选内容超过 128 KiB; 请减少 limit、按 table 筛选或关闭 includeExpressions. 模型未因读取发生修改");
        return result with { Value = projected };
    }

    /// <summary>保留精确目标、基线与保存事实, 不返回全部表达式和模型对象数组.</summary>
    private static JsonObject Summary(JsonElement snapshot)
    {
        var output = Copy(snapshot, SnapshotKeys);
        output["model"] = Copy(snapshot.GetProperty("model"), ["name", "compatibilityLevel", "culture", "relationshipCount", "roleCount"]);
        output["projection"] = "summary";
        output["readSections"] = JsonSerializer.SerializeToNode(Sections.Where(section => section != "configuration"));
        return output;
    }

    /// <summary>基于同一完整快照筛选分页, 表摘要与列分页分离, 不静默忽略未知表名.</summary>
    private JsonObject Page(JsonElement snapshot, JsonElement model)
    {
        JsonElement selectedTable = default;
        if (Table is not null)
        {
            selectedTable = model.GetProperty("tables").EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == Table);
            if (selectedTable.ValueKind == JsonValueKind.Undefined) throw new AppException("INVALID_ARGUMENT", "指定表不存在, 请重新读取表清单");
        }
        var source = Section == "columns" ? selectedTable.GetProperty("columns") : model.GetProperty(Section);
        var items = source.EnumerateArray().Where(item => Table is null || Section == "columns"
            || item.GetProperty(Section == "tables" ? "name" : "table").GetString() == Table).ToArray();
        if (Offset > items.Length) throw new AppException("INVALID_ARGUMENT", "分页起点超出当前结果范围");
        var page = new JsonArray();
        foreach (var item in items.Skip(Offset).Take(Limit))
        {
            var entry = JsonNode.Parse(item.GetRawText())!.AsObject();
            if (Section == "tables") { entry.Remove("columns"); entry["columnCount"] = item.GetProperty("columns").GetArrayLength(); }
            if (!IncludeExpressions && Section is "measures" or "functions") entry.Remove("expression");
            page.Add(entry);
        }
        var output = Copy(snapshot, ["projectId", "baselineId", "connection"]);
        output["section"] = Section;
        output["offset"] = Offset;
        output["limit"] = Limit;
        output["total"] = items.Length;
        output["nextOffset"] = Offset + page.Count < items.Length ? JsonValue.Create(Offset + page.Count) : null;
        output["items"] = page;
        output["includeExpressions"] = IncludeExpressions;
        return output;
    }

    /// <summary>复制实际存在的字段; 未返回的数据保持缺失, 不补伪值.</summary>
    private static JsonObject Copy(JsonElement source, string[] keys)
    {
        var output = new JsonObject();
        foreach (var key in keys) if (source.TryGetProperty(key, out var value)) output[key] = JsonNode.Parse(value.GetRawText());
        return output;
    }

    /// <summary>读取可选非空有界字符串; 显式 null、空白或错误类型拒绝.</summary>
    private static string? ReadString(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()) || property.GetString()!.Length > maximum) throw Invalid();
        return property.GetString();
    }

    /// <summary>读取有界整数, 不把浮点或数字字符串转换成分页身份.</summary>
    private static int ReadInteger(JsonElement value, string name, int fallback, int minimum, int maximum)
    {
        if (!value.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var number) || number < minimum || number > maximum) throw Invalid();
        return number;
    }

    /// <summary>选项错误使用固定安全消息, 不回显用户模型内容.</summary>
    private static AppException Invalid() => new("INVALID_ARGUMENT", "读取选项无效; 后续页需要 projectId 和 baselineId, columns 需要 table, limit 为 1 至 100");
}
