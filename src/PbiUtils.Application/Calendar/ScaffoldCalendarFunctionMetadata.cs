// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendarFunctionMetadata.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为自包含日期函数生成完整双语 Power Query 参数与函数文档元数据.

using System.Text;

namespace PbiUtils.Application.Calendar;

/// <summary>元数据只描述当前四参数契约, 字段选择和输出别名仍由共享编译器固定.</summary>
internal static class ScaffoldCalendarFunctionMetadata
{
    /// <summary>为指定函数追加类型化参数和完整说明; 固定示例在公历与农历范围内均有效.</summary>
    /// <param name="code">接收 M 查询片段的输出.</param>
    /// <param name="functionName">已校验的查询名称, 按 M 文本编码.</param>
    /// <param name="lunar">实际输出映射是否需要农历范围.</param>
    /// <param name="english">是否输出英文说明, 不改变业务值和参数名称.</param>
    internal static void Append(StringBuilder code, string functionName, bool lunar, bool english)
    {
        var range = lunar ? "{2000 .. 2099}" : "{1900 .. 2100}";
        var yearDescription = english
            ? (lunar ? "Integer year from 2000 through 2099; start year must not exceed end year."
                : "Integer year from 1900 through 2100; start year must not exceed end year.")
            : (lunar ? "整数年份 2000 至 2099, 开始年份不能晚于结束年份."
                : "整数年份 1900 至 2100, 开始年份不能晚于结束年份.");
        code.AppendLine("    FunctionType = type function (");
        Parameter(code, "StartYear", "number", english ? "Start year (inclusive)" : "开始年份(包含)", yearDescription, "2024", range, true);
        Parameter(code, "EndYear", "number", english ? "End year (inclusive)" : "结束年份(包含)", yearDescription, "2024", range, true);
        Parameter(code, "Workdays", "list", english ? "Weekly workdays" : "每周工作日",
            english ? "List of unique integers: Monday 0 through Sunday 6. An empty list means no weekly workdays."
                : "由周一 0 至周日 6 的唯一整数组成的列表, 空列表表示每周没有固定工作日.", "{0, 1, 2, 3, 4}", null, true);
        Parameter(code, "Overrides", "list", english ? "Daily overrides" : "逐日覆盖",
            english ? "List of [Date = date, IsWorkday = logical, Name = text] records; at most 10000 unique dates within the selected years. Overrides take priority over Workdays."
                : "[Date = date, IsWorkday = logical, Name = text] 记录列表, 最多包含所选年份内的 10000 个唯一日期. 逐日覆盖优先于 Workdays.", "{}", null, false);
        code.AppendLine("    ) as table meta [");
        Property(code, "Documentation.Name", functionName);
        Property(code, "Documentation.Description", english ? "Returns the selected calendar columns." : "返回所选日期表字段.");
        Property(code, "Documentation.LongDescription", (english
            ? "Generates the selected calendar columns using explicit years, weekly workdays and daily overrides, with no external data source. Output aliases and optional numbering are fixed by this generated function. "
            : "使用显式年份, 每周工作日和逐日覆盖生成所选日期表字段, 不读取外部数据源. 输出别名与可选编号由此生成函数固定. ")
            + "<ul><li><code>StartYear</code>: " + yearDescription + "</li><li><code>EndYear</code>: " + yearDescription
            + "</li><li><code>Workdays</code>: " + (english ? "Monday 0 through Sunday 6." : "周一 0 至周日 6.")
            + "</li><li><code>Overrides</code>: " + (english ? "Explicit Date, IsWorkday and Name records; maximum 10000 unique dates."
                : "显式 Date, IsWorkday 和 Name 记录, 最多 10000 个唯一日期.") + "</li></ul>"
            + (lunar ? (english ? " Lunar fields support Gregorian dates from 2000 through 2099; traditional festivals are not official leave schedules."
                : " 农历字段支持公历 2000 至 2099 年, 传统节日不代表法定放假安排.") : ""));
        Property(code, "Documentation.WhoAskedTheRightQuestion", "www.jiaopengzi.com");
        Property(code, "Documentation.Author", "焦棚子");
        code.AppendLine("        Documentation.Examples = {");
        code.Append("            [Description = ").Append(Text(english ? "Generate one year with Monday through Friday as workdays."
            : "生成一个完整年度的日期表, 周一至周五为工作日.")).AppendLine(",");
        var reference = functionName == ScaffoldCalendarM.FunctionName ? functionName : "#" + Text(functionName);
        code.Append("             Code = ").Append(Text(reference + "(2024, 2024, {0, 1, 2, 3, 4}, {})")).AppendLine(",");
        code.Append("             Result = ").Append(Text(english
            ? "Returns dates for 2024 (366 rows) with the selected columns, fixed output names and no daily overrides."
            : "返回 2024 年的日期表(366 行), 使用所选字段和固定输出名称, 不含逐日覆盖.")).AppendLine("]");
        code.AppendLine("        }");
        code.AppendLine("    ]");
    }

    /// <summary>追加参数元数据; list 的 AllowedValues 不错误限制为单个数字, 其元素约束由正文说明.</summary>
    private static void Parameter(StringBuilder code, string name, string type, string caption, string description,
        string sample, string? allowed, bool comma)
    {
        code.Append("        ").Append(name).Append(" as (type ").Append(type).Append(" meta [Documentation.FieldCaption = ")
            .Append(Text(caption)).Append(", Documentation.FieldDescription = ").Append(Text(description))
            .Append(", Documentation.SampleValues = {").Append(sample).Append('}');
        if (allowed is not null) code.Append(", Documentation.AllowedValues = ").Append(allowed);
        code.Append("])").AppendLine(comma ? "," : "");
    }

    /// <summary>追加以逗号结束的固定文档属性, 不把说明文本作为 M 代码执行.</summary>
    private static void Property(StringBuilder code, string name, string value)
        => code.Append("        ").Append(name).Append(" = ").Append(Text(value)).AppendLine(",");

    /// <summary>编码 M 字符串引号和转义引导符, 允许说明中安全出现实际查询名称.</summary>
    private static string Text(string value) => "\"" + value.Replace("#", "#(#)", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
