// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarColumnRenameReferences.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保真改写日期列的真实 DAX 引用并保护未知表达式与人工 Q&A 元数据.

using PbiUtils.Application.Code.Dax;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>只使用完整模型和显式更名表消歧, 不将词法成功当作引擎语义验证.</summary>
internal sealed partial class CalendarColumnRenameReferences(TOM.Model model, TOM.Table table, IReadOnlyDictionary<string, string> renames)
{
    private static readonly string[] localColumnFunctions = ["ADDCOLUMNS", "SELECTCOLUMNS", "ROW", "DATATABLE", "SUMMARIZE", "SUMMARIZECOLUMNS", "GROUPBY", "SUBSTITUTEWITHINDEX"];
    private readonly HashSet<string> ambiguousNames = model.Tables.Where(value => value != table).SelectMany(value => value.Columns).Select(value => value.Name)
        .Concat(model.Tables.SelectMany(value => value.Measures).Select(value => value.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>复用现有 DAX 词法器解码引用, 只替换真实方括号词元, 字符串/注释/其他表保留.</summary>
    /// <param name="expression">SDK 真实表达式原文.</param>
    /// <param name="token">词法预检取消令牌.</param>
    /// <returns>仅目标列名发生改变的表达式.</returns>
    /// <exception cref="PbiUtils.Core.AppException">未限定引用存在同名列/度量、虚拟列作用域或词法不完整时拒绝.</exception>
    internal string Rewrite(string expression, CancellationToken token)
    {
        if (renames.Count == 0) return expression;
        if (!DaxFormatter.TryReadMeasureReferences(expression, out var references, token)) throw CalendarColumnRename.Blocked();
        var localColumns = false;
        if (references.Any(reference => reference.Table is null && renames.ContainsKey(reference.Name)))
        {
            if (!DaxFormatter.TryReadFunctionCalls(expression, out var calls, token)) throw CalendarColumnRename.Blocked();
            // 物理模型没有同名列不代表未限定词元来自日期表; 虚拟表可声明自己的列, 无完整绑定证明时保持原公式.
            localColumns = calls.Overlaps(localColumnFunctions);
        }
        var index = 0;
        return ReferenceTokens().Replace(expression, match =>
        {
            token.ThrowIfCancellationRequested();
            if (!match.Value.StartsWith('[')) return match.Value;
            var (Table, Name) = references[index++];
            if (!renames.TryGetValue(Name, out var name)) return match.Value;
            if (Table is { } qualifier)
            {
                if (!qualifier.Equals(table.Name, StringComparison.OrdinalIgnoreCase)) return match.Value;
            }
            else if (localColumns || ambiguousNames.Contains(Name)) throw CalendarColumnRename.Blocked();
            return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
        });
    }

    /// <summary>完整 SDK 定义中需要改写的表达式都必须有明确 setter, 未知扩展不静默遗留悬空引用.</summary>
    internal void RequireKnownExpressions(IEnumerable<string> known, CancellationToken token)
    {
        var counts = known.GroupBy(Normalize, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Scan(ModelConfigurationOwnership.Definition(model));
        void Scan(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Scan(child); return; }
            if (node is not JsonObject obj) return;
            var nonDax = Text(obj["kind"]) is "m" or "M" || Text(obj["type"]) is "m" or "M" or "query";
            foreach (var pair in obj)
            {
                token.ThrowIfCancellationRequested();
                if (pair.Key is "annotations" or "extendedProperties" or "linguisticMetadata" or "refreshPolicy") continue;
                if (pair.Key.EndsWith("expression", StringComparison.OrdinalIgnoreCase)
                    || pair.Key == "sourceColumn" && Text(obj["type"]) == "calculatedTableColumn")
                {
                    if (nonDax || pair.Value is null) continue;
                    if (pair.Value is JsonObject) { Scan(pair.Value); continue; }
                    var expression = (Text(pair.Value) ?? (pair.Value is JsonArray lines && lines.All(line => Text(line) is not null) ? string.Join('\n', lines.Select(Text)) : null)) ?? throw CalendarColumnRename.Blocked();
                    if (Rewrite(expression, token) == expression) continue;
                    var key = Normalize(expression);
                    if (!counts.TryGetValue(key, out var count) || count == 0) throw CalendarColumnRename.Blocked();
                    counts[key] = count - 1;
                }
                else Scan(pair.Value);
            }
        }
    }

    /// <summary>只有已证明的生成 Q&A 可由共享同步器重建, 人工内容引用待更名列时明确拒绝.</summary>
    internal void RequireLinguistic(CancellationToken token)
    {
        if (renames.Count == 0) return;
        var verified = CalendarGeneratedLinguisticMetadata.VerifiedCultures(table).ToHashSet();
        foreach (var culture in model.Cultures.Where(value => !verified.Contains(value)))
        {
            if (culture.LinguisticMetadata?.Content is not { Length: > 0 } content) continue;
            try { Scan(JsonNode.Parse(content)); }
            catch (JsonException) { throw CalendarColumnRename.Blocked(); }
        }
        void Scan(JsonNode? node, IReadOnlyList<string>? scope = null)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonArray array) { foreach (var child in array) Scan(child, scope); return; }
            if (node is not JsonObject obj) return;
            string?[] tableBindings = [Text(obj["table"]), Text(obj["Table"]), Text(obj["ConceptualEntity"])];
            string?[] columnBindings = [Text(obj["column"]), Text(obj["Column"]), Text(obj["ConceptualProperty"]),
                Text(obj["Measure"]), Text(obj["measure"]), Text(obj["measureName"]), Text(obj["defaultMeasure"])];
            var targets = tableBindings.OfType<string>().ToArray();
            var columns = columnBindings.OfType<string>().ToArray();
            IReadOnlyList<string> actualScope = targets.Length > 0 ? targets : scope ?? [];
            // 混用身份不能通过 coalesce 隐藏另一条日期绑定; 未知未限定属性也不获得无依赖证明.
            if (actualScope.Any(target => target.Equals(table.Name, StringComparison.OrdinalIgnoreCase))
                && (targets.Length > 1 || columns.Length > 1 || columns.Any(renames.ContainsKey))
                || actualScope.Count == 0 && columns.Any(renames.ContainsKey)) throw CalendarColumnRename.Blocked();
            foreach (var pair in obj)
                if (pair.Key.EndsWith("expression", StringComparison.OrdinalIgnoreCase) && Text(pair.Value) is { } expression)
                { if (Rewrite(expression, token) != expression) throw CalendarColumnRename.Blocked(); }
                else Scan(pair.Value, actualScope);
        }
    }

    /// <summary>模型序列化的数组表达式只规范换行, 保留其余业务字符.</summary>
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>仅接受真实 JSON 字符串, 其他形状由调用方显式处理.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>最长匹配字符串、完整标识符与注释, 方括号引用的顺序与共享词法器一致.</summary>
    [GeneratedRegex("""//[^\r\n]*|--[^\r\n]*|/\*[\s\S]*?\*/|"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]""", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceTokens();
}
