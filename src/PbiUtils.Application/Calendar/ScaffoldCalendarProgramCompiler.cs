// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendarProgramCompiler.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以字段依赖闭包编译无外部读取的独立日期函数, 保留实际源列与固定业务值.

using PbiUtils.Application.Code.PowerQuery;
using System.Globalization;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Calendar;

/// <summary>声明式字段和计算节点只生成实际需要的依赖, 不裁剪原始 M 文本.</summary>
internal static class ScaffoldCalendarProgramCompiler
{
    /// <summary>一个全局或逐行计算节点及其稳定直接依赖.</summary>
    private sealed record Calculation(string Expression, bool Global, params string[] Dependencies);

    /// <summary>字段结果表达式与直接依赖; 顺序仅由固定字段目录决定.</summary>
    private sealed record Field(string Expression, params string[] Dependencies);

    private static readonly Dictionary<string, Calculation> Calculations = BuildCalculations();
    private static readonly Dictionary<string, Field> Fields = BuildFields();

    /// <summary>验证最终映射并生成当前独立函数及调用.</summary>
    internal static ScaffoldCalendarProgram Compile(ScaffoldCalendarOptions options, IReadOnlyList<ScaffoldCalendarColumn> columns,
        string locale, string? functionName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(columns);
        if (locale is not ("zh-cn" or "en-us")) throw new AppException("INVALID_LOCALE", "日期函数说明语言必须为 zh-cn 或 en-us");
        _ = ScaffoldCalendarFields.Resolve(options);
        if (columns.Count is < 1 or > 256 || !columns.Any(column => column is not null && column.Field == "Dates")
            || columns.Any(column => column is null || string.IsNullOrWhiteSpace(column.Name) || column.Name.Length > 100
                || column.Name.Any(char.IsControl) || column.Name.Contains('\uFEFF', StringComparison.Ordinal))
            || columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
            throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期输出列映射无效或名称重复");
        var ordered = columns.OrderBy(column => ScaffoldCalendarFields.Get(column.Field).Number).ToArray();
        ScaffoldCalendar.ValidateOptions(options, ordered.Select(column => ScaffoldCalendarFields.Get(column.Field)).ToArray());
        functionName ??= ScaffoldCalendarM.FunctionName;
        if (string.IsNullOrWhiteSpace(functionName) || functionName.Length > 200 || functionName.Any(char.IsControl)
            || functionName.Contains('\uFEFF', StringComparison.Ordinal))
            throw new AppException("SCAFFOLD_CALENDAR_FUNCTION_NAME", "日期函数查询名称无效");
        var selected = ordered.Select(column => column.Field).Distinct(StringComparer.Ordinal).ToArray();
        var closure = Closure(selected, token);
        var lunar = selected.Any(field => ScaffoldCalendarFields.Get(field).RequiresLunar);
        var definition = Definition(ordered, closure, lunar, locale, functionName, token);
        var invocation = Invocation(options, functionName, token);
        token.ThrowIfCancellationRequested();
        var formattedDefinition = PowerQueryFormatter.Format(definition, token);
        var formattedInvocation = PowerQueryFormatter.Format(invocation, token);
        if ((long)Encoding.UTF8.GetByteCount(formattedDefinition) + Encoding.UTF8.GetByteCount(formattedInvocation) > PowerQueryFormatter.MaximumOutputBytes)
            throw new AppException("SCAFFOLD_CALENDAR_PROGRAM_LIMIT", "日期函数和调用代码超过允许的总大小");
        return new(functionName, formattedDefinition, formattedInvocation, locale,
            ScaffoldCalendarM.FunctionGeneratorVersion, ScaffoldCalendar.LunarResourceVersion);
    }

    /// <summary>按字段目录和依赖声明顺序深度优先展开, 计算拓扑不改变实际输出列顺序.</summary>
    private static List<string> Closure(IEnumerable<string> fields, CancellationToken token)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var field in fields)
            foreach (var dependency in Fields[field].Dependencies) Add(dependency);
        return result;

