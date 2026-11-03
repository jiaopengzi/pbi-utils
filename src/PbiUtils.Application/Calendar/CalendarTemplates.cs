// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\CalendarTemplates.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 生成独立公历与经典日期列期间公式, 不依赖 TOM 或私有 51 状态脚本.

using PbiUtils.Application.Code.Dax;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Core;
using PbiUtils.Application.Models;

namespace PbiUtils.Application.Calendar;

/// <summary>完整公历年份, 周一开始; 两入口统一允许 1900–2100 年.</summary>
public sealed record CalendarOptions(int StartYear, int EndYear);
/// <summary>显式选定基础指标及日期角色; 不自动挑选或建立关系.</summary>
public sealed record PeriodOptions(string DateTable, string DateColumn, string MeasureTable, string[] Templates,
    int DigitsN = 2, int DigitsP = 2, string Folder = "时间智能");
/// <summary>生成内容供所有入口展示和模型适配器使用.</summary>
public sealed record GeneratedMeasure(string Name, string Expression, string FormatString, string Folder, string TemplateId);
/// <summary>无环境副作用的业务生成器; 数据加载、关系与业务数值须另验.</summary>
public static class CalendarTemplates
{
    public const string Version = "classic-period-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>只读取已声明、无重复字段的配置; 不接受任意额外公式.</summary>
    public static T Parse<T>(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.RootElement.EnumerateObject().Count())
                throw new JsonException();
            return JsonSerializer.Deserialize<T>(value, Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new AppException("INVALID_TEMPLATE_CONFIG", "模板配置格式、字段或类型无效"); }
    }

    /// <summary>生成并本地格式化完整年份连续日期, 与 M 农历方法和原版状态无兼容承诺.</summary>
    /// <param name="options">包含 1900–2100 年有效区间的显式参数.</param>
    /// <returns>使用统一 SQLBI 排版的 DAX 计算表表达式.</returns>
    /// <exception cref="AppException">日期区间无效或表达式无法安全格式化.</exception>
    public static string Calendar(CalendarOptions options)
    {
        if (options.StartYear < 1900 || options.EndYear > 2100 || options.StartYear > options.EndYear)
            throw new AppException("INVALID_DATE_RANGE", "日期年份需为 1900–2100, 起始年份不能大于结束年份");
        return DaxFormatter.Format(FormattableString.Invariant($"""
            ADDCOLUMNS(
                CALENDAR(DATE({options.StartYear}, 1, 1), DATE({options.EndYear}, 12, 31)),
                "Year", YEAR([Date]),
                "MonthNo", MONTH([Date]),
                "Quarter", QUARTER([Date]),
                "YearMonthKey", YEAR([Date]) * 100 + MONTH([Date]),
                "YearMonth", FORMAT([Date], "yyyy-MM"),
                "StartOfWeek", [Date] - WEEKDAY([Date], 2) + 1
            )
            """));
    }

    /// <summary>按 DAX 标识符规则转义已有模型名称, 不把名称作为公式片段.</summary>
    public static string TableReference(string name) => "'" + ModelNames.Validate(name).Replace("'", "''", StringComparison.Ordinal) + "'";
    /// <summary>列和度量值的方括号标识符转义.</summary>
    public static string ColumnReference(string name) => "[" + ModelNames.Validate(name).Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>当前日期筛选期间模板; 同比率内联依赖, 比较期空/零返回 BLANK.</summary>
    /// <param name="baseMeasure">唯一基础度量的实际名称.</param>
    /// <param name="options">日期映射、输出表和期间配置.</param>
    /// <param name="reservedTableNames">宿主可提供完整表名以避让变量, 日期和输出表始终预留.</param>
    /// <returns>保留公开业务名称及数值逻辑的本地格式化期间表达式.</returns>
    /// <exception cref="AppException">业务名称或期间参数无效, 或生成式无法安全格式化.</exception>
    public static IReadOnlyList<GeneratedMeasure> Periods(string baseMeasure, PeriodOptions options, IEnumerable<string>? reservedTableNames = null)
    {
        ModelNames.Validate(baseMeasure); ModelNames.Validate(options.DateTable); ModelNames.Validate(options.DateColumn); ModelNames.Validate(options.MeasureTable);
        if (options.Templates is null || options.Templates.Length is < 1 or > 5 || options.DigitsN is < 0 or > 8 || options.DigitsP is < 0 or > 8
            || options.Folder is null || options.Folder.Length > 200 || options.Folder.Any(char.IsControl))
            throw new AppException("INVALID_TEMPLATE_CONFIG", "请选择 1–5 个期间模板, 小数位需为 0–8, 文件夹不超过200字符");
        var dates = TableReference(options.DateTable) + ColumnReference(options.DateColumn);
        var measure = ColumnReference(baseMeasure);
        var py = $"CALCULATE({measure}, SAMEPERIODLASTYEAR({dates}))";
        var tableNames = (reservedTableNames ?? []).Append(options.DateTable).Append(options.MeasureTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variables = DaxGeneratedVariables.Reserve(["PreviousPeriod", "Result"], tableNames);
        var previousPeriod = variables["PreviousPeriod"];
        var result = variables["Result"];
        static string Format(int digits) => "0" + (digits == 0 ? "" : "." + new string('0', digits));
        return options.Templates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
        {
            var (label, expression) = id switch
            {
                "MTD" => ("MTD", $"CALCULATE({measure}, DATESMTD({dates}))"),
                "QTD" => ("QTD", $"CALCULATE({measure}, DATESQTD({dates}))"),
                "YTD" => ("YTD", $"CALCULATE({measure}, DATESYTD({dates}))"),
                "PY" => ("上年同期", py),
                "YOY" => ("同比率", $"""
                    VAR {previousPeriod} = {py}
                    VAR {result} = DIVIDE({measure} - {previousPeriod}, {previousPeriod})
                    RETURN {result}
                    """),
                _ => throw new AppException("UNKNOWN_TEMPLATE", "不支持的期间模板 ID; 未实现原版51状态")
            };
            var name = ModelNames.Validate(baseMeasure + " " + label);
            return new GeneratedMeasure(name, DaxFormatter.FormatMeasure(expression), id == "YOY" ? Format(options.DigitsP) + "%" : Format(options.DigitsN), options.Folder, Version + "/" + id);
        }).ToArray();
    }
}
