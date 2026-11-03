// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\TimeIntelligence\ScaffoldPeriodMeasures.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将时间智能最终选择同步到私有模型, 用明确角色与完整归属保护未知对象.

using PbiUtils.Application.Calendar;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Calendar.TimeIntelligence;

namespace PbiUtils.Infrastructure.Calendar.TimeIntelligence;

/// <summary>期间集合由一次显式配置拥有, null 跳过, 空模板集删除已核实输出.</summary>
internal static class ScaffoldPeriodMeasures
{
    private const string OriginAnnotation = "PbiUtils.TimeIntelligence.v1";
    private sealed record Origin(int SchemaVersion, string BaseMeasure, string TemplateId);
    private static readonly (string Prefix, string TemplateId)[] TemplatePrefixes = [.. ScaffoldTimeIntelligence.Generate("Base",
        new("Dates", "Date", "Output", ["Base"], [.. ScaffoldTimeIntelligence.Templates.Select(value => value.Id)]))
        .Select(value => (value.Name[..^4], value.TemplateId))];

    /// <summary>校验本轮日期/基础绑定后同步生成集合, 返回变更父表名供调用者延续原有效父表归属.</summary>
    /// <param name="model">调用者独占的原连接模型或生成事务模型, 本方法不提交引擎.</param>
    /// <param name="options">明确配置, null 不修改任何既有对象.</param>
    /// <param name="configuration">现有模型允许更新归属有效对象; 新建时名称冲突仍拒绝.</param>
    /// <returns>新增、更新、移动或删除输出涉及的实际表名.</returns>
    /// <exception cref="AppException">绑定、归属、名称或存活引用不安全时拒绝, 权威模型由上层保持原样.</exception>
    internal static IReadOnlySet<string> Apply(TOM.Model model, ScaffoldPeriodOptions? options, bool configuration)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (options is null) return changed;
        var date = model.Tables.Find(options.DateTable)?.Columns.Find(options.DateColumn);
        var table = model.Tables.Find(options.MeasureTable);
        if (date?.DataType != TOM.DataType.DateTime || table is null)
            throw new AppException("SCAFFOLD_PERIOD_BINDING", "请选择实际日期字段和输出度量值表", 2);
        var existing = model.Tables.SelectMany(value => value.Measures).ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var generated = options.BaseMeasures.SelectMany(name =>
        {
            if (!existing.ContainsKey(name)) throw new AppException("SCAFFOLD_PERIOD_BINDING", "基础度量值不存在", 2);
            return ScaffoldTimeIntelligence.Generate(name, options, model.Tables.Select(table => table.Name))
                .Select(value => (Value: value, Origin: new Origin(1, name, value.TemplateId)));
        }).ToArray();
        var names = generated.Select(value => value.Value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != generated.Length) throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "时间智能度量值名称与已有对象冲突", 2);
        var owned = configuration ? existing.Values.Where(value => ReadOrigin(value) is not null).ToHashSet() : [];
        var removed = owned.Where(value => !names.Contains(value.Name)).ToArray();
        foreach (var value in generated)
            if (existing.TryGetValue(value.Value.Name, out var prior))
            {
                if (!configuration || prior.Table != table && !owned.Contains(prior))
                    throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "时间智能度量值名称与已有对象冲突", 2);
                ModelConfigurationOwnership.Require(prior, prior.Annotations);
                if (!owned.Contains(prior)) throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "同名对象没有完整时间智能生成归属, 未覆盖", 3);
            }
        var moving = generated.Select(value => existing.GetValueOrDefault(value.Value.Name)).OfType<TOM.Measure>().Where(value => value.Table != table).ToArray();
        // SDK 的 Measure.Table 只读, Remove 后不可重新挂载; 只对已核验生成输出按本次配置生成目标对象.
        // 所有迁移先验证可写元数据等价, 未知子元数据必须在任何原对象移除前拒绝.
        var moved = moving.ToDictionary(measure => measure,
            measure => CreateMovedOutput(measure, generated.Single(value => value.Value.Name.Equals(measure.Name, StringComparison.OrdinalIgnoreCase)).Value));
        // 旧期间公式会整体重写或移除, 外部业务根和元数据若仍引用旧位置则禁止移动.
        ScaffoldPeriodProtection.RequireUnreferenced(model, moving, owned);
        foreach (var value in generated)
        {
            var measure = existing.GetValueOrDefault(value.Value.Name) ?? new TOM.Measure { Name = value.Value.Name };
            if (measure.Table is { } previous && previous != table)
            {
                changed.Add(previous.Name);
                var replacement = moved[measure];
                previous.Measures.Remove(measure);
                measure = replacement;
            }
            if (measure.Parent is null) table.Measures.Add(measure);
            measure.Expression = value.Value.Expression;
            measure.FormatString = value.Value.FormatString;
            measure.DisplayFolder = value.Value.Folder;
            if (measure.Annotations.Find(OriginAnnotation) is { } old) measure.Annotations.Remove(old);
            measure.Annotations.Add(new TOM.Annotation { Name = OriginAnnotation, Value = JsonSerializer.Serialize(value.Origin) });
            if (configuration) ModelConfigurationOwnership.Mark(measure, measure.Annotations);
            changed.Add(table.Name);
        }
        // 先应用保留项的新表达式, 再检查真实剩余引用; 待删除集合内部的依赖不阻止整批移除.
        ScaffoldPeriodProtection.RequireUnreferenced(model, removed);
        foreach (var measure in removed)
        {
            changed.Add(measure.Table.Name);
            measure.Table.Measures.Remove(measure);
        }
        ScaffoldPeriodConfiguration.Write(model, options);
        return changed;
    }

    /// <summary>从已批准生成参数建立目标输出并保留明确业务标量, SDK 未知子树或属性不能等价迁移时拒绝.</summary>
    /// <param name="source">已通过完整归属核验的原生成输出; 存活引用由调用方在移除前核验, 此处不修改它.</param>
    /// <param name="generated">本轮冻结配置生成的准确表达式、格式与目录.</param>
    /// <returns>尚未挂载的目标生成输出, 不作为完整模型副本或校验模型.</returns>
    /// <exception cref="AppException">原输出含无法按生成契约等价保留的可写元数据.</exception>
    private static TOM.Measure CreateMovedOutput(TOM.Measure source, GeneratedMeasure generated)
    {
        var target = new TOM.Measure
        {
            Name = source.Name,
            Expression = generated.Expression,
            FormatString = generated.FormatString,
            DisplayFolder = generated.Folder,
            Description = source.Description,
            IsHidden = source.IsHidden,
            DataCategory = source.DataCategory,
            LineageTag = source.LineageTag,
            SourceLineageTag = source.SourceLineageTag
        };
        foreach (var annotation in source.Annotations)
            target.Annotations.Add(new TOM.Annotation { Name = annotation.Name, Value = annotation.Value });
        var before = ModelConfigurationOwnership.Definition(source).AsObject();
        var after = ModelConfigurationOwnership.Definition(target).AsObject();
        foreach (var field in new[] { "expression", "formatString", "displayFolder" })
        {
            before.Remove(field);
            after.Remove(field);
        }
        if (!JsonNode.DeepEquals(before, after))
            throw new AppException("CONFIGURATION_PERIOD_MOVE_UNSUPPORTED", "期间输出包含无法等价迁移的业务元数据, 未移动或修改原对象", 3);
        return target;
    }

    /// <summary>读取完整归属有效的期间角色, 失效生成对象明确抛错供上层拒绝旧配置背书.</summary>
    internal static (TOM.Measure Measure, string BaseMeasure, string TemplateId)[] ReadOwned(TOM.Model model) => [.. model.Tables.SelectMany(table => table.Measures)
        .Select(measure => (Measure: measure, Origin: ReadOrigin(measure))).Where(value => value.Origin is not null)
        .Select(value => (value.Measure, value.Origin!.BaseMeasure, value.Origin.TemplateId))];

    /// <summary>角色注解必须包含在完整归属指纹内; 无角色注解的对象不推定为工具生成.</summary>
    private static Origin? ReadOrigin(TOM.Measure measure)
    {
        if (measure.Annotations.Find(OriginAnnotation) is not { } annotation)
            return null;
        ModelConfigurationOwnership.Require(measure, measure.Annotations);
        try
        {
            var origin = JsonSerializer.Deserialize<Origin>(annotation.Value);
            if (origin is null || origin.SchemaVersion != 1 || string.IsNullOrWhiteSpace(origin.BaseMeasure)
                || !TemplatePrefixes.Any(value => value.TemplateId == origin.TemplateId && value.Prefix + origin.BaseMeasure == measure.Name)) throw new JsonException();
            return origin;
        }
        catch (JsonException) { throw new AppException("CONFIGURATION_OWNERSHIP_INVALID", "时间智能生成归属无效或版本未知, 未修改模型", 3); }
    }

}
