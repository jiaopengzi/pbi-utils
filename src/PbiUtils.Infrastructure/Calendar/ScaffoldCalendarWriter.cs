// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\ScaffoldCalendarWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以日期语义而非旧编号映射离线日历, 保留既有关系与计算引用.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Application.SampleData;
using PbiUtils.Infrastructure.SampleData;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;

using PbiUtils.Infrastructure.Models;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>现有表保留兼容列, 不破坏报表和 CalendarMirror 的既有字段引用.</summary>
internal static partial class ScaffoldCalendarWriter
{
    internal const string OptionsAnnotation = "PbiUtils.Configuration.Calendar.v1";
    private sealed record CalendarMapping(string Name, string SourceColumn, string Field);
    private sealed record CalendarDocument(int SchemaVersion, ScaffoldCalendarOptions Options, string? StorageMode = null,
        string? GeneratorVersion = null, string? ResourceVersion = null, CalendarMapping[]? Columns = null,
        ModelConfigurationCalendarFunction? Function = null);

    /// <summary>选项和实际列映射由完整对象归属保护; 未知版本不推断为已停用CSV.</summary>
    private static CalendarDocument ReadDocument(TOM.Table table)
    {
        try
        {
            var json = table.Annotations.Find(OptionsAnnotation)?.Value ?? "null";
            using var parsed = JsonDocument.Parse(json);
            var document = parsed.RootElement.Deserialize<CalendarDocument>();
            if (document is null || document.SchemaVersion != 3 || document.Options is null || document.Options.TableName != table.Name) throw new JsonException();
            var properties = parsed.RootElement.EnumerateObject().Select(value => value.Name).ToArray();
            string[] allowed = ["SchemaVersion", "Options", "StorageMode", "GeneratorVersion", "ResourceVersion", "Columns", "Function"];
            if (properties.Length != allowed.Length || !properties.ToHashSet(StringComparer.Ordinal).SetEquals(allowed)) throw new JsonException();
            ScaffoldCalendar.ValidateOptions(document.Options);
            if (document.StorageMode != "function-m"
                || document.GeneratorVersion != ScaffoldCalendarM.FunctionGeneratorVersion
                || document.ResourceVersion != ScaffoldCalendar.LunarResourceVersion || document.Columns is null) throw new JsonException();
            var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
            if (document.Columns.Length != columns.Length || document.Columns.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != columns.Length
                || document.Columns.Any(value => columns.SingleOrDefault(column => column.Name == value.Name) is not { } column
                    || column.SourceColumn != value.SourceColumn || column.DataType.ToString() != ScaffoldCalendarFields.Get(value.Field).DataType)) throw new JsonException();
            ScaffoldCalendar.ValidateOptions(document.Options, document.Columns.Select(value => ScaffoldCalendarFields.Get(value.Field)).ToArray());
            VerifyFunction(table, document);
            return document;
        }
        catch (JsonException) { throw new AppException("CONFIGURATION_CALENDAR_OPTIONS_REQUIRED", "已有日期表缺少可验证的生成选项, 请先明确配置日期范围", 3); }
    }

    /// <summary>只回读工具记录的选项, 不从任意M推导更新权限.</summary>
    internal static ScaffoldCalendarOptions ReadOptions(TOM.Table table) => ReadDocument(table).Options;

    /// <summary>完整归属、当前 schema3 与函数绑定都有效才接受日期定义.</summary>
    internal static bool IsSelfContained(TOM.Table table)
    {
        ModelConfigurationOwnership.Require(table, table.Annotations);
        _ = ReadDocument(table);
        return true;
    }

    /// <summary>读取真实列到语义的映射, 历史错号只按固定后缀解释.</summary>
    internal static IReadOnlyList<ScaffoldCalendarColumn> ReadMapping(TOM.Table table)
    {
        var document = table.Annotations.Find(OptionsAnnotation) is null ? null : ReadDocument(table);
        return table.Columns.OfType<TOM.DataColumn>().Select(column => new ScaffoldCalendarColumn(column.SourceColumn,
            document?.Columns?.Single(value => value.Name == column.Name).Field ?? Semantic(column.Name))).ToArray();
    }

