// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\TimeIntelligence\ScaffoldPeriodConfiguration.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从实际完整生成定义核验并回读时间智能方案, 保存准确用户选择且不据此授予覆盖权限.

using PbiUtils.Application.Models;
using PbiUtils.Application.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Application.Calendar.TimeIntelligence;

namespace PbiUtils.Infrastructure.Calendar.TimeIntelligence;

/// <summary>方案注解仅是读取提示, 必须由实际输出和完整归属共同证明; 人工修改后不返回陈旧方案.</summary>
internal static class ScaffoldPeriodConfiguration
{
    private const string Annotation = "PbiUtils.TimeIntelligence.Options.v1";
    private static readonly HashSet<string> TemplateIds = ScaffoldTimeIntelligence.Templates.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
    private sealed record Stored(int SchemaVersion, ScaffoldPeriodOptions Options);

    /// <summary>在调用者私有模型完成集合变更后保存准确选项, 不写磁盘或提交引擎.</summary>
    internal static void Write(TOM.Model model, ScaffoldPeriodOptions options)
    {
        if (model.Annotations.Find(Annotation) is { } prior) model.Annotations.Remove(prior);
        model.Annotations.Add(new TOM.Annotation { Name = Annotation, Value = JsonSerializer.Serialize(new Stored(1, options)) });
    }

    /// <summary>仅返回能从实际模型完整重建的选项; 旧方案从真实生成集合推导, 歧义或改动返回 null 而不阻止只读模型浏览.</summary>
    internal static ScaffoldPeriodOptions? Read(TOM.Model model)
    {
        try
        {
            var owned = ScaffoldPeriodMeasures.ReadOwned(model);
            if (model.Annotations.Find(Annotation) is { } annotation)
            {
                var stored = JsonSerializer.Deserialize<Stored>(annotation.Value);
                return stored is { SchemaVersion: 1, Options: not null } && Matches(model, stored.Options, owned) ? stored.Options : null;
            }
            if (owned.Length == 0) return null;
            var bases = owned.Select(value => value.BaseMeasure).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var templates = owned.Select(value => value.TemplateId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var (Measure, BaseMeasure, TemplateId) = owned[0];
            var suffix = "\\" + BaseMeasure;
            if (Measure.DisplayFolder?.EndsWith(suffix, StringComparison.Ordinal) != true) return null;
            var folder = Measure.DisplayFolder[..^suffix.Length];
            var numbers = Digits(owned.Where(value => !value.TemplateId.EndsWith('%')).Select(value => value.Measure));
            var percents = Digits(owned.Where(value => value.TemplateId.EndsWith('%')).Select(value => value.Measure), true);
            if (numbers < 0 || percents < 0) return null;
            ScaffoldPeriodOptions? result = null;
            foreach (var date in model.Tables.SelectMany(table => table.Columns).Where(column => column.DataType == TOM.DataType.DateTime))
            {
                var options = new ScaffoldPeriodOptions(date.Table.Name, date.Name, Measure.Table.Name, bases, templates, folder, numbers, percents);
                if (!Matches(model, options, owned)) continue;
                if (result is not null) return null;
                result = options;
            }
            return result;
        }
        catch (Exception error) when (error is AppException or JsonException or ArgumentException)
        {
            // 读取不替用户修复归属或公式, null 只表示不能证明当前统一方案; 应用仍完整验证每个实际对象.
            return null;
        }
    }

    /// <summary>日期列改名后同步读取提示; 实际公式和归属仍须由调用方先安全更新, 下次读取重新完整证明.</summary>
    internal static void RenameDateBinding(TOM.Model model, string tableName, IReadOnlyDictionary<string, string> renames)
    {
        if (model.Annotations.Find(Annotation) is not { } annotation) return;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(annotation.Value); }
        catch (JsonException) { return; }
        if (stored is { SchemaVersion: 1, Options: not null } && stored.Options.DateTable == tableName
            && renames.TryGetValue(stored.Options.DateColumn, out var next)) Write(model, stored.Options with { DateColumn = next });
    }

    /// <summary>核对日期角色、基础度量、全部生成角色以及精确公式/格式/目录, 选项本身不是生成归属.</summary>
    private static bool Matches(TOM.Model model, ScaffoldPeriodOptions options, (TOM.Measure Measure, string BaseMeasure, string TemplateId)[] owned)
    {
        if (options.BaseMeasures is null || options.Templates is null || options.BaseMeasures.Length > 200 || options.Templates.Length > 51
            || options.NumberDigits is < 0 or > 8 || options.PercentDigits is < 0 or > 8 || options.Templates.Any(id => !TemplateIds.Contains(id))
            || model.Tables.Find(options.DateTable)?.Columns.Find(options.DateColumn)?.DataType != TOM.DataType.DateTime
            || model.Tables.Find(options.MeasureTable) is null) return false;
        ModelNames.Validate(options.Folder);
        var all = model.Tables.SelectMany(table => table.Measures).ToDictionary(measure => measure.Name, StringComparer.OrdinalIgnoreCase);
        if (options.BaseMeasures.Any(name => !all.ContainsKey(name))) return false;
        var expected = options.BaseMeasures.SelectMany(basis => ScaffoldTimeIntelligence.Generate(basis, options, model.Tables.Select(table => table.Name))
            .Select(value => (Value: value, Base: basis))).ToArray();
        return expected.Length == owned.Length && expected.All(value => owned.Any(actual => actual.BaseMeasure == value.Base
            && actual.TemplateId == value.Value.TemplateId && actual.Measure.Name == value.Value.Name && actual.Measure.Table.Name == options.MeasureTable
            && Normalize(actual.Measure.Expression) == Normalize(value.Value.Expression) && actual.Measure.FormatString == value.Value.FormatString
            && actual.Measure.DisplayFolder == value.Value.Folder));
    }

    /// <summary>旧对象只接受生成器支持的精度, 缺少该数值类别时使用默认2; 不猜测自定义格式.</summary>
    private static int Digits(IEnumerable<TOM.Measure> measures, bool percent = false)
    {
        var result = -1;
        foreach (var measure in measures)
        {
            var digits = Enumerable.Range(0, 9).FirstOrDefault(value => measure.FormatString == "#,0"
                + (value == 0 ? "" : "." + new string('0', value)) + (percent ? "%" : ""), -1);
            if (digits < 0 || result >= 0 && result != digits) return -1;
            result = digits;
        }
        return result < 0 ? 2 : result;
    }

    /// <summary>仅规范换行, 不忽略表达式词元和业务空白.</summary>
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
