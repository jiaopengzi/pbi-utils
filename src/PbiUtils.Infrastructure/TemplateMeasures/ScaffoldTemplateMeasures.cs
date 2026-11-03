// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\TemplateMeasures\ScaffoldTemplateMeasures.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 六字段模板配置与时间智能度量值组合, 精确检测名称和日期角色冲突.

using PbiUtils.Application.Code.Dax;
using System.Globalization;
using System.Text.RegularExpressions;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.TemplateMeasures;

/// <summary>显式模板配置直接创建或就地覆盖同名度量, 保留原父表与对象身份.</summary>
internal static partial class ScaffoldTemplateMeasures
{
    /// <summary>显式配置覆盖同名度量的六字段元数据; 不存在时创建, 不以历史生成归属限制本次授权.</summary>
    /// <param name="model">调用方拥有的当前 TOM 对象, 本方法不执行服务器提交.</param>
    /// <param name="definitions">显式配置, null 保留原定义; 禁用条目隐藏既有对象且不生成新对象.</param>
    /// <param name="mode">tmdl 使用字符串常量, config 仅从当前固定参数表查值.</param>
    /// <param name="configTable">由宿主角色归属决定的参数表名, 不作为文件访问授权.</param>
    /// <param name="reservedTableNames">本轮稍后生成的表名, 与当前表及参数表一起约束 DAX 变量命名.</param>
    /// <exception cref="AppException">同名度量有多个实际对象时拒绝模糊目标.</exception>
    internal static void Apply(TOM.Model model, TemplateMeasureDefinition[]? definitions, string mode = "tmdl", string? configTable = null,
        IEnumerable<string>? reservedTableNames = null)
    {
        if (definitions is null) return;
        var table = model.Tables.Find("Measure");
        if (table is null)
        {
            table = new TOM.Table { Name = "Measure" };
            table.Partitions.Add(new TOM.Partition { Name = "Measure", Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table({}, {})" } });
            model.Tables.Add(table);
        }
        var configName = configTable ?? TemplateMeasureConfigurationStorage.DefaultTable;
        var tableNames = mode == "config" ? model.Tables.Select(table => table.Name).Concat(reservedTableNames ?? [])
            .Append(configName).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var definition in definitions)
        {
            var matches = model.Tables.SelectMany(t => t.Measures).Where(m => m.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
                throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "模板度量值名称与已有对象冲突", 2);
            // 同名项保持原对象及父表, 以免移动度量破坏报表绑定; 未选中的用户对象不参与覆盖.
            if (!definition.Enabled)
            {
                // 禁用不删除原有对象, 保留报表引用并隐藏; 新条目不生成.
                if (matches.Length == 1)
                {
                    matches[0].IsHidden = true;
                    ModelConfigurationOwnership.Mark(matches[0], matches[0].Annotations);
                }
                continue;
            }
            var measure = matches.SingleOrDefault() ?? new TOM.Measure { Name = definition.Name };
            measure.Expression = mode == "config" ? ConfigExpression(configName, definition, tableNames!)
                : TmdlExpression(definition);
            measure.DataCategory = TemplateMeasureCategories.ToModelCategory(definition.DataCategory);
            measure.Description = definition.Description;
            measure.DisplayFolder = definition.DisplayFolder;
            measure.IsHidden = false;
            if (matches.Length == 0) table.Measures.Add(measure);
            ModelConfigurationOwnership.Mark(measure, measure.Annotations);
        }
    }

    /// <summary>转义模板文本为 DAX 常量, 按 TMDL 保存语义统一 LF, 不修改配置或草稿原文.</summary>
    internal static string Literal(string value) => "\"" + value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
        .Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>将已转义的单个模板常量放在度量定义头下一行, 不缩进字符串内部换行且保留 32768 字符配置容量.</summary>
    /// <param name="value">模板的原始业务文本, 不作为 DAX 片段执行.</param>
    /// <returns>含起始 LF 且常量顶格的完整度量表达式.</returns>
    internal static string LiteralMeasure(string value) => "\n" + Literal(value);

    /// <summary>仅完整匹配本工具当前 Config 生成式时给出无外部源的等值表达式, 不按旧清单名称认领业务 DAX.</summary>
    /// <param name="actual">待保留旧项的完整实际表达式, 未知内容保持原样.</param>
    /// <param name="table">旧式实际绑定的参数表名称.</param>
    /// <param name="definition">该旧项最后有效的六字段配置, 包含禁用状态.</param>
    /// <param name="expression">匹配成功时返回完整独立 DAX, 图像仅内嵌配置值并保留原条件分支, 否则为 null.</param>
    /// <param name="sourceRowExists">实际来源是否有该键行; false 内嵌 BLANK(), true 将空字符串行保留为空字符串.</param>
    /// <returns>仅完整词元匹配成功时为 true; 格式化失败视为未知业务式.</returns>
    internal static bool TryMaterializeConfigExpression(string? actual, string table, TemplateMeasureDefinition definition, out string? expression, bool sourceRowExists = true)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ConfigVariable().Matches(actual))
            if (!names.TryAdd(match.Groups["basis"].Value, match.Groups["name"].Value)) return false;
        // 变量后缀来自旧表达式的完整声明, 不依赖已经变化的当前表集合; 最终仍必须整式匹配.
        string VariableName(string preferred) => names.GetValueOrDefault(preferred, preferred);
        try
        {
            if (DaxFormatter.FormatMeasure(actual) != ConfigExpression(table, definition, VariableName)) return false;
            // 零行 CONCATENATEX 返回 BLANK, 存在空值行返回空字符串; 不以六字段中的哨兵值混淆两者.
            var value = sourceRowExists ? Literal(definition.Value) : "BLANK()";
            // 图像的 SEARCH/LEFT 由模型继续求值, 不用 C# 的 StartsWith 近似 DAX 通配符及排序规则.
            expression = definition.DataCategory == "ImageUrl" ? ConfigExpression(table, definition, VariableName, materializedValue: value)
                : sourceRowExists ? LiteralMeasure(definition.Value) : "\nBLANK ()";
            return true;
        }
        catch (AppException) { return false; }
    }

    /// <summary>按既有 TMDL 分类规则生成字符串常量, SVG 补前缀且已有 URI 原样保留.</summary>
    /// <param name="definition">已通过共享契约校验的完整六字段定义.</param>
    /// <returns>已转义并统一换行的完整 DAX 字符串常量.</returns>
    private static string TmdlExpression(TemplateMeasureDefinition definition)
    {
        var value = definition.DataCategory == "ImageUrl" ? ImageValue(definition.Value) : definition.Value;
        return LiteralMeasure(value);
    }

    /// <summary>为裸 SVG 添加图片前缀, 允许 XML 声明和注释在根元素之前, 不改写已有 URI 或原文.</summary>
    /// <param name="value">模板中的完整图像文本, 不解析外部资源或执行 XML.</param>
    /// <returns>已有 URI 和非 SVG 文本原样返回, 裸 SVG 仅在原文前补充 data URI 前缀.</returns>
    internal static string ImageValue(string value) => !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        && !Uri.TryCreate(value, UriKind.Absolute, out _) && RawSvg().IsMatch(value)
        ? "data:image/svg+xml;utf8," + value : value;

    /// <summary>只识别根元素前的空白、XML 声明与完整注释, 不把注释内的 SVG 字样当作根元素.</summary>
    [GeneratedRegex(@"\A\s*(?:<\?xml\s[^<>]*\?>\s*)?(?:<!--(?:(?!-->).)*-->\s*)*<svg(?:\s|/?>)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RawSvg();

    /// <summary>筛选并拼接同名配置行, 完整 SVG 在返回处补前缀, 格式化后交给模型及 TMDL 保存.</summary>
    /// <param name="table">宿主确定的参数表名, 按 DAX 标识符规则转义.</param>
    /// <param name="definition">本轮模板键及分类, 不将配置值内嵌进表达式.</param>
    /// <param name="tableNames">当前及本轮稍后生成的完整表名集合, 按大小写无关规则查询.</param>
    /// <returns>本地格式化的多行 DAX; ImageUrl 保留已有 data URI, 其他值按配置表原文返回.</returns>
    /// <exception cref="AppException">生成表达式无法安全格式化时拒绝返回未格式化结果.</exception>
    private static string ConfigExpression(string table, TemplateMeasureDefinition definition, IReadOnlySet<string> tableNames)
        => ConfigExpression(table, definition, preferred => Variable(preferred, tableNames));

    /// <summary>生成和整式识别共用固定 DAX 模板, 变量名由已核实的当前表集合或旧式声明提供.</summary>
    /// <param name="table">实际参数表名称, 按 DAX 标识符转义.</param>
    /// <param name="definition">完整模板键及分类, 配置值不内嵌进查值表达式.</param>
    /// <param name="variable">只返回当前工具允许的变量基名及数字后缀, 不接受任意用户 DAX.</param>
    /// <param name="materializedValue">完整旧式核实后内嵌的已转义 DAX 字符串常量或 BLANK(), null 生成正常查值, 保留图像原条件语义.</param>
    /// <returns>经过本地格式化的完整当前生成式.</returns>
    /// <exception cref="AppException">生成表达式无法安全格式化.</exception>
    private static string ConfigExpression(string table, TemplateMeasureDefinition definition, Func<string, string> variable, string? materializedValue = null)
    {
        var name = "'" + table.Replace("'", "''", StringComparison.Ordinal) + "'";
        var configTable = variable("ConfigTable");
        var configValue = variable("ConfigValue");
        var result = variable("Result");
        // 限定 value 列, 避免模型中的同名度量覆盖行上下文里的配置值.
        var basis = materializedValue is not null ? $"""
            VAR {configValue} =
                {materializedValue}
            """ : $"""
            VAR {configTable} =
                FILTER (
                    {name},
                    {name}[name] = {Literal(definition.Name)}
                )

            VAR {configValue} =
                CONCATENATEX (
                    {configTable},
                    {name}[value]
                )
            """;
        var expression = $"""
            {basis}
            VAR {result} =
                {configValue}

            RETURN
                {result}
            """;
        if (definition.DataCategory == "ImageUrl")
        {
            var svgStart = variable("SvgStartImage");
            var svgMatchPosition = variable("SvgMatchPosition");
            // Status 被引擎拒绝作为变量名; SEARCH 也会命中 URI 内的 SVG, 已有地址必须原样保留.
            expression = $"""
                VAR {svgStart} =
                    "data:image/svg+xml;utf8,"

                {basis}

                VAR {svgMatchPosition} =
                    COALESCE (
                        SEARCH (
                            "<svg*</svg>",
                            {configValue},
                            1,
                            BLANK ()
                        ),
                        SEARCH (
                            "<svg*/>",
                            {configValue},
                            1,
                            BLANK ()
                        )
                    )

                VAR {result} =
                    IF (
                        ISBLANK ( {svgMatchPosition} )
                            || LEFT (
                                {configValue},
                                5
                            ) = "data:"
                            || LEFT (
                                {configValue},
                                7
                            ) = "http://"
                            || LEFT (
                                {configValue},
                                8
                            ) = "https://",
                        {configValue},
                        {svgStart} & {configValue}
                    )

                RETURN
                    {result}
                """;
        }
        return DaxFormatter.FormatMeasure(expression);
    }

    [GeneratedRegex(@"^\s*VAR\s+(?<name>(?<basis>ConfigTable|ConfigValue|SvgStartImage|SvgMatchPosition|Result)(?:[1-9][0-9]*)?)\s*=", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigVariable();

    /// <summary>为 DAX 变量选择未被表占用的确定名称; 当前及同批后续表名均参与, 不改业务对象名.</summary>
    /// <param name="preferred">生成模板使用的合法变量基名.</param>
    /// <param name="tableNames">大小写无关的已占用表名, 调用方负责包含尚未创建的表.</param>
    /// <returns>基名或首个空闲数字后缀名称, 相同表集合的结果保持一致.</returns>
    private static string Variable(string preferred, IReadOnlySet<string> tableNames)
    {
        var name = preferred;
        var suffix = 1;
        while (tableNames.Contains(name)) name = preferred + (suffix++).ToString(CultureInfo.InvariantCulture);
        return name;
    }

}
