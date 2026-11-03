// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarGeneratedLinguisticMetadata.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以嵌入内置模板全文和实际日期映射证明当前 Q&A 归属, 仅同步已证明的生成绑定.

using PbiUtils.Infrastructure.Input;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.TemplateMeasures;

using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>只接受当前固定内置资源的完整内容证明, 人工编辑保持受保护.</summary>
internal static class CalendarGeneratedLinguisticMetadata
{
    private sealed record BuiltinCulture(string Name, string TableName, JsonObject Content);
    private static readonly Lazy<BuiltinCulture[]> builtinCultures = new(ReadBuiltinCultures);

    /// <summary>核对完整日期归属、登记角色和内置 Q&A 全文, 返回可随日期字段同步的真实 culture; 未知内容不赋予归属.</summary>
    internal static IReadOnlyList<TOM.Culture> VerifiedCultures(TOM.Table table)
    {
        if (table.Name != "Calendar" || table.Model is not { } model
            || table.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is null
            || !ModelConfigurationOwnership.IsOwned(table, table.Annotations)) return [];
        if (!ModelConfigurationOwnership.ReadRoles(model).Roles.TryGetValue(table.Name, out var role) || role != "calendar") return [];
        var mapping = ScaffoldCalendarWriter.CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal)).Columns;
        List<TOM.Culture> result = [];
        foreach (var culture in model.Cultures)
        {
            if (culture.LinguisticMetadata is not { ContentType: TOM.ContentType.Json, Content: { Length: > 0 } content }) continue;
            JsonNode? actual;
            try { actual = JsonNode.Parse(content); }
            catch (JsonException) { continue; }
            foreach (var source in builtinCultures.Value.Where(value => value.Name == culture.Name && value.TableName == table.Name))
            {
                var expected = Project(source.Content, table.Name, mapping);
                if (expected is null || !JsonNode.DeepEquals(expected, actual)) continue;
                result.Add(culture); break;
            }
        }
        return result;
    }

    /// <summary>为已核验参数表的 Q&A 清理证明完整来源, 不授予日期字段或其他用户元数据改写能力.</summary>
    /// <param name="model">调用方拥有的完整模型, 参数角色和待改对象已由模板配置流程核验.</param>
    /// <returns>已由合法生成日历的完整投影或固定资源原文完整相等证明的 culture, 人工差异不被忽略.</returns>
    internal static IReadOnlyList<TOM.Culture> VerifiedMeasureConfigCultures(TOM.Model model)
    {
        var calendar = model.Tables.Find("Calendar");
        // 一旦声明生成日历, 失败不得改走原文证明, 否则人工恢复不适配当前字段的资源全文也会被错误认领.
        if (calendar?.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null) return VerifiedCultures(calendar);
        var result = new List<TOM.Culture>();
        foreach (var culture in model.Cultures)
        {
            if (culture.LinguisticMetadata is not { ContentType: TOM.ContentType.Json, Content: { Length: > 0 } content }) continue;
            JsonNode? actual;
            try
            {
                using var document = JsonDocument.Parse(content);
                JsonInputValidation.RejectDuplicates(document.RootElement);
                actual = JsonNode.Parse(content);
            }
            catch (JsonException) { continue; }
            // 无生成日历时只比较固定完整原文, 不按表名、局部实体或人工 stamp 猜测投影.
            if (builtinCultures.Value.Any(source => source.Name == culture.Name && JsonNode.DeepEquals(source.Content, actual))) result.Add(culture);
        }
        return result;
    }

    /// <summary>在源表列修改前重新核验旧全文, 用最终实际映射替换已证明的 Q&A 绑定; 其他 culture 完整保留.</summary>
    internal static void Apply(TOM.Table table, IReadOnlyList<ModelConfigurationCalendarColumn> prior,
        IReadOnlyList<ModelConfigurationCalendarColumn> final)
    {
        var actual = ScaffoldCalendarWriter.CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal)).Columns;
        if (!actual.SequenceEqual(prior)) throw new AppException("CONFIGURATION_CALENDAR_MAPPING_CHANGED", "日期字段映射与 Q&A 同步基线不一致, 未修改模型", 3);
        foreach (var culture in VerifiedCultures(table))
        {
            var content = JsonNode.Parse(culture.LinguisticMetadata.Content)!;
            foreach (var source in builtinCultures.Value.Where(value => value.Name == culture.Name && value.TableName == table.Name))
            {
                var expected = Project(source.Content, table.Name, prior);
                if (expected is null || !JsonNode.DeepEquals(content, expected)) continue;
                var projected = Project(source.Content, table.Name, final)
                    ?? throw new AppException("CONFIGURATION_CALENDAR_MAPPING_CHANGED", "日期字段映射无法唯一同步 Q&A, 未修改模型", 3);
                culture.LinguisticMetadata.Content = projected.ToJsonString(); break;
            }
        }
    }

    /// <summary>重建内置转换产生的完整 Q&A, 仅调整目标字段实体和引用其实体键的关系; 多别名无法唯一证明时返回 null.</summary>
    private static JsonObject? Project(JsonObject source, string tableName, IReadOnlyList<ModelConfigurationCalendarColumn> columns)
    {
        var node = source.DeepClone().AsObject();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        if (node["Entities"] is JsonObject entities)
            foreach (var pair in entities.ToArray())
            {
                var binding = pair.Value?["Definition"]?["Binding"];
                if (binding?["ConceptualEntity"]?.GetValue<string>() != tableName || binding["ConceptualProperty"] is not JsonValue property) continue;
                var original = property.GetValue<string>();
                var semantic = ScaffoldCalendarWriter.Semantic(original);
                var matches = columns.Where(column => column.Field == semantic).ToArray();
                if (matches.Length == 0) { entities.Remove(pair.Key); removed.Add(pair.Key); }
                else
                {
                    var selected = matches.SingleOrDefault(column => column.Name == original);
                    if (selected is null && matches.Length != 1) return null;
                    binding["ConceptualProperty"] = (selected ?? matches[0]).Name;
                }
            }
        if (node["Relationships"] is JsonObject relationships)
            foreach (var pair in relationships.Where(pair => References(pair.Value, removed)).ToArray()) relationships.Remove(pair.Key);
        return node;
    }

    /// <summary>检查 Q&A 关系中的实体键引用, 只用于已核验资源的精确转换.</summary>
    private static bool References(JsonNode? value, HashSet<string> keys) => value switch
    {
        JsonObject obj => obj.Any(pair => References(pair.Value, keys)),
        JsonArray array => array.Any(child => References(child, keys)),
        JsonValue scalar => scalar.TryGetValue<string>(out var text) && keys.Contains(text),
        _ => false
    };

    /// <summary>读取当前固定模板的精确 Q&A 全文, 不通过名称重新授予归属.</summary>
    private static BuiltinCulture[] ReadBuiltinCultures()
    {
        var assembly = typeof(ScaffoldTemplateCatalog).Assembly;
        List<BuiltinCulture> result = [];
        foreach (var kind in new[] { "blank" })
        {
            var resources = assembly.GetManifestResourceNames().Where(name => name.Replace('\\', '/').StartsWith("PbiUtils.Scaffold/" + kind + "/", StringComparison.Ordinal)
                && name.Replace('\\', '/').Contains("/definition/cultures/", StringComparison.Ordinal) && name.EndsWith(".tmdl", StringComparison.Ordinal)).ToArray();
            if (resources.Length == 0) throw new AppException("TEMPLATE_RESOURCE_MISSING", "内置模板 Q&A 资源缺失", 3);
            foreach (var resource in resources)
            {
                using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new AppException("TEMPLATE_RESOURCE_MISSING", "内置模板 Q&A 资源缺失", 3);
                using var reader = new StreamReader(stream);
                var (name, content) = ParseBuiltinCulture(reader.ReadToEnd());
                if (content is not null)
                {
                    result.Add(new(name, "Calendar", content));
                    // 模板读取/首次日期转换仍使用原资源; 当前度量生成阶段会精确清理 C01 的 Q&A.
                    // 两个阶段各自以完整来源全文证明, 不把未知字段差异或人工编辑当作可忽略内容.
                    var configured = ScaffoldTemplateMeasureMode.ProjectWithoutMeasureConfig(content, "C01_ReportVisualTemplates");
                    if (!JsonNode.DeepEquals(content, configured)) result.Add(new(name, "Calendar", configured));
                }
            }
        }
        return [.. result];
    }

    /// <summary>解析固定内置 culture 文本; 仅名称后的纯空白表示没有 Q&A, 其他结构与 JSON 必须完整有效.</summary>
    /// <param name="text">嵌入资源的原始 TMDL 文本, 允许 LF 或 CRLF 换行.</param>
    /// <returns>真实 culture 名称和可用于全文归属证明的 JSON 对象; 合法无 Q&A 时内容为 null.</returns>
    /// <exception cref="AppException">culture 头、正文、元数据声明或 JSON 无效时抛出 TEMPLATE_RESOURCE_INVALID.</exception>
    internal static (string Name, JsonObject? Content) ParseBuiltinCulture(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string header = "cultureInfo ";
        const string marker = "\n\tlinguisticMetadata =";
        if (!text.StartsWith(header, StringComparison.Ordinal))
            throw new AppException("TEMPLATE_RESOURCE_INVALID", "内置模板 Q&A 资源格式不受支持", 3);
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0) lineEnd = text.Length;
        var name = text[header.Length..lineEnd].Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            throw new AppException("TEMPLATE_RESOURCE_INVALID", "内置模板 Q&A 资源格式不受支持", 3);
        var metadata = text.IndexOf(marker, StringComparison.Ordinal);
        if (metadata < 0 && text[lineEnd..].Trim().Length == 0) return (name, null);
        var start = metadata < 0 ? -1 : text.IndexOf('{', metadata);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start || text[lineEnd..metadata].Trim().Length != 0
            || text[(metadata + marker.Length)..start].Trim().Length != 0
            || text.IndexOf(marker, metadata + marker.Length, StringComparison.Ordinal) >= 0
            || text[(end + 1)..].Trim() != "contentType: json")
            throw new AppException("TEMPLATE_RESOURCE_INVALID", "内置模板 Q&A 资源格式不受支持", 3);
        try
        {
            if (JsonNode.Parse(text[start..(end + 1)]) is JsonObject content) return (name, content);
        }
        catch (JsonException)
        {
            throw new AppException("TEMPLATE_RESOURCE_INVALID", "内置模板 Q&A 资源格式不受支持", 3);
        }
        throw new AppException("TEMPLATE_RESOURCE_INVALID", "内置模板 Q&A 资源格式不受支持", 3);
    }
}
