// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarColumnProtection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在已有日期表裁剪前从真实模型依赖确定必须保留的语义字段.

using PbiUtils.Application.Code.PowerQuery;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>日期字段依赖保护, 不解析或修改报表文件.</summary>
internal static partial class CalendarColumnProtection
{
    /// <summary>读取可信候选表及所属模型, 返回必须保留的语义; 映射、绑定或元数据错误继续向调用者传播.</summary>
    internal static IReadOnlySet<string> RequiredFields(TOM.Table table)
    {
        var mapping = ScaffoldCalendarWriter.ReadMapping(table).ToDictionary(column => column.Name, column => column.Field, StringComparer.Ordinal);
        var fields = table.Columns.OfType<TOM.DataColumn>().ToDictionary(column => column.Name, column => mapping[column.SourceColumn], StringComparer.OrdinalIgnoreCase);
        var mirror = CalendarGeneratedMirror.Find(table);
        var targets = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [table.Name] = fields };
        if (mirror is not null) targets.Add(mirror.Table.Name, mirror.Fields);
        var required = new HashSet<string>(StringComparer.Ordinal) { "Dates" };
        var model = table.Model;
        var knownTables = (model?.Tables.Select(candidate => candidate.Name) ?? [table.Name]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 排序目标只由外表直接保护; 本表未保留列的自身排序不构成永久保留依据.
        foreach (var column in table.Columns.Concat(mirror?.Table.Columns.AsEnumerable() ?? []).Where(column => column is not TOM.RowNumberColumn && column.IsKey)) Keep(column);
        foreach (var candidate in model?.Tables.AsEnumerable() ?? [table])
        {
            foreach (var level in candidate.Hierarchies.SelectMany(hierarchy => hierarchy.Levels)) Keep(level.Column);
            foreach (var column in candidate.Columns)
            {
                if (candidate != table && candidate != mirror?.Table) Keep(column.SortByColumn);
                if (column.RelatedColumnDetails is not { GroupByColumns.Count: > 0 } details) continue;
                Keep(column);
                foreach (var group in details.GroupByColumns) Keep(group.GroupingColumn);
            }
        }
        if (model is not null)
        {
            foreach (var relationship in model.Relationships.OfType<TOM.SingleColumnRelationship>())
            { Keep(relationship.FromColumn); Keep(relationship.ToColumn); }
            foreach (var permission in model.Roles.SelectMany(role => role.TablePermissions))
                if (permission.Table is { } permissionTable && targets.TryGetValue(permissionTable.Name, out var permissionFields))
                    foreach (var column in permission.ColumnPermissions) KeepName(column.Name, permissionFields);
            foreach (var translation in model.Cultures.SelectMany(culture => culture.ObjectTranslations))
                if (translation.Object is TOM.Column column) Keep(column);
        }

        var definition = model?.Database is TOM.Database database
            ? JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(database))!["model"]!.AsObject()
            : JsonNode.Parse(TOM.JsonSerializer.SerializeObject((TOM.MetadataObject?)model ?? table))!.AsObject();
        var cultureDefinitions = (definition["cultures"] as JsonArray)?.OfType<JsonObject>().ToHashSet() ?? [];
        var tableDefinitions = (definition["tables"] as JsonArray)?.OfType<JsonObject>().ToHashSet() ?? [];
        var mDefinitions = (definition["expressions"] as JsonArray)?.OfType<JsonObject>()
            .Where(expression => Text(expression["kind"]) is "m" or "M").ToHashSet() ?? [];
        var mSources = tableDefinitions.SelectMany(value => (value["partitions"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(partition => partition["source"]).OfType<JsonObject>().Where(source => Text(source["type"]) is "m" or "M").ToHashSet();
        var generated = table.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null;
        if (generated && definition["tables"] is JsonArray tables)
            foreach (var item in tables.Where(item => item?["name"]?.GetValue<string>() == table.Name)) item!.AsObject().Remove("partitions");
        else if (generated) definition.Remove("partitions");
        if (mirror is not null && definition["tables"] is JsonArray allTables)
            foreach (var item in allTables.Where(item => item?["name"]?.GetValue<string>() == mirror.Table.Name))
            {
                // 完整已归属镜像将与最终选集同步; 仅排除其已核验生成分区和直接列映射, 业务引用仍扫描.
                item!.AsObject().Remove("partitions");
                if (item["columns"] is JsonArray columns)
                    foreach (var column in columns.OfType<JsonObject>()) column.Remove("sourceColumn");
            }
        var generatedCultures = CalendarGeneratedLinguisticMetadata.VerifiedCultures(table).Select(culture => culture.Name).ToHashSet(StringComparer.Ordinal);
        if (definition["cultures"] is JsonArray cultures)
            foreach (var culture in cultures.OfType<JsonObject>().Where(culture => generatedCultures.Contains(Text(culture["name"]) ?? "")))
                culture.Remove("linguisticMetadata");
        var ownFunction = ScaffoldCalendarWriter.FunctionBinding(table)?.FunctionName;
        if (ownFunction is not null && definition["expressions"] is JsonArray expressions)
            foreach (var item in expressions.Where(item => item?["name"]?.GetValue<string>() == ownFunction).ToArray()) expressions.Remove(item);
        if (definition["perspectives"] is JsonArray perspectives)
            foreach (var perspective in perspectives)
                if (perspective?["tables"] is JsonArray visible)
                    foreach (var item in visible.Where(item => targets.ContainsKey(Text(item?["name"]) ?? "")))
                    {
                        var perspectiveFields = targets[Text(item?["name"])!];
                        if (item?["includeAll"]?.GetValue<bool>() == true) required.UnionWith(perspectiveFields.Values);
                        else if (item?["columns"] is JsonArray columns)
                            foreach (var column in columns) KeepName(column?["name"]?.GetValue<string>(), perspectiveFields);
                        else required.UnionWith(perspectiveFields.Values);
                        if (item?["hierarchies"] is JsonArray hierarchies && hierarchies.Count > 0) required.UnionWith(perspectiveFields.Values);
                    }
        Scan(definition);
        return required;

        /// <summary>按模型引用保护目标表列; 空引用或其他表不增加保护字段.</summary>
        void Keep(TOM.Column? column)
        {
            if (column?.Table is { } owner && targets.TryGetValue(owner.Name, out var referencedFields)) KeepName(column.Name, referencedFields);
        }
        /// <summary>按实际输出名映射语义; 无法定位列的目标表引用保守保护全部字段.</summary>
        void KeepName(string? name, IReadOnlyDictionary<string, string>? referencedFields = null)
        {
            referencedFields ??= fields;
            if (name is not null && referencedFields.TryGetValue(name, out var field)) required.Add(field);
            else required.UnionWith(referencedFields.Values);
        }
        /// <summary>检查 TOM 表达式和业务元数据绑定, 归属及扩展属性不作为模型依赖.</summary>
        void Scan(JsonNode? node, int linguisticContext = -1, bool inLinguistic = false, bool refreshPolicy = false)
        {
            if (refreshPolicy && (node is not JsonObject policy || Text(policy["policyType"]) != "basic"))
            { required.UnionWith(fields.Values); return; }
            // 官方声明使用字典路径; 数组继续检查真实引用, 但不能继承作用域豁免.
            if (node is JsonArray array) { foreach (var child in array) Scan(child, inLinguistic: inLinguistic); return; }
            if (node is not JsonObject obj) return;
            if (inLinguistic)
            {
                // 混用身份或属性别名不能通过优先级掩盖另一条依赖, 无法消歧时保留完整输出.
                string?[] tableBindings = [Text(obj["table"]), Text(obj["ConceptualEntity"]), Text(obj["Table"])];
                string?[] columnBindings = [Text(obj["column"]), Text(obj["ConceptualProperty"]), Text(obj["Column"]), Text(obj["Measure"])];
                if (tableBindings.Count(value => value is not null) > 1 || columnBindings.Count(value => value is not null) > 1)
                { required.UnionWith(fields.Values); return; }
            }
            var qaTable = Text(obj["ConceptualEntity"]) ?? (inLinguistic ? Text(obj["Table"]) : null);
            var target = Text(obj["table"]) ?? qaTable;
            // 标准实体与关系的单字段表绑定只声明作用域; 未知路径和扩展仍保守保护.
            var tableEntity = linguisticContext == 4 && obj.Count == 1 && qaTable is not null;
            if (!tableEntity && target is not null && targets.TryGetValue(target, out var referencedFields))
                KeepName(Text(obj["column"]) ?? Text(obj["ConceptualProperty"])
                    ?? (inLinguistic ? Text(obj["Column"]) ?? Text(obj["Measure"]) : null), referencedFields);
            foreach (var pair in obj)
            {
                if (pair.Key is "annotations" or "extendedProperties") continue;
                if (pair.Key == "refreshPolicy" && tableDefinitions.Contains(obj)) Scan(pair.Value, refreshPolicy: true);
                else if (pair.Key == "linguisticMetadata" && cultureDefinitions.Contains(obj))
                {
                    var contentNode = pair.Value is JsonObject metadata ? metadata["content"] : pair.Value;
                    if (contentNode is JsonObject or JsonArray) Scan(contentNode, LinguisticRootContext(contentNode), inLinguistic: true);
                    else if (Text(contentNode) is { Length: > 0 } content)
                    {
                        try
                        {
                            var parsed = JsonNode.Parse(content);
                            Scan(parsed, LinguisticRootContext(parsed), inLinguistic: true);
                        }
                        catch (JsonException) { required.UnionWith(fields.Values); }
                    }
                    else if (contentNode is not null) required.UnionWith(fields.Values);
                }
                else if (pair.Key.EndsWith("expression", StringComparison.OrdinalIgnoreCase)
                    || pair.Key == "sourceColumn" && obj["type"]?.GetValue<string>() == "calculatedTableColumn")
                {
                    var isM = mDefinitions.Contains(obj) || mSources.Contains(obj)
                        || refreshPolicy && pair.Key is "sourceExpression" or "pollingExpression";
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) InspectExpression(text, isM);
                    else if (pair.Value is JsonArray lines && lines.All(line => Text(line) is not null)) InspectExpression(string.Join("\n", lines.Select(Text)), isM);
                    else if (pair.Value is not null) { required.UnionWith(fields.Values); Scan(pair.Value, inLinguistic: inLinguistic); }
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
        /// <summary>只有公开 LSDL 根字段完整已知时允许标准路径豁免; 人工未知扩展仍扫描真实引用.</summary>
        static int LinguisticRootContext(JsonNode? node) => node is JsonObject root && root.All(pair => pair.Key is
            "Version" or "Language" or "DynamicImprovement" or "Namespaces" or "Entities" or "Relationships"
            or "GlobalSubstitutions" or "Examples" or "MinResultConfidence" or "Agents") ? 0 : -1;

        /// <summary>真实 M 复用有界查询词法; 局部记录字段不绑定模型列, 实际或未知查询引用保护完整输出.</summary>
        void InspectExpression(string text, bool isM)
        {
            if (!isM) { Inspect(text); return; }
            foreach (var (name, referencedFields) in targets)
                if (PowerQueryFormatter.MayReferenceQuery(text, name)) required.UnionWith(referencedFields.Values);
            if (ownFunction is not null && PowerQueryFormatter.MayReferenceQuery(text, ownFunction)) required.UnionWith(fields.Values);
        }

        /// <summary>非 M 表达式继续检查明确列名与整体表引用, 不把未知语言当成无依赖.</summary>
        void Inspect(string text)
        {
            // 此处是保守引用识别, 不声称完整 DAX/M 解析; 无法证明列粒度的表引用保留全部字段.
            text = NonReferences().Replace(text, match => match.Value.StartsWith("#\"", StringComparison.Ordinal)
                || match.Value.StartsWith('\'') || match.Value.StartsWith('[') ? match.Value : " ");
            if (DynamicReferences().IsMatch(text)) required.UnionWith(fields.Values);
            // 先整体消费已限定表列, 避免其他表的同名列被再次当作未限定日期字段.
            var remaining = QualifiedColumnReferences().Replace(text, match =>
            {
                var name = Identifier(match.Groups["table"].Value);
                // RETURN [列]、NOT [列] 等相邻词元不是表限定; 只消费当前模型可核实的表名.
                if (!knownTables.Contains(name)) return match.Value;
                if (targets.TryGetValue(name, out var referencedFields))
                    KeepName(match.Groups["column"].Value.Replace("]]", "]", StringComparison.Ordinal), referencedFields);
                return " ";
            });
            foreach (Match reference in ColumnReferences().Matches(remaining))
                foreach (var referencedFields in targets.Values)
                    if (referencedFields.TryGetValue(reference.Groups["column"].Value.Replace("]]", "]", StringComparison.Ordinal), out var field)) required.Add(field);
            // 表和函数名按完整标识符词元比较, 不在其他引号标识符或列名内部搜索同名前缀.
            remaining = ColumnReferences().Replace(remaining, " ");
            foreach (Match reference in IdentifierReferences().Matches(remaining))
            {
                var name = Identifier(reference.Value);
                if (targets.TryGetValue(name, out var referencedFields)) required.UnionWith(referencedFields.Values);
                if (string.Equals(name, ownFunction, StringComparison.OrdinalIgnoreCase)) required.UnionWith(fields.Values);
            }
        }
        /// <summary>解码完整 DAX/M 标识符, 保留其名称内容并只处理相应语言的引号转义.</summary>
        static string Identifier(string value) => value.StartsWith("#\"", StringComparison.Ordinal)
            ? value[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : value.StartsWith('\'') ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : value;
        /// <summary>安全读取 JSON 字符串; 对象或其他标量交由调用者保守处理.</summary>
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>识别完整引号标识符、字符串和注释, 让调用者只移除不产生模型依赖的词元.</summary>
    [GeneratedRegex("""\[(?:[^\]]|\]\])+\]|#"(?:[^"]|"")*"|'(?:[^']|'')*'|"(?:[^"]|"")*"|//[^\r\n]*|--[^\r\n]*|/\*[\s\S]*?\*/""", RegexOptions.CultureInvariant)]
    private static partial Regex NonReferences();

    /// <summary>读取方括号列名及双右括号转义, 返回引用词元匹配器.</summary>
    [GeneratedRegex(@"\[(?<column>(?:[^\]]|\]\])+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnReferences();

    /// <summary>整体读取已限定的表列引用, 包括完整引号标识符及转义列名.</summary>
    [GeneratedRegex("""(?<table>#"(?:[^"]|"")*"|'(?:[^']|'')*'|[\p{L}_][\p{L}\p{N}_]*)\s*\[(?<column>(?:[^\]]|\]\])+)\]""", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedColumnReferences();

    /// <summary>读取完整表或函数标识符, 不把其他引号词元内部的同名文本视为独立引用.</summary>
    [GeneratedRegex("""#"(?:[^"]|"")*"|'(?:[^']|'')*'|[\p{L}_][\p{L}\p{N}_]*""", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierReferences();

    /// <summary>动态 M 环境或求值无法静态证明输出粒度, 标记后保护全部日期字段.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:#shared|#sections|Expression\s*\.\s*Evaluate)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DynamicReferences();
}
