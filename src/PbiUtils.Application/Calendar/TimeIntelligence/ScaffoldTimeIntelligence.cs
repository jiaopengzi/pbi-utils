// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\TimeIntelligence\ScaffoldTimeIntelligence.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 生成51项声明式期间度量值.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Application.Calendar;
using System.Globalization;
using System.Text.RegularExpressions;
using PbiUtils.Core;
using PbiUtils.Application.Models;

namespace PbiUtils.Application.Calendar.TimeIntelligence;

/// <summary>仅生成明确选择的期间定义, 不操作已有模型.</summary>
public static partial class ScaffoldTimeIntelligence
{
    private static readonly Dictionary<string, ScaffoldPeriodDefinitions.Definition> Definitions = ScaffoldPeriodDefinitions.All.ToDictionary(d => d.Template.Id, StringComparer.Ordinal);

    /// <summary>供选择界面展示的定义与依赖.</summary>
    public static IReadOnlyList<ScaffoldPeriodTemplate> Templates => ScaffoldPeriodDefinitions.All.Select(d => d.Template with { Dependencies = [.. d.Template.Dependencies] }).ToArray();

    /// <summary>生成指定基础指标的定义及依赖闭包.</summary>
    /// <param name="baseMeasure">经过验证的基础度量名称.</param>
    /// <param name="options">实际日期角色、输出表及期间选择.</param>
    /// <param name="reservedTableNames">宿主已知的完整表名集合, 包括同批即将创建的表; 另始终预留日期及输出表.</param>
    /// <returns>安全避让表名并经本地格式化的完整期间闭包, 公开度量名称保持既有约定.</returns>
    /// <exception cref="AppException">参数、模板或名称无效, 或生成结果无法安全格式化.</exception>
    public static IReadOnlyList<GeneratedMeasure> Generate(string baseMeasure, ScaffoldPeriodOptions options, IEnumerable<string>? reservedTableNames = null)
    {
        ModelNames.Validate(baseMeasure);
        if (options is null) throw new AppException("INVALID_PERIOD_OPTIONS", "期间参数缺失");
        ModelNames.Validate(options.DateTable);
        ModelNames.Validate(options.DateColumn);
        ModelNames.Validate(options.MeasureTable);
        ModelNames.Validate(options.Folder);
        if (options.Templates is null || options.Templates.Length > 51 || options.BaseMeasures is null
            || options.BaseMeasures.Length is < 1 or > 200 || options.BaseMeasures.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.BaseMeasures.Length
            || !options.BaseMeasures.Contains(baseMeasure, StringComparer.Ordinal) || options.NumberDigits is < 0 or > 8 || options.PercentDigits is < 0 or > 8)
            throw new AppException("INVALID_PERIOD_OPTIONS", "期间选择、基础指标或精度无效");
        foreach (var name in options.BaseMeasures) ModelNames.Validate(name);
        var ordered = new List<ScaffoldPeriodDefinitions.Definition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (id is null || !Definitions.TryGetValue(id, out var definition))
                throw new AppException("INVALID_PERIOD_TEMPLATE", "存在未知期间模板");
            if (!visited.Add(id)) return;
            foreach (var dependency in definition.Template.Dependencies) Include(dependency);
            ordered.Add(definition);
        }
        foreach (var id in options.Templates) Include(id);
        var dateTable = CalendarTemplates.TableReference(options.DateTable);
        var date = dateTable + CalendarTemplates.ColumnReference(options.DateColumn);
        var measure = CalendarTemplates.ColumnReference(baseMeasure);
        var tableNames = (reservedTableNames ?? []).Append(options.DateTable).Append(options.MeasureTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<GeneratedMeasure>(ordered.Count);
        foreach (var definition in ordered)
        {
            var template = definition.Template;
            string expression;
            if (template.Dependencies.Length == 2)
            {
                var current = CalendarTemplates.ColumnReference(GeneratedName(Definitions[template.Dependencies[0]], baseMeasure));
                var previous = CalendarTemplates.ColumnReference(GeneratedName(Definitions[template.Dependencies[1]], baseMeasure));
                expression = template.Id.EndsWith('%') ? $"DIVIDE ( {current} - {previous}, {previous}, BLANK () )" : $"{current} - {previous}";
            }
            else
            {
                // 一次扫描替换避免用户名称中的占位文本被第二次解释为公式.
                var templateExpression = DaxGeneratedVariables.AvoidTableNames(definition.Expression, tableNames);
                expression = Placeholder().Replace(templateExpression, match => match.Value switch
                {
                    "{{DATE}}" => date,
                    "{{TABLE}}" => dateTable,
                    "{{MEASURE}}" => measure,
                    _ => throw new InvalidOperationException()
                });
            }
            var digits = template.Id.EndsWith('%') ? options.PercentDigits : options.NumberDigits;
            var format = "#,0" + (digits == 0 ? "" : "." + new string('0', digits)) + (template.Id.EndsWith('%') ? "%" : "");
            result.Add(new(GeneratedName(definition, baseMeasure), DaxFormatter.FormatMeasure(expression), format, options.Folder + "\\" + baseMeasure, template.Id));
        }
        if (result.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new AppException("PERIOD_NAME_CONFLICT", "生成的期间名称存在冲突");
        return result;
    }

    /// <summary>原始序号和标签保持稳定, 不依靠基础指标中的特殊标记.</summary>
    private static string GeneratedName(ScaffoldPeriodDefinitions.Definition definition, string baseMeasure) =>
        ModelNames.Validate(definition.Ordinal.ToString("00", CultureInfo.InvariantCulture) + "_" + definition.Template.Id + "_" + baseMeasure);

    [GeneratedRegex("\\{\\{(?:DATE|TABLE|MEASURE)\\}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
