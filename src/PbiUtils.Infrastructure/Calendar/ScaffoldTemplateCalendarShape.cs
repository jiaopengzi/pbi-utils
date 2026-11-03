// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\ScaffoldTemplateCalendarShape.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 只在已核实内置模板副本内裁剪日期字段并同步明确依赖.

using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Application.Calendar;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>调用者已验证内置资源身份; 不用于已有工程或用户定义.</summary>
internal static class ScaffoldTemplateCalendarShape
{
    internal static void Apply(TOM.Model model, TOM.Table table, ScaffoldCalendarOptions options, bool retail)
    {
        var fields = ScaffoldCalendarFields.Resolve(options).Select(value => value.Field).ToHashSet(StringComparer.Ordinal);
        if (retail) fields.UnionWith(["Dates", "StartOfMonth", "StartOfYear"]);
        var names = table.Columns.ToDictionary(column => column.Name, column => fields.Contains(ScaffoldCalendarWriter.Semantic(column.Name))
            ? ScaffoldCalendarFields.ColumnName(ScaffoldCalendarWriter.Semantic(column.Name), options.NumberedColumns ?? true) : null, StringComparer.Ordinal);
        // 先解除对象排序引用, 后续writer仅在辅助列同时存在时重新绑定.
        foreach (var column in table.Columns) column.SortByColumn = null;
        foreach (var column in table.Columns.OfType<TOM.DataColumn>().ToArray())
        {
            if (names[column.Name] is not { } name) table.Columns.Remove(column);
            else { column.Name = name; column.SourceColumn = name; }
        }
        if (retail)
        {
            var mirror = model.Tables["CalendarMirror"];
            foreach (var column in mirror.Columns) column.SortByColumn = null;
            foreach (var column in mirror.Columns.OfType<TOM.CalculatedTableColumn>().ToArray())
            {
                if (!names.TryGetValue(column.Name, out var name)) throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "内置镜像字段与日期角色不一致", 3);
                if (name is null) mirror.Columns.Remove(column);
                else { column.Name = name; column.SourceColumn = "Calendar[" + name + "]"; }
            }
            // 仅替换固定内置Measure表的完整字段引用, 不扫描或猜测用户表达式.
            foreach (var measure in model.Tables["Measure"].Measures)
                foreach (var pair in names.Where(pair => pair.Value is not null && pair.Key != pair.Value))
                    foreach (var target in new[] { "Calendar", "CalendarMirror" })
                    {
                        measure.Expression = measure.Expression.Replace(target + "[" + pair.Key + "]", target + "[" + pair.Value + "]", StringComparison.Ordinal)
                            .Replace("'" + target + "'[" + pair.Key + "]", "'" + target + "'[" + pair.Value + "]", StringComparison.Ordinal);
                    }
        }
        foreach (var culture in model.Cultures)
        {
            if (culture.LinguisticMetadata is not { Content: { Length: > 0 } content } metadata) continue;
            var node = JsonNode.Parse(content)!.AsObject();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            if (node["Entities"] is JsonObject entities)
                foreach (var pair in entities.ToArray())
                {
                    var binding = pair.Value?["Definition"]?["Binding"];
                    if (binding?["ConceptualEntity"]?.GetValue<string>() != table.Name || binding["ConceptualProperty"] is not JsonValue property) continue;
                    if (!names.TryGetValue(property.GetValue<string>(), out var name)) continue;
                    if (name is null) { entities.Remove(pair.Key); removed.Add(pair.Key); }
                    else binding["ConceptualProperty"] = name;
                }
            // 删除引用已移除字段实体的Q&A关系, 保留其他业务实体及其词汇.
            static bool References(JsonNode? value, HashSet<string> keys) => value switch
            {
                JsonObject obj => obj.Any(pair => References(pair.Value, keys)),
                JsonArray array => array.Any(child => References(child, keys)),
                JsonValue scalar => scalar.TryGetValue<string>(out var text) && keys.Contains(text),
                _ => false
            };
            if (node["Relationships"] is JsonObject relationships)
                foreach (var pair in relationships.Where(pair => References(pair.Value, removed)).ToArray()) relationships.Remove(pair.Key);
            metadata.Content = node.ToJsonString();
        }
    }
}