        // 节点定义是受信静态有向无环图; 递归农历函数只在 M 内自引用, 不作为图依赖.
        void Add(string name)
        {
            token.ThrowIfCancellationRequested();
            if (!included.Add(name)) return;
            foreach (var dependency in Calculations[name].Dependencies) Add(dependency);
            result.Add(name);
        }
    }

    /// <summary>根据实际列和语言生成返回函数值的查询; 校验直接参与返回值避免 M 惰性求值跳过.</summary>
    /// <param name="columns">已核验且按固定目录排序的实际输出列.</param>
    /// <param name="closure">按拓扑顺序展开的计算依赖闭包.</param>
    /// <param name="lunar">是否生成农历依赖及其年份约束.</param>
    /// <param name="locale">已核验的说明语言.</param>
    /// <param name="functionName">已核验且仍需按 M 标识符规则编码的函数名称.</param>
    /// <param name="token">取消生成的令牌.</param>
    /// <returns>包含动态计算节点和完整参数元数据的 M 函数查询.</returns>
    /// <exception cref="OperationCanceledException">生成过程中收到取消请求.</exception>
    private static string Definition(IReadOnlyList<ScaffoldCalendarColumn> columns, IReadOnlyList<string> closure, bool lunar,
        string locale, string functionName, CancellationToken token)
    {
        var english = locale == "en-us";
        var outputStep = functionName == "TypedFunction" ? "TypedCalendarFunction" : "TypedFunction";
        var code = new StringBuilder("let\n");
        Comment(code, english ? "Generate selected fields from explicit parameters; no external data source." : "按显式参数生成所选字段, 不读取外部数据源.", 4);
        var reference = functionName == ScaffoldCalendarM.FunctionName ? functionName : "#" + Text(functionName);
        code.Append("    ").Append(reference).AppendLine(" = (StartYear as number, EndYear as number, Workdays as list, Overrides as list) as table =>");
        code.AppendLine("    let");
        Comment(code, english ? "Validate years, workweek and every override before generating any date." : "先验证年份, 工作周和全部逐日覆盖, 再生成日期.", 8);
        code.AppendLine("        Integer = (x as any) as logical => Value.Is(x, type number) and Number.RoundDown(x) = x,");
        code.AppendLine("""        Fail = (message as text) => error Error.Record("InvalidCalendar", message, null),""");
        code.Append("        ValidYears = Integer(StartYear) and Integer(EndYear) and StartYear <= EndYear and StartYear >= ")
            .Append(lunar ? "2000" : "1900").Append(" and EndYear <= ").Append(lunar ? "2099" : "2100").AppendLine(",");
        code.AppendLine("        ValidWeek = List.Count(Workdays) <= 7 and List.Count(List.Distinct(Workdays)) = List.Count(Workdays) and List.AllTrue(List.Transform(Workdays, each Integer(_) and _ >= 0 and _ <= 6)),");
        code.AppendLine("        First = #date(StartYear, 1, 1),");
        code.AppendLine("        Last = #date(EndYear, 12, 31),");
        code.AppendLine("        CleanText = (x as any) as logical => Value.Is(x, type text) and Text.Length(x) <= 200 and List.AllTrue(List.Transform(Text.ToList(x), each let n = Character.ToNumber(_) in n >= 32 and n <> 65279 and (n < 127 or n > 159))),");
        code.AppendLine("""        OverrideOk = (o as any) as logical => Value.Is(o, type record) and Record.HasFields(o, {"Date", "IsWorkday", "Name"}) and Record.FieldCount(o) = 3 and Value.Is(o[Date], type date) and o[Date] >= First and o[Date] <= Last and Value.Is(o[IsWorkday], type logical) and CleanText(o[Name]),""");
        code.AppendLine("        ValidOverrides = List.Count(Overrides) <= 10000 and List.AllTrue(List.Transform(Overrides, OverrideOk)) and List.Count(List.Distinct(List.Transform(Overrides, each [Date]))) = List.Count(Overrides),");
        foreach (var name in closure.Where(name => Calculations[name].Global))
        {
            token.ThrowIfCancellationRequested();
            if (name == "LunarBytes") Comment(code, english ? "Frozen lunar year rules support Gregorian dates from 2000 through 2099; festivals are not official leave schedules." : "固定农历年规则支持公历 2000 至 2099 年; 传统节日不表示法定放假安排.", 8);
            if (name == "OverrideMap") Comment(code, english ? "An explicit daily override takes priority over the weekly workday set." : "逐日覆盖优先于每周工作日集合.", 8);
            var expression = name == "LunarBytes"
                ? "List.Buffer(Binary.ToList(Binary.FromText(" + Text(Convert.ToBase64String(ScaffoldCalendar.GetLunarYearData())) + ", BinaryEncoding.Base64)))"
                : Calculations[name].Expression;
            code.Append("        ").Append(name).Append(" = ").Append(expression).AppendLine(",");
        }
        Comment(code, english ? "Only required dependencies are calculated; output aliases retain catalog order." : "只计算所需依赖; 输出别名保持固定字段目录顺序.", 8);
        var rowNodes = closure.Where(name => !Calculations[name].Global).ToArray();
        code.AppendLine("        Row = (d as date) as list =>");
        if (rowNodes.Length > 0)
        {
            code.AppendLine("            let");
            for (var index = 0; index < rowNodes.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                code.Append("                ").Append(rowNodes[index]).Append(" = ").Append(Calculations[rowNodes[index]].Expression)
                    .AppendLine(index + 1 == rowNodes.Length ? "" : ",");
            }
            code.Append("            in ");
        }
        else code.Append("            ");
        code.Append('{').AppendJoin(", ", columns.Select(column => Fields[column.Field].Expression)).AppendLine("},");
        code.Append("        Result = if not ValidYears then Fail(").Append(Text(lunar ? "Lunar fields require years 2000-2099" : "Gregorian years must be 1900-2100"))
            .AppendLine(")");
        code.AppendLine("""            else if not ValidWeek then Fail("Workdays must contain unique integers from 0 to 6")""");
        code.AppendLine("""            else if not ValidOverrides then Fail("Overrides must have unique dates in range and valid names; maximum 10000 rows")""");
        code.Append("            else Table.TransformColumnTypes(Table.FromRows(List.Transform(List.Dates(First, Duration.Days(Last - First) + 1, #duration(1, 0, 0, 0)), Row), ")
            .Append(List(columns.Select(column => column.Name))).Append("), {")
            .AppendJoin(", ", columns.Select(column => "{" + Text(column.Name) + ", " + Type(column.Field) + "}"))
            .AppendLine("""}, "en-US")""");
        code.AppendLine("    in");
        code.AppendLine("        Result,");
        Comment(code, english ? "Parameter documentation is localized; business names and values remain unchanged." : "参数说明随界面语言变化, 业务名称和值保持原样.", 4);
        ScaffoldCalendarFunctionMetadata.Append(code, functionName, lunar, english);
        // 将类型包装绑定为最后一个步骤并直接返回, 保留参数元数据和函数值以消除 Power Query 简化提示.
        code.AppendLine(",");
        code.Append("    ").Append(outputStep).Append(" = Value.ReplaceType(").Append(reference).AppendLine(", FunctionType)");
        code.AppendLine("in");
        code.Append("    ").Append(outputStep);
        return code.ToString();
    }

    /// <summary>生成唯一函数调用, 覆盖和工作周先规范排序以保证跨区域确定性.</summary>
    private static string Invocation(ScaffoldCalendarOptions options, string functionName, CancellationToken token)
    {
        var reference = functionName == ScaffoldCalendarM.FunctionName ? functionName : "#" + Text(functionName);
        var code = new StringBuilder(reference + "(" + Number(options.StartYear) + ", " + Number(options.EndYear) + ", {");
        code.AppendJoin(", ", ScaffoldCalendar.ValidateWorkdays(options.Workdays).Select(Number)).Append("}, {");
        var first = true;
        foreach (var value in (options.Overrides ?? []).OrderBy(value => value.Date, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var date = DateOnly.ParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!first) code.Append(", ");
            first = false;
            code.Append("[Date = #date(").Append(Number(date.Year)).Append(", ").Append(Number(date.Month)).Append(", ").Append(Number(date.Day))
                .Append("), IsWorkday = ").Append(value.IsWorkday ? "true" : "false").Append(", Name = ").Append(Text(value.Name)).Append(']');
        }
        return code.Append("})").ToString();
    }

    /// <summary>只写入固定本地化说明, 不把用户业务值插入注释.</summary>
    private static void Comment(StringBuilder code, string text, int indent) => code.Append(' ', indent).Append("// ").AppendLine(text);

    /// <summary>M 文本分别编码引号和转义引导符, 不允许业务名称注入表达式.</summary>
    private static string Text(string value) => "\"" + value.Replace("#", "#(#)", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>输出固定次序的 M 文本列表.</summary>
    private static string List(IEnumerable<string> values) => "{" + string.Join(", ", values.Select(Text)) + "}";

    /// <summary>数字始终使用不受系统区域影响的十进制格式.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>字段输出类型仅来自共享固定目录.</summary>
    private static string Type(string field) => ScaffoldCalendarFields.Get(field).DataType switch
    {
        "DateTime" => "type date",
        "Int64" => "Int64.Type",
        "Double" => "type number",
        _ => "type text"
    };

    /// <summary>声明逐行和全局计算依赖; 固定标签与旧 v2 计算含义保持一致.</summary>
    /// <returns>按精确节点名称索引的固定 M 表达式与直接依赖.</returns>
    private static Dictionary<string, Calculation> BuildCalculations() => new(StringComparer.Ordinal)
    {
        ["Pad"] = new("""(n as number, size as number) as text => Text.PadStart(Number.ToText(n, "0", "en-US"), size, "0")""", true),
        ["WeekCN"] = new("""{"一", "二", "三", "四", "五", "六", "日"}""", true),
        ["WeekEN"] = new("""{"Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"}""", true),
        ["MonthCN"] = new("""{"一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月"}""", true),
        ["MonthEN"] = new("""{"January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"}""", true),
        ["DefaultWeek"] = new("List.Sort(Workdays) = {0, 1, 2, 3, 4}", true),
        ["OverrideKey"] = new("""(d as date) as text => Pad(Date.Year(d), 4) & "-" & Pad(Date.Month(d), 2) & "-" & Pad(Date.Day(d), 2)""", true, "Pad"),
        ["OverrideMap"] = new("Record.FromList(Overrides, List.Transform(Overrides, each OverrideKey([Date])))", true, "OverrideKey"),
        ["LunarBytes"] = new("", true),
        ["YearRules"] = new("List.Buffer(List.Transform({0..100}, (i) => let at = 12 + i * 3 in LunarBytes{at} + LunarBytes{at + 1} * 256 + LunarBytes{at + 2} * 65536))", true, "LunarBytes"),
        ["MonthRules"] = new("List.Buffer(List.Combine(List.Transform({0..100}, (i) => let rule = YearRules{i}, leap = Number.Mod(Number.IntegerDivide(rule, 8192), 16) in List.Transform({1..(if leap = 0 then 12 else 13)}, (m) => [Year = 1999 + i, Month = m - (if leap <> 0 and m > leap then 1 else 0), Leap = leap <> 0 and m = leap + 1, Days = 29 + Number.Mod(Number.IntegerDivide(rule, Number.Power(2, m - 1)), 2)]))))", true, "YearRules"),
        ["MonthStarts"] = new("List.Buffer(List.Generate(() => [i = 0, date = #date(1999, 2, 16)], each [i] < List.Count(MonthRules), each [i = [i] + 1, date = Date.AddDays([date], MonthRules{[i]}[Days])], each [date]))", true, "MonthRules"),
        ["LunarMonths"] = new("List.Buffer(List.Transform(List.Positions(MonthRules), (i) => Record.Combine({MonthRules{i}, [Start = MonthStarts{i}]})))", true, "MonthRules", "MonthStarts"),
        ["FindMonth"] = new("(d as date, lo as number, hi as number) as record => if lo = hi then LunarMonths{lo} else let mid = Number.IntegerDivide(lo + hi + 1, 2) in if LunarMonths{mid}[Start] <= d then @FindMonth(d, mid, hi) else @FindMonth(d, lo, mid - 1)", true, "LunarMonths"),
        ["LunarMonthCN"] = new("""{"正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "腊月"}""", true),
        ["LunarDayCN"] = new("""{"初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十", "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十", "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"}""", true),
        ["Animals"] = new("""{"鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"}""", true),
        ["y"] = new("Date.Year(d)", false),
        ["m"] = new("Date.Month(d)", false),
        ["day"] = new("Date.Day(d)", false),
        ["wd"] = new("Date.DayOfWeek(d, Day.Monday)", false),
        ["ys"] = new("#date(y, 1, 1)", false, "y"),
        ["ms"] = new("#date(y, m, 1)", false, "y", "m"),
        ["q"] = new("Number.IntegerDivide(m - 1, 3) + 1", false, "m"),
        ["qs"] = new("#date(y, (q - 1) * 3 + 1, 1)", false, "y", "q"),
        ["h"] = new("if m < 7 then 1 else 2", false, "m"),
        ["hs"] = new("#date(y, if h = 1 then 1 else 7, 1)", false, "y", "h"),
        ["ws"] = new("Date.AddDays(d, -wd)", false, "wd"),
        ["ye"] = new("Date.AddDays(Date.AddYears(ys, 1), -1)", false, "ys"),
        ["me"] = new("Date.AddDays(Date.AddMonths(ms, 1), -1)", false, "ms"),
        ["qe"] = new("Date.AddDays(Date.AddMonths(qs, 3), -1)", false, "qs"),
        ["he"] = new("Date.AddDays(Date.AddMonths(hs, 6), -1)", false, "hs"),
        ["dy"] = new("Duration.Days(d - ys) + 1", false, "ys"),
        ["dq"] = new("Duration.Days(d - qs) + 1", false, "qs"),
        ["dh"] = new("Duration.Days(d - hs) + 1", false, "hs"),
        ["ny"] = new("Duration.Days(ye - ys) + 1", false, "ye", "ys"),
        ["nq"] = new("Duration.Days(qe - qs) + 1", false, "qe", "qs"),
        ["nh"] = new("Duration.Days(he - hs) + 1", false, "he", "hs"),
        ["nm"] = new("Date.Day(me)", false, "me"),
        ["wn"] = new("Number.IntegerDivide(dy - 1 + Date.DayOfWeek(ys, Day.Monday), 7) + 1", false, "dy", "ys"),
        ["o"] = new("Record.FieldOrDefault(OverrideMap, OverrideKey(d), null)", false, "OverrideMap", "OverrideKey"),
        ["work"] = new("List.Contains(Workdays, wd)", false, "wd"),
        ["status"] = new("if o = null then (if work then \"工作日\" else if DefaultWeek then \"周末\" else \"休息日\") else if o[IsWorkday] then (if work then \"工作日\" else \"补班\") else \"假期\"", false, "o", "work", "DefaultWeek"),
        ["lm"] = new("FindMonth(d, 0, List.Count(LunarMonths) - 1)", false, "FindMonth", "LunarMonths"),
        ["ld"] = new("Duration.Days(d - lm[Start]) + 1", false, "lm"),
        ["festival"] = new("""if lm[Leap] then "" else Record.FieldOrDefault([#"1-1" = "春节", #"1-15" = "元宵节", #"5-5" = "端午节", #"7-7" = "七夕", #"8-15" = "中秋节", #"9-9" = "重阳节", #"12-8" = "腊八节"], Pad(lm[Month], 1) & "-" & Pad(ld, 1), "")""", false, "lm", "ld", "Pad")
    };

    /// <summary>固定 63 字段表达式, 计算依赖与源列命名独立, 不受界面语言影响.</summary>
    /// <returns>按精确字段标识索引的固定 M 结果表达式与直接依赖.</returns>
    private static Dictionary<string, Field> BuildFields() => new(StringComparer.Ordinal)
    {
        ["Dates"] = new("d"),
        ["Day"] = new("day", "day"),
        ["WeekDay"] = new("wd", "wd"),
        ["WeekCNS1"] = new("WeekCN{wd}", "WeekCN", "wd"),
        ["WeekCNS3"] = new("\"星期\" & WeekCN{wd}", "WeekCN", "wd"),
        ["WeekENS1"] = new("Text.Start(WeekEN{wd}, 1)", "WeekEN", "wd"),
        ["WeekENS3"] = new("Text.Start(WeekEN{wd}, 3)", "WeekEN", "wd"),
        ["WeekEN"] = new("WeekEN{wd}", "WeekEN", "wd"),
        ["WeekIndex"] = new("Number.IntegerDivide(Duration.Days(d - #date(1900, 1, 1)), 7) + 1"),
        ["WeekNumber"] = new("wn", "wn"),
        ["Week"] = new("\"W\" & Pad(wn, 2)", "wn", "Pad"),
        ["YearWeek"] = new("""Pad(y, 4) & "W" & Pad(wn, 2)""", "y", "wn", "Pad"),
        ["Month"] = new("m", "m"),
        ["MonthM"] = new("\"M\" & Pad(m, 2)", "m", "Pad"),
        ["MonthCN"] = new("MonthCN{m - 1}", "MonthCN", "m"),
        ["MonthENS3"] = new("Text.Start(MonthEN{m - 1}, 3)", "MonthEN", "m"),
        ["MonthEN"] = new("MonthEN{m - 1}", "MonthEN", "m"),
        ["YearMonthM"] = new("""Pad(y, 4) & "M" & Pad(m, 2)""", "y", "m", "Pad"),
        ["YearMonthUS"] = new("""Text.Start(MonthEN{m - 1}, 3) & "-" & Pad(Number.Mod(y, 100), 2)""", "MonthEN", "m", "y", "Pad"),
        ["YearMonth"] = new("y * 100 + m", "y", "m"),
        ["Quarter"] = new("q", "q"),
        ["QuarterQ"] = new("\"Q\" & Pad(q, 1)", "q", "Pad"),
        ["YearQuarterQ"] = new("""Pad(y, 4) & "Q" & Pad(q, 1)""", "y", "q", "Pad"),
        ["YearQuarter"] = new("y * 100 + q", "y", "q"),
        ["HalfOfYearCN"] = new("if h = 1 then \"上半年\" else \"下半年\"", "h"),
        ["HalfOfYearEN"] = new("\"H\" & Pad(h, 1)", "h", "Pad"),
        ["YearHalf"] = new("""Pad(y, 4) & "H" & Pad(h, 1)""", "y", "h", "Pad"),
        ["Year"] = new("y", "y"),
        ["FY00"] = new("\"FY\" & Pad(Number.Mod(y, 100), 2)", "y", "Pad"),
        ["FY"] = new("\"FY\""),
        ["StartOfWeek"] = new("ws", "ws"),
        ["StartOfMonth"] = new("ms", "ms"),
        ["StartOfQuarter"] = new("qs", "qs"),
        ["StartOfHalfYear"] = new("hs", "hs"),
        ["StartOfYear"] = new("ys", "ys"),
        ["EndOfWeek"] = new("Date.AddDays(ws, 6)", "ws"),
        ["EndOfMonth"] = new("me", "me"),
        ["EndOfQuarter"] = new("qe", "qe"),
        ["EndOfHalfYear"] = new("he", "he"),
        ["EndOfYear"] = new("ye", "ye"),
        ["DayOfWeek"] = new("wd + 1", "wd"),
        ["DayOfMonth"] = new("day", "day"),
        ["DayOfQuarter"] = new("dq", "dq"),
        ["DayOfHalfYear"] = new("dh", "dh"),
        ["DayOfYear"] = new("dy", "dy"),
        ["DaysOfWeek"] = new("7"),
        ["DaysOfMonth"] = new("nm", "nm"),
        ["DaysOfQuarter"] = new("nq", "nq"),
        ["DaysOfHalfYear"] = new("nh", "nh"),
        ["DaysOfYear"] = new("ny", "ny"),
        ["ProgressOfWeek"] = new("(wd + 1) / 7", "wd"),
        ["ProgressOfMonth"] = new("day / nm", "day", "nm"),
        ["ProgressOfQuarter"] = new("dq / nq", "dq", "nq"),
        ["ProgressOfHalfYear"] = new("dh / nh", "dh", "nh"),
        ["ProgressOfYear"] = new("dy / ny", "dy", "ny"),
        ["Holiday"] = new("if o <> null then o[Name] else festival", "o", "festival"),
        ["Animal"] = new("Animals{Number.Mod(lm[Year] - 4, 12)}", "Animals", "lm"),
        ["LunarDateCN"] = new("LunarDayCN{ld - 1}", "LunarDayCN", "ld"),
        ["LunarMonthCN"] = new("""(if lm[Leap] then "闰" else "") & LunarMonthCN{lm[Month] - 1}""", "lm", "LunarMonthCN"),
        ["LunarDate"] = new("ld", "ld"),
        ["LunarMonth"] = new("lm[Month]", "lm"),
        ["LunarYear"] = new("lm[Year]", "lm"),
        ["Status"] = new("status", "status")
    };
}
