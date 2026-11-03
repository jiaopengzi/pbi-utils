// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\TemplateMeasures\ScaffoldTemplateMeasureMode.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享模板度量存储切换, 精确物化旧查值并保护参数表的未知引用.

using PbiUtils.Application.Code.Dax;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;

using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.Models;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.TemplateMeasures;

/// <summary>仅在调用方原对象或自有 staging 模型上组合, 不执行提交、刷新或文件保存.</summary>
internal static partial class ScaffoldTemplateMeasureMode
{
    /// <summary>按本轮明确配置直接创建或覆盖对象; 移除项核实存活引用后真删, 禁用项保留并隐藏.</summary>
    /// <param name="model">调用方拥有的完整 TOM 模型.</param>
    /// <param name="definitions">完整六字段配置, 空集仍代表明确模式切换.</param>
    /// <param name="mode">tmdl 或 config.</param>
    /// <param name="roles">本轮唯一角色字典, 随模型候选一同持久化.</param>
    /// <param name="configuration">已有模型配置模式, 不以历史模板归属限制显式覆盖.</param>
    /// <param name="builtin">仅新建受信内置模板可转换原始模板对象.</param>
    /// <param name="reservedTableNames">同批稍后创建的表名, Config 变量不得使用这些表名.</param>
    /// <param name="sourceDefinitions">当前 Config 的已核实来源行; null 使用模型旧值, 空集或缺键物化为 BLANK(), 存在空值行仍为空字符串, TMDL 隐藏快照仍使用其模型旧值.</param>
    /// <exception cref="AppException">真实删除项存在存活引用或目标名称不明确时拒绝修改.</exception>
    internal static void Apply(TOM.Model model, TemplateMeasureDefinition[] definitions, string mode,
        Dictionary<string, string> roles, bool configuration, bool builtin = false, IEnumerable<string>? reservedTableNames = null,
        TemplateMeasureDefinition[]? sourceDefinitions = null)
    {
        var prior = TemplateMeasureConfigurationStorage.Display(model);
        var configRole = roles.Where(pair => pair.Value == "measure-config").OrderByDescending(pair => model.Tables.ContainsName(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault().Key;
        var configName = configRole ?? AvailableConfigName(model, roles);
        // 明确 Config 可认领默认参数表; 普通 TMDL 不按默认名称退休无角色、无旧配置的用户表.
        var config = configRole is not null || mode == "config" || prior?.Mode == "config" ? model.Tables.Find(configName) : null;
        var removed = new List<TOM.Measure>();
        if (prior is not null)
        {
            foreach (var item in prior.Measures)
            {
                var measure = model.Tables.SelectMany(table => table.Measures).FirstOrDefault(value => value.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (measure is null) continue;
                if (!definitions.Any(value => value.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))) removed.Add(measure);
            }
        }
        if (config is not null && (builtin || prior is null && configRole is not null)) MaterializeTemplate(model, config, definitions, configuration);
        // 禁用只隐藏原对象, 保留原业务表达式; 若它仍查参数表, 切 TMDL 时由引用保留与内嵌快照保持有效.
        ScaffoldTemplateMeasures.Apply(model, definitions, mode, configName, reservedTableNames);
        var retained = Array.Empty<TemplateMeasureDefinition>();
        try { ScaffoldPeriodProtection.RequireUnreferenced(model, removed); }
        catch (AppException error) when (error.Code == "CONFIGURATION_PERIOD_REFERENCED")
        {
            if (prior is null || prior.Mode == mode)
                throw new AppException("CONFIGURATION_MEASURE_REFERENCED", "待移除的模板度量值仍被模型引用, 或其依赖无法安全核实; 请先处理引用后重试", 3);
            // 模式变化优先完成切换, 只保留实际存活根和它们的依赖闭包; 无关旧项继续真正删除.
            var protectedMeasures = ReferencedRemovals(model, removed);
            retained = [.. prior.Measures.Where(item => protectedMeasures.Any(measure => measure.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))];
            if (config is not null)
                foreach (var measure in protectedMeasures)
                {
                    var item = retained.Single(item => measure.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    var sourceRowExists = true;
                    // 当前 Config 的磁盘行是查值权威; 只替换旧值, 不用外部六字段重认领公式或覆盖元数据.
                    if (prior.Mode == "config" && sourceDefinitions is not null)
                    {
                        var source = sourceDefinitions.FirstOrDefault(source => source.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                        sourceRowExists = source is not null;
                        if (source is not null) item = item with { Value = source.Value };
                    }
                    // 移出清单的已知查值物化为独立 DAX, 含禁用项; 未知或已手改业务式及其元数据保持原样.
                    if (!ScaffoldTemplateMeasures.TryMaterializeConfigExpression(measure.Expression, config.Name, item, out var expression, sourceRowExists)) continue;
                    measure.Expression = expression;
                    if (configuration) ModelConfigurationOwnership.Mark(measure, measure.Annotations);
                }
            removed.RemoveAll(protectedMeasures.Contains);
        }
        foreach (var measure in removed) measure.Table.Measures.Remove(measure);
        if (config is not null) RemoveProvenLinguisticBindings(model, config.Name, builtin);
        if (mode == "config")
        {
            if (config is null)
            {
                config = new TOM.Table { Name = configName };
                model.Tables.Add(config);
            }
            RepairColumns(config);
            config.IsHidden = true;
            ModelImportPartition.Set(config, WithExtraSources(config, TemplateMeasureConfigurationStorage.Partition()));
            ModelConfigurationOwnership.Mark(config, config.Annotations);
            roles[configName] = "measure-config";
        }
        else if (config is not null)
        {
            if (!CanRemove(model, config))
            {
                // 用户仍引用参数表时保留同一对象, 数据改为本轮内嵌快照, 避免退休 JSON 后产生断源.
                RepairColumns(config);
                config.IsHidden = true;
                ModelImportPartition.Set(config, WithExtraSources(config, Snapshot([.. definitions, .. retained])));
                ModelConfigurationOwnership.Mark(config, config.Annotations);
                roles[configName] = "measure-config";
            }
            else
            {
                model.Tables.Remove(config);
                roles.Remove(configName);
                RemoveQueryOrder(model, configName);
            }
        }
        // 本轮只保留实际使用的唯一参数角色; 退休缺失/多余的内部映射, 不删除其余实际模型对象.
        foreach (var name in roles.Where(pair => pair.Value == "measure-config"
            && (pair.Key != configName || !model.Tables.ContainsName(pair.Key))).Select(pair => pair.Key).ToArray()) roles.Remove(name);
        TemplateMeasureConfigurationStorage.WriteState(model, mode, definitions);
    }

    /// <summary>从存活模型根逐项求待删旧项的引用闭包, 无关项和不被外部引用的互相依赖组仍可删除.</summary>
    /// <param name="model">本轮已更新明确选中项的完整 TOM 模型, 只读取其可写定义.</param>
    /// <param name="removed">本轮移出模板清单的实际旧度量对象.</param>
    /// <returns>实际被外部根或其保留项依赖引用的集合; 无法核实的表达式保守保留相关集合.</returns>
    private static HashSet<TOM.Measure> ReferencedRemovals(TOM.Model model, IReadOnlyCollection<TOM.Measure> removed)
    {
        var definition = ModelConfigurationOwnership.Definition(model);
        var source = new Dictionary<TOM.Measure, JsonNode>();
        foreach (var table in definition["tables"]!.AsArray().OfType<JsonObject>())
        {
            if (table["measures"] is not JsonArray measures) continue;
            foreach (var measure in removed.Where(measure => measure.Table.Name == table["name"]!.GetValue<string>()))
            {
                var node = measures.Single(node => node!["name"]!.GetValue<string>() == measure.Name)!;
                measures.Remove(node);
                source.Add(measure, node);
            }
        }
        var retained = new HashSet<TOM.Measure>();
        var queue = new Queue<TOM.Measure>();
        Find([.. removed], definition);
        while (queue.TryDequeue(out var measure))
        {
            // 原对象定义的只读扫描视图只含当前保留项, 不复制或创建另一个 TOM 模型.
            var dependency = new JsonObject
            {
                ["tables"] = new JsonArray(new JsonObject
                {
                    ["name"] = measure.Table.Name,
                    ["measures"] = new JsonArray(source[measure].DeepClone())
                })
            };
            Find([.. removed.Where(candidate => !retained.Contains(candidate))], dependency);
        }
        return retained;

        /// <summary>按组预检剪去无引用分支, 只在存在引用的分支继续拆分到单个旧项.</summary>
        void Find(TOM.Measure[] candidates, JsonNode scan)
        {
            if (candidates.Length == 0) return;
            try { ScaffoldPeriodProtection.RequireUnreferenced(model, candidates, finalDefinition: scan); return; }
            catch (AppException error) when (error.Code == "CONFIGURATION_PERIOD_REFERENCED") { /* 拆分到实际被引用的旧项. */ }
            if (candidates.Length == 1)
            {
                if (retained.Add(candidates[0])) queue.Enqueue(candidates[0]);
                return;
            }
            var middle = candidates.Length / 2;
            Find(candidates[..middle], scan);
            Find(candidates[middle..], scan);
        }
    }

    /// <summary>保留其他业务角色和命名表达式, 参数表使用稳定的首个可用名称而不阻止模式切换.</summary>
    private static string AvailableConfigName(TOM.Model model, Dictionary<string, string> roles)
    {
        var name = TemplateMeasureConfigurationStorage.DefaultTable;
        var suffix = 1;
        while (roles.TryGetValue(name, out var role) && role != "measure-config" || model.Expressions.ContainsName(name)
            || model.Tables.Find(name)?.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null)
            name = TemplateMeasureConfigurationStorage.DefaultTable + "_" + suffix++;
        return name;
    }

    /// <summary>修复固定六字段, 保留额外用户列与 SDK 只读行号, 同名非数据列按当前显式用途替换.</summary>
    private static void RepairColumns(TOM.Table config)
    {
        foreach (var name in TemplateMeasureConfigurationStorage.Columns)
        {
            var prior = config.Columns.Find(name);
            if (prior is not TOM.DataColumn)
            {
                var lineage = prior?.LineageTag;
                if (prior is not null) config.Columns.Remove(prior);
                config.Columns.Add(new TOM.DataColumn { Name = name, LineageTag = lineage });
            }
            var column = (TOM.DataColumn)config.Columns[name];
            column.SourceColumn = name;
            column.DataType = name == "status" ? TOM.DataType.Boolean : TOM.DataType.String;
            column.IsDataTypeInferred = false;
        }
    }

    /// <summary>额外数据列仍从真实分区得到 null 源列, 避免保留列后刷新缺失 SourceColumn.</summary>
    /// <param name="config">当前参数表, 空 SourceColumn 在生成前绑定到原列名.</param>
    /// <param name="expression">已生成并完成转义的完整 M 分区表达式.</param>
    /// <returns>原表达式或包含全部实际源列的固定包装, 保留内部表达式原文.</returns>
    private static string WithExtraSources(TOM.Table config, string expression)
    {
        foreach (var column in config.Columns.OfType<TOM.DataColumn>().Where(column => string.IsNullOrEmpty(column.SourceColumn))) column.SourceColumn = column.Name;
        var sources = config.Columns.OfType<TOM.DataColumn>().Select(column => column.SourceColumn).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.All(source => TemplateMeasureConfigurationStorage.Columns.Contains(source, StringComparer.Ordinal))) return expression;
        var names = string.Join(", ", sources.Select(PowerQueryLiterals.Text));
        return $$"""
            let
                Template = ({{expression}}),
                Result = Table.SelectColumns(Template, {{{names}}}, MissingField.UseNull)
            in
                Result
            """;
    }

    /// <summary>将本轮六字段配置物化为独立 M 表, 仅供 TMDL 模式保留仍有用户引用的参数表.</summary>
    private static string Snapshot(TemplateMeasureDefinition[] definitions) => "#table(type table [name = text, value = text, dataCategory = text, status = logical, description = text, displayFolder = text], {"
        + string.Join(", ", definitions.Select(value => "{" + string.Join(", ", new[] { PowerQueryLiterals.Text(value.Name), PowerQueryLiterals.Text(value.Value),
            PowerQueryLiterals.Text(value.DataCategory), value.Enabled ? "true" : "false", PowerQueryLiterals.Text(value.Description), PowerQueryLiterals.Text(value.DisplayFolder) }) + "}")) + "})";

    /// <summary>仅清理成功退休参数表的查询顺序; 无效的无关注解不阻止模板切换.</summary>
    private static void RemoveQueryOrder(TOM.Model model, string table)
    {
        if (model.Annotations.Find("PBI_QueryOrder") is not { } order) return;
        try
        {
            if (JsonNode.Parse(order.Value) is not JsonArray queries) return;
            foreach (var query in queries.OfType<JsonValue>().Where(query => query.TryGetValue<string>(out var name) && name == table).ToArray()) queries.Remove(query);
            order.Value = queries.ToJsonString();
        }
        catch (System.Text.Json.JsonException) { /* 不接管与本次六字段模板配置无关的损坏注解. */ }
    }

    /// <summary>参数角色和对象已核验后, 仅清理受信新建资源或完整来源已证明的既有 Q&A, 其他词汇保持原样.</summary>
    private static void RemoveProvenLinguisticBindings(TOM.Model model, string table, bool builtin)
    {
        IEnumerable<TOM.Culture> cultures;
        try { cultures = builtin ? model.Cultures : CalendarGeneratedLinguisticMetadata.VerifiedMeasureConfigCultures(model); }
        catch (AppException error) when (error.Code == "CONFIGURATION_OWNERSHIP_INVALID")
        { return; } // 缺少可靠角色证明时保留 Q&A, 参数表随后按真实剩余引用保留而不阻止切换.
        foreach (var culture in cultures)
        {
            if (culture.LinguisticMetadata is not { Content: { Length: > 0 } content } metadata) continue;
            metadata.Content = ProjectWithoutMeasureConfig(JsonNode.Parse(content)!.AsObject(), table).ToJsonString();
        }
    }

    /// <summary>从完整受信资源副本精确投影参数表的 Q&A 清理, 创建与日历全文证明共用同一纯变换.</summary>
    /// <param name="source">固定内置资源或其当前生成投影的完整 JSON, 原节点保持不变.</param>
    /// <param name="table">受信内置角色绑定的参数表名, 不用于认领任意用户元数据.</param>
    /// <returns>仅去除该表实体及引用这些实体的关系的完整副本, 其他内容逐节点保留.</returns>
    internal static JsonObject ProjectWithoutMeasureConfig(JsonObject source, string table)
    {
        var node = source.DeepClone().AsObject();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        if (node["Entities"] is JsonObject entities)
            foreach (var pair in entities.ToArray())
                if (pair.Value?["Definition"]?["Binding"]?["ConceptualEntity"]?.GetValue<string>() == table)
                { entities.Remove(pair.Key); removed.Add(pair.Key); }
        static bool References(JsonNode? item, HashSet<string> names) => item switch
        {
            JsonObject obj => obj.Any(pair => References(pair.Value, names)),
            JsonArray array => array.Any(child => References(child, names)),
            JsonValue scalar => scalar.TryGetValue<string>(out var text) && names.Contains(text),
            _ => false
        };
        if (node["Relationships"] is JsonObject relationships)
            foreach (var pair in relationships.Where(pair => References(pair.Value, removed)).ToArray()) relationships.Remove(pair.Key);
        return node;
    }

    /// <summary>仅转换固定模板已知的完整查值表达式, 不用名称或局部字符串认领未知 DAX.</summary>
    /// <param name="model">调用方拥有的当前模型, 仅物化完整模板表达式匹配的度量.</param>
    /// <param name="config">已证明来源的参数表, 名称按 DAX 标识符转义后参与模板比较.</param>
    /// <param name="values">当前完整配置值, 缺失键按既有规则物化为空字符串.</param>
    /// <param name="configuration">是否为已有模型配置; 为 true 时更新匹配对象归属.</param>
    private static void MaterializeTemplate(TOM.Model model, TOM.Table config, TemplateMeasureDefinition[] values, bool configuration)
    {
        foreach (var measure in model.Tables.SelectMany(table => table.Measures).Where(measure => measure.Expression.Contains(config.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var match = ConfigKey().Match(measure.Expression);
            if (!match.Success) continue;
            var key = match.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal);
            var quoted = "'" + config.Name.Replace("'", "''", StringComparison.Ordinal) + "'";
            var basis = $"""
                VAR ConfigTable =
                    FILTER (
                        {quoted},
                        {quoted}[name] = {ScaffoldTemplateMeasures.Literal(key)}
                    )

                VAR ConfigValue =
                    CONCATENATEX (
                        ConfigTable,
                        {quoted}[value]
                    )
                """;
            var expected = $"""
                {basis}
                VAR Result =
                    ConfigValue

                RETURN
                    Result
                """;
            var image = $"""
                VAR SvgStartImage =
                    "data:image/svg+xml;utf8,"

                {basis}

                VAR Status =
                    SEARCH (
                        "<svg*</svg>",
                        ConfigValue,
                        1,
                        BLANK ()
                    )

                VAR Result =
                    IF (
                        ISBLANK ( Status ),
                        ConfigValue,
                        SvgStartImage & ConfigValue
                    )

                RETURN
                    Result
                """;
            if (!Equivalent(measure.Expression, expected) && !Equivalent(measure.Expression, image)) continue;
            var value = values.FirstOrDefault(value => value.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (measure.DataCategory == "ImageUrl") value = ScaffoldTemplateMeasures.ImageValue(value);
            measure.Expression = ScaffoldTemplateMeasures.LiteralMeasure(value);
            if (measure.DataCategory == "RefreshTime") measure.DataCategory = "Uncategorized";
            if (configuration) ModelConfigurationOwnership.Mark(measure, measure.Annotations);
        }
    }

    /// <summary>格式化只用于完整词元比较, 失败即拒绝把表达式视为已知模板.</summary>
    private static bool Equivalent(string actual, string expected)
    {
        try { return DaxFormatter.FormatMeasure(actual) == DaxFormatter.FormatMeasure(expected); }
        catch (AppException) { return false; }
    }

    /// <summary>退休前检查完整可写定义; 额外用户成员或任何剩余引用均保留表, 不阻止 TMDL.</summary>
    private static bool CanRemove(TOM.Model model, TOM.Table config)
    {
        if (config.Measures.Count > 0 || config.Hierarchies.Count > 0 || config.Columns.Any(column => column is not TOM.RowNumberColumn
            && (column is not TOM.DataColumn data || !TemplateMeasureConfigurationStorage.Columns.Contains(data.Name, StringComparer.Ordinal)))) return false;
        var definition = ModelConfigurationOwnership.Definition(model).AsObject();
        var tables = definition["tables"]!.AsArray();
        tables.Remove(tables.Single(table => table!["name"]!.GetValue<string>() == config.Name));
        bool Referenced(JsonNode? node)
        {
            if (node is JsonObject obj) return obj.Any(pair => pair.Key is not ("annotations" or "extendedProperties") && Referenced(pair.Value));
            if (node is JsonArray array) return array.Any(Referenced);
            return node is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains(config.Name, StringComparison.OrdinalIgnoreCase);
        }
        return !Referenced(definition);
    }

    [GeneratedRegex("\\[name\\]\\s*=\\s*\"((?:[^\"]|\"\")*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigKey();

}