    /// <summary>从真实表读取模型名和源列; 供冻结摘要及候选回核共用.</summary>
    internal static ModelConfigurationCalendarOutput CaptureOutput(TOM.Table table, IReadOnlySet<string> priorNames)
    {
        var mapping = ReadMapping(table).ToDictionary(value => value.Name, value => value.Field, StringComparer.Ordinal);
        return new(table.Name, table.Columns.OfType<TOM.DataColumn>().Select(column =>
            new ModelConfigurationCalendarColumn(column.Name, column.SourceColumn, mapping[column.SourceColumn], !priorNames.Contains(column.Name))).ToArray(), FunctionBinding(table));
    }

    /// <summary>规划当前显式或 kind 默认选集及宿主保护字段; 标准列仅按明确编号选项覆盖, 未指定及业务别名保持, 名称冲突抛错.</summary>
    internal static IReadOnlyList<ModelConfigurationCalendarColumn> PlanColumns(ScaffoldCalendarOptions options,
        IReadOnlyList<ModelConfigurationCalendarColumn> prior, bool addSelected = true, IReadOnlySet<string>? requiredFields = null)
    {
        var selected = ScaffoldCalendarFields.Resolve(options);
        var selectedFields = selected.Select(column => column.Field).ToHashSet(StringComparer.Ordinal);
        var mapping = prior.Select(column => column with { Added = false }).ToList();
        if (mapping.Select(value => value.SourceColumn).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count
            || mapping.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count)
            throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期已有输出列名称重复", 2);
        if (addSelected && requiredFields is not null)
        {
            mapping.RemoveAll(column => !selectedFields.Contains(column.Field) && !requiredFields.Contains(column.Field));
            if (options.NumberedColumns is { } numbered)
                mapping = [.. mapping.Select(column => column with
                {
                    Name = CalendarColumnRename.StandardName(column.Name, column.Field, numbered),
                    SourceColumn = CalendarColumnRename.StandardName(column.SourceColumn, column.Field, numbered),
                    Added = CalendarColumnRename.StandardName(column.Name, column.Field, numbered) != column.Name
                })];
            if (mapping.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count
                || mapping.Select(value => value.SourceColumn).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count)
                throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期编号覆盖与已有别名冲突, 未合并或覆盖其他列", 2);
        }
        if (addSelected)
            foreach (var item in selected)
            {
                if (mapping.Any(value => value.Field == item.Field)) continue;
                if (mapping.FirstOrDefault(value => value.Name == item.Name) is { } existing)
                {
                    if (existing.Field != item.Field) throw new AppException("SCAFFOLD_COLUMN_TYPE_MISMATCH", "日期字段与模板语义冲突", 2);
                    continue;
                }
                if (mapping.Any(value => value.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) || value.SourceColumn.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期新增字段与已有名称冲突", 2);
                mapping.Add(new(item.Name, item.Name, item.Field, true));
            }
        return mapping;
    }

    /// <summary>持续配置与创建共享事实日期及目标月末规则, 只扩展范围而保留原类型与工作日覆盖.</summary>
    /// <param name="table">归属已核实的现有日期表.</param>
    /// <param name="sampleOptions">本轮真实示例日期选项, 不需要创建请求.</param>
    /// <param name="selectedCalendar">可选明确日期配置, 仅同一目标时约束事实覆盖范围.</param>
    /// <param name="storage">本轮准备阶段生成的真实示例数据.</param>
    /// <param name="token">逐行核查事实日期时的取消令牌.</param>
    /// <returns>保留其他生成选项并扩展至完整事实年份的日期配置.</returns>
    /// <exception cref="AppException">日期生成归属或明确日期范围无效.</exception>
    internal static ScaffoldCalendarOptions ExpandForSample(TOM.Table table, SampleDataOptions sampleOptions,
        ScaffoldCalendarOptions? selectedCalendar, SampleCsvStorage storage, CancellationToken token)
    {
        var options = ReadOptions(table);
        var (start, end) = DefaultYears(table, selectedCalendar, sampleOptions, storage, token);
        return options with { StartYear = start, EndYear = end };
    }
    /// <summary>生成内置日期角色与明确选择的独立日期表, 不猜测用户模板角色.</summary>
    /// <param name="model">本轮调用方拥有的完整模型.</param>
    /// <param name="calendar">明确选择的日期配置, null 保留已有模板日期角色.</param>
    /// <param name="sampleOptions">可选事实日期范围, 不隐式生成或刷新数据.</param>
    /// <param name="locale">生成代码使用的已验证语言.</param>
    /// <param name="builtin">调用方已核实的内置资源身份.</param>
    /// <param name="stage">调用方拥有的创建 staging 目录.</param>
    /// <param name="results">调用方聚合的生成结果, 不表示真实刷新成功.</param>
    /// <param name="warnings">调用方聚合的生成提示.</param>
    /// <param name="token">编译及事实日期核查中的取消令牌.</param>
    /// <param name="sample">本轮可选示例数据, 与调用方同一预算和生命周期绑定.</param>
    /// <param name="explicitCalendarRole">模板显式声明的日期角色, 不按近似名称猜测.</param>
    /// <exception cref="AppException">日期映射、目标名称或生成规则冲突时拒绝.</exception>
    internal static void Apply(TOM.Model model, ScaffoldCalendarOptions? calendar, SampleDataOptions? sampleOptions, string locale, bool builtin, string stage,
        List<ScaffoldTableResult> results, List<string> warnings, CancellationToken token, SampleCsvStorage? sample = null,
        string? explicitCalendarRole = null)
    {
        TOM.Table? existing = builtin ? model.Tables.Find("Calendar") : explicitCalendarRole is null ? null : model.Tables.Find(explicitCalendarRole);
        if (existing is not null)
        {
            var selected = calendar?.TableName == existing.Name ? calendar : null;
            var (Start, End) = DefaultYears(existing, calendar, sampleOptions, sample, token);
            var options = selected ?? new ScaffoldCalendarOptions("full", Start, End, existing.Name);
            if (builtin && selected is not null) ScaffoldTemplateCalendarShape.Apply(model, existing, selected, model.Tables.ContainsName("CalendarMirror"));
            Write(existing, options, selected is not null, stage, results, warnings, token, locale);
        }
        if (calendar is { } requested && requested.TableName != existing?.Name)
        {
            if (model.Tables.ContainsName(requested.TableName)) throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "日期表名称与模板对象冲突", 2);
            var table = new TOM.Table { Name = requested.TableName, DataCategory = "Time" };
            model.Tables.Add(table);
            Write(table, requested, true, stage, results, warnings, token, locale);
        }
    }

