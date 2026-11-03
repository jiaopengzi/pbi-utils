// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\TimeIntelligence\ScaffoldPeriodProtection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从完整 TOM 可写定义保护期间缩减后的存活 DAX 与元数据引用, 未知结构失败关闭.

using PbiUtils.Application.Code.Dax;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar.TimeIntelligence;

/// <summary>只读检查待删除闭包, 不修改模型或读取报表文件.</summary>
internal static class ScaffoldPeriodProtection
{
    /// <summary>排除本轮整体删除的对象后验证完整模型, 有存活引用或未知表达式时拒绝整批缩减.</summary>
    /// <param name="model">调用方拥有的当前 TOM 原对象, 可提供本轮最终定义只读扫描视图.</param>
    /// <param name="removed">即将整体删除的期间集合, 集合内部依赖不阻止整批删除.</param>
    /// <param name="replaced">移动预检时明确将整体重写或删除的旧期间根; 不豁免它们的透视、翻译和 Q&amp;A 绑定.</param>
    /// <param name="finalDefinition">原对象批次的只读最终扫描视图; 不构建或导入另一个 TOM 模型.</param>
    /// <exception cref="AppException">CONFIGURATION_PERIOD_REFERENCED 表示引用仍存在或无法证明安全, 模型保持原状.</exception>
    internal static void RequireUnreferenced(TOM.Model model, IReadOnlyCollection<TOM.Measure> removed, IReadOnlyCollection<TOM.Measure>? replaced = null,
        JsonNode? finalDefinition = null)
    {
        if (removed.Count == 0) return;
        if (removed.Concat(replaced ?? []).Any(measure => measure.Model != model)) throw Blocked();
        var names = removed.Select(measure => measure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = removed.GroupBy(measure => measure.Table.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(measure => measure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var knownTables = model.Tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var visible in model.Perspectives.SelectMany(perspective => perspective.PerspectiveTables))
            if (targets.TryGetValue(visible.Table?.Name ?? visible.Name, out var tableNames)
                && (visible.IncludeAll || visible.PerspectiveMeasures.Any(measure => tableNames.Contains(measure.Measure?.Name ?? measure.Name)))) throw Blocked();
        foreach (var translation in model.Cultures.SelectMany(culture => culture.ObjectTranslations))
            if (translation.Object is TOM.Measure measure && Matches(measure.Table?.Name, measure.Name)) throw Blocked();

        // 仅修改序列化副本, 被删除对象自己的 DAX、KPI 和动态格式不再是存活根.
        var excluded = removed.Concat(replaced ?? []).GroupBy(measure => measure.Table.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(measure => measure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var definition = finalDefinition ?? ModelConfigurationOwnership.Definition(model);
        var tableDefinitions = (definition["tables"] as JsonArray)?.OfType<JsonObject>().ToHashSet() ?? [];
        var cultureDefinitions = (definition["cultures"] as JsonArray)?.OfType<JsonObject>().ToHashSet() ?? [];
        if (definition["tables"] is JsonArray tables)
            foreach (var table in tables.OfType<JsonObject>())
                if (Text(table["name"]) is { } tableName && excluded.TryGetValue(tableName, out var tableNames) && table["measures"] is JsonArray measures)
                    foreach (var measure in measures.Where(measure => Text(measure?["name"]) is { } name && tableNames.Contains(name)).ToArray()) measures.Remove(measure);
        Scan(definition);

        /// <summary>已知其他表的同名列可消歧, 未知限定名仍按可能的未限定引用保守保护.</summary>
        bool Matches(string? table, string name) => names.Contains(name)
            && (table is null || !knownTables.Contains(table) || targets.TryGetValue(table, out var tableNames) && tableNames.Contains(name));

        /// <summary>扫描 SDK 表达式根和业务元数据; 已知 M/原生查询不作为 DAX, 注解不作为模型引用.</summary>
        void Scan(JsonNode? node, int linguisticContext = -1, bool refreshPolicy = false, bool inLinguistic = false)
        {
            // 官方 Q&A 声明路径要求字典对象, 数组只继续检查引用, 不继承标准路径的豁免状态.
            if (node is JsonArray array) { foreach (var child in array) Scan(child, inLinguistic: inLinguistic); return; }
            if (node is not JsonObject obj) return;
            if (refreshPolicy && Text(obj["policyType"]) != "basic") throw Blocked();
            var qaTable = Text(obj["ConceptualEntity"]) ?? (inLinguistic ? Text(obj["Table"]) : null);
            if (inLinguistic)
            {
                // 同一绑定只应有一种表身份和一种属性身份; 多别名不得通过优先级隐藏另一条真实引用.
                string?[] tableBindings = [Text(obj["table"]), Text(obj["ConceptualEntity"]), Text(obj["Table"])];
                string?[] propertyBindings = [Text(obj["measure"]), Text(obj["measureName"]), Text(obj["defaultMeasure"]),
                    Text(obj["ConceptualProperty"]), Text(obj["Measure"]), Text(obj["Column"])];
                if (tableBindings.Count(value => value is not null) > 1 || propertyBindings.Count(value => value is not null) > 1) throw Blocked();
            }
            var boundTable = Text(obj["table"]) ?? qaTable;
            var boundMeasure = Text(obj["measure"]) ?? Text(obj["measureName"]) ?? Text(obj["defaultMeasure"])
                ?? Text(obj["ConceptualProperty"]) ?? (inLinguistic ? Text(obj["Measure"]) ?? Text(obj["Column"]) : null);
            if (boundMeasure is not null && Matches(boundTable, boundMeasure)) throw Blocked();
            // 标准 Q&A 实体与关系的单字段表绑定只声明作用域, 不绑定每个度量值; 未知扩展继续保护.
            var tableEntity = linguisticContext == 4 && obj.Count == 1 && qaTable is not null;
            if (!tableEntity && qaTable is { } entity && targets.ContainsKey(entity) && boundMeasure is null) throw Blocked();
            var nonDax = Text(obj["kind"]) is "m" or "M" || Text(obj["type"]) is "m" or "M" or "query";
            foreach (var pair in obj)
            {
                if (pair.Key is "annotations" or "extendedProperties") continue;
                // SDK BasicRefreshPolicy 的两个查询字段明确为 M; 只在真实策略容器豁免, 其他表达式继续完整扫描.
                if (refreshPolicy && pair.Key is "sourceExpression" or "pollingExpression") continue;
                if (pair.Key == "refreshPolicy" && tableDefinitions.Contains(obj)) Scan(pair.Value, refreshPolicy: true, inLinguistic: inLinguistic);
                else if (pair.Key == "linguisticMetadata" && cultureDefinitions.Contains(obj)) ScanLinguistic(pair.Value);
                else if (pair.Key.EndsWith("expression", StringComparison.OrdinalIgnoreCase)
                    || pair.Key == "sourceColumn" && Text(obj["type"]) == "calculatedTableColumn")
                {
                    if (nonDax || pair.Value is null) continue;
                    if (pair.Value is JsonObject && pair.Key is "noSelectionExpression" or "multipleOrEmptySelectionExpression") { Scan(pair.Value, inLinguistic: inLinguistic); continue; }
                    var expression = Text(pair.Value) ?? (pair.Value is JsonArray lines && lines.All(line => Text(line) is not null) ? string.Join('\n', lines.Select(Text)) : null);
                    if (expression is null || !DaxFormatter.TryReadMeasureReferences(expression, out var references)) throw Blocked();
                    if (references.Any(reference => Matches(reference.Table, reference.Name))) throw Blocked();
                }
                else Scan(pair.Value, linguisticContext switch
                {
                    0 when pair.Key == "Entities" => 1,
                    0 when pair.Key == "Relationships" => 5,
                    1 => 2,
                    2 when pair.Key == "Definition" => 3,
                    3 when pair.Key == "Binding" => 4,
                    5 => 6,
                    6 when pair.Key == "Binding" => 4,
                    _ => -1
                }, inLinguistic: inLinguistic);
            }
        }

        /// <summary>Q&amp;A 的 JSON 内容继续按真实绑定扫描, 非 JSON 或破损内容不能证明没有依赖.</summary>
        void ScanLinguistic(JsonNode? node)
        {
            if (node is null) return;
            var content = node is JsonObject metadata ? metadata["content"] : node;
            if (content is JsonObject or JsonArray) { Scan(content, 0, inLinguistic: true); return; }
            if (Text(content) is not { } text) throw Blocked();
            try { Scan(JsonNode.Parse(text) ?? throw Blocked(), 0, inLinguistic: true); }
            catch (JsonException) { throw Blocked(); }
        }
    }

    /// <summary>读取 SDK JSON 字符串, 其他类型由调用方按未知结构处理.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>稳定安全错误不包含模型名、表达式或用户数据.</summary>
    private static AppException Blocked() => new("CONFIGURATION_PERIOD_REFERENCED", "待移除的时间智能度量值仍被模型引用, 或其依赖无法安全核实; 请先处理引用后重试", 3);
}