    /// <summary>内置默认范围来自源角色, 选模拟时范围扩展至完整事实年份.</summary>
    /// <param name="table">已有模板或生成日期表.</param>
    /// <param name="calendar">可选本轮日期配置, 仅对应当前表时约束范围.</param>
    /// <param name="sampleOptions">可选示例事实日期, 不代替实际 CSV 最早和最晚日期.</param>
    /// <param name="storage">可选真实示例存储, 按行核查事实日期和目标月末.</param>
    /// <param name="token">逐行核查中的取消令牌.</param>
    /// <returns>模板已有年份与本轮完整事实日期的最小覆盖范围.</returns>
    /// <exception cref="AppException">无法证明已有年份或明确选择未覆盖事实日期时拒绝.</exception>
    private static (int Start, int End) DefaultYears(TOM.Table table, ScaffoldCalendarOptions? calendar,
        SampleDataOptions? sampleOptions, SampleCsvStorage? storage, CancellationToken token)
    {
        var expression = (table.Partitions[0].Source as TOM.MPartitionSource)?.Expression ?? "";
        var match = MyRegex().Match(expression);
        var recorded = table.Annotations.Find(OptionsAnnotation) is null ? null : ReadOptions(table);
        if (recorded is null && !match.Success && calendar?.TableName != table.Name) throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "模板日期角色没有明确年份", 2);
        var start = recorded?.StartYear ?? (match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : calendar!.StartYear);
        var end = recorded?.EndYear ?? (match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : calendar!.EndYear);
        if (sampleOptions is { } sample)
        {
            var (Start, End) = sample.Validate();
            var first = Start;
            var last = End;
            if (storage is not null)
            {
                // 日期角色包括非活动送货关系, 不把生日等普通日期字段纳入事实覆盖.
                foreach (var (file, columns) in new (string File, int[] Columns)[] { (SampleTables.FileOrder, [3, 4]), (SampleTables.FileInventory, [4]), (SampleTables.FileCustomer, [5]), (SampleTables.FileSaleTarget, [3]) })
                    foreach (var row in storage.Read(file))
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var column in columns)
                        {
                            if (row[column].Length == 0) continue;
                            var date = DateOnly.ParseExact(row[column], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                            if (date < first) first = date;
                            if (file == SampleTables.FileSaleTarget) date = new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
                            if (date > last) last = date;
                        }
                    }
            }
            start = Math.Min(start, first.Year);
            end = Math.Max(end, last.Year);
            if (calendar is { } selected && selected.TableName == table.Name && (selected.StartYear > first.Year || selected.EndYear < last.Year))
                throw new AppException("SCAFFOLD_CALENDAR_COVERAGE", "日期表范围未覆盖模拟事实日期, 请调整年份", 2);
        }
        return (start, end);
    }

    /// <summary>编译 M 并更新已核验生成表的显式列选集, 保留必需引用和剩余列别名; 归属/映射失败抛错, 行数不代表刷新证明.</summary>
    internal static void Write(TOM.Table table, ScaffoldCalendarOptions options, bool addSelected, string? stage,
        List<ScaffoldTableResult> results, List<string> warnings, CancellationToken token, string? locale = null)
    {
        token.ThrowIfCancellationRequested();
        if (options.TableName != table.Name) throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期目标名称与实际表不一致", 2);
        CalendarProgramPlanner.RequireTableName(table.Model ?? throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期表必须属于当前模型", 2), table.Name);
        var selected = ScaffoldCalendarFields.Resolve(options);
        var binding = FunctionBinding(table);
        CalendarProgramPlanner.RequireName(table.Model!, ScaffoldCalendarM.FunctionName, table, binding);
        CalendarProgramPlanner.RequireSingleBinding(table.Model!, table, binding, token);
        var prior = table.Columns.OfType<TOM.DataColumn>().ToArray();
        var output = CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal));
        IReadOnlySet<string>? required = null;
        if (addSelected && table.Annotations.Find(OptionsAnnotation) is not null)
        {
            ModelConfigurationOwnership.Require(table, table.Annotations);
            required = CalendarColumnProtection.RequiredFields(table);
        }
        var planned = PlanColumns(options, output.Columns, addSelected, required);
        var mirror = required is null ? null : CalendarGeneratedMirror.Find(table);
        var rename = required is null ? null : CalendarColumnRename.Prepare(table, options, output.Columns, planned, token);
        if (required is not null) CalendarGeneratedLinguisticMetadata.Apply(table, output.Columns, planned);
        rename?.Apply();
        foreach (var item in planned)
            if (table.Columns.Find(item.Name) is TOM.DataColumn existing) existing.SourceColumn = item.SourceColumn;
        var names = planned.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var removed = prior.Where(column => !names.Contains(column.Name)).ToArray();
        // 生成排序只在辅助列同时输出时有效; 先解除指向待删除列的引用, 避免 TOM 留下已移除对象.
        foreach (var column in table.Columns.OfType<TOM.DataColumn>().Where(column => column.SortByColumn is not null && removed.Contains(column.SortByColumn))) column.SortByColumn = null;
        foreach (var column in removed) table.Columns.Remove(column);
        foreach (var item in planned.Where(value => value.Added))
        {
            // 更名后的可见名称也属于本轮新增映射, 但必须保留原列对象和 lineage.
            if (table.Columns.Find(item.Name) is { } existing)
            {
                if (prior.Contains(existing)) continue;
                throw new AppException("SCAFFOLD_COLUMN_TYPE_MISMATCH", "日期新增字段与已有对象冲突", 2);
            }
            table.Columns.Add(new TOM.DataColumn { Name = item.Name, SourceColumn = item.SourceColumn, DataType = Enum.Parse<TOM.DataType>(ScaffoldCalendarFields.Get(item.Field).DataType) });
        }
        var mapping = planned.Select(value => new CalendarMapping(value.Name, value.SourceColumn, value.Field)).ToList();
        if (prior.Length == 0) table.Columns[mapping.First(value => value.Field == "Dates").Name].IsKey = true;
        if (mapping.Any(item => !selected.Any(value => value.Field == item.Field)))
            warnings.Add($"{table.Name}: 为保留现有关系和报表引用, 保留必要兼容列");
        var normalized = options with
        {
            Fields = [.. selected.Select(column => column.Field)],
            Workdays = ScaffoldCalendar.ValidateWorkdays(options.Workdays),
            Overrides = [.. (options.Overrides ?? []).OrderBy(value => value.Date, StringComparer.Ordinal)]
        };
        if (mapping.Any(value => table.Columns[value.Name].DataType.ToString() != ScaffoldCalendarFields.Get(value.Field).DataType))
            throw new AppException("SCAFFOLD_COLUMN_TYPE_MISMATCH", "日期已有字段与语义类型不一致", 2);
        var program = ScaffoldCalendarM.Compile(normalized, mapping.Select(value => new ScaffoldCalendarColumn(value.SourceColumn, value.Field)).ToArray(),
            locale ?? binding?.Locale ?? "zh-cn", cancellationToken: token);
        WriteFunction(table, program, binding);
        ModelImportPartition.Set(table, program.InvocationExpression, nativeDate: true);
        foreach (var item in mapping)
        {
            var column = table.Columns[item.Name];
            // 两位年份标签跨百年重复, 不能绑定多个不同的年月排序键.
            var sortField = item.Field == "YearMonthUS" && options.EndYear - options.StartYear >= 100 ? null : ScaffoldCalendarFields.Get(item.Field).SortBy;
            var sort = sortField is null ? null : mapping.FirstOrDefault(value => value.Field == sortField);
            // 仅存在明确辅助字段时绑定; 首字母标签不设置无效排序.
            column.SortByColumn = sort is null ? null : table.Columns[sort.Name];
        }
        CalendarGeneratedMirror.Apply(table, mirror, planned, rename?.Renames);
        if (table.Annotations.Find(OptionsAnnotation) is { } old) table.Annotations.Remove(old);
        table.Annotations.Add(new TOM.Annotation
        {
            Name = OptionsAnnotation,
            Value = JsonSerializer.Serialize(new CalendarDocument(3, normalized,
            "function-m", ScaffoldCalendarM.FunctionGeneratorVersion, ScaffoldCalendar.LunarResourceVersion, [.. mapping], CalendarProgramPlanner.Capture(program)))
        });
        rename?.Complete();
        if (stage is not null) results.Add(new(table.Name, new DateOnly(options.EndYear, 12, 31).DayNumber - new DateOnly(options.StartYear, 1, 1).DayNumber + 1, 0, "populated"));
    }
    /// <summary>旧列编号并不可靠, 仅使用固定定义的语义部分.</summary>
    internal static string Semantic(string name) => name switch
    {
        "日期" => "Dates",
        "天" => "Day",
        "月份" => "MonthM",
        "年月" => "YearMonthM",
        "年" => "FY00",
        _ => name.StartsWith('C') && name.IndexOf('_') is var index && index > 0 ? name[(index + 1)..] : name
    };
    [GeneratedRegex(@"fxCalendar\(\s*(\d{4})\s*,\s*(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
