// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarGeneratedMirror.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 核验生成日期镜像的完整归属和逐列映射, 并在私有候选中同步明确字段选集.

using System.Text.RegularExpressions;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>一次只读核验得到的镜像及其实际输出名到日期语义映射, 不构成写入授权.</summary>
internal sealed record CalendarMirrorBinding(TOM.Table Table, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Sources);

/// <summary>仅协调完整工具生成镜像, 未知或人工修改对象继续作为外部依赖保护.</summary>
internal static partial class CalendarGeneratedMirror
{
    /// <summary>返回完整已归属、单一直接计算分区和一对一源列均一致的生成镜像; 任一证明缺失返回 null.</summary>
    internal static CalendarMirrorBinding? Find(TOM.Table source)
    {
        if (source.Model is not { } model || !ModelConfigurationOwnership.IsOwned(source, source.Annotations)) return null;
        var roles = ModelConfigurationOwnership.ReadRoles(model);
        if (roles.Roles.GetValueOrDefault(source.Name) != "calendar") return null;
        var names = roles.Roles.Where(pair => pair.Value == "calendar-mirror").Select(pair => pair.Key).ToArray();
        if (names.Length != 1 || model.Tables.Find(names[0]) is not { } mirror || !ModelConfigurationOwnership.IsOwned(mirror, mirror.Annotations)
            || mirror.Partitions.Count != 1 || mirror.Partitions[0].Source is not TOM.CalculatedPartitionSource partition
            || partition.Expression.Trim() != source.Name && partition.Expression.Trim() != "'" + source.Name.Replace("'", "''", StringComparison.Ordinal) + "'") return null;
        var sourceMapping = ScaffoldCalendarWriter.CaptureOutput(source, new HashSet<string>(StringComparer.Ordinal)).Columns;
        var columns = mirror.Columns.OfType<TOM.CalculatedTableColumn>().ToArray();
        if (columns.Length != sourceMapping.Count || mirror.Columns.Any(column => column is not (TOM.CalculatedTableColumn or TOM.RowNumberColumn))) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var match = sourceMapping.SingleOrDefault(item => column.SourceColumn == Reference(source.Name, item.Name)
                || column.SourceColumn == "'" + source.Name.Replace("'", "''", StringComparison.Ordinal) + "'[" + item.Name.Replace("]", "]]", StringComparison.Ordinal) + "]");
            if (match is null || !referenced.Add(match.Name)) return null;
            fields.Add(column.Name, match.Field);
            sources.Add(column.Name, match.Name);
        }
        return new(mirror, fields, sources);
    }

    /// <summary>按已核验映射同步镜像, 保留仍存在列的对象/别名/lineage; 仅对原本完整归属的镜像重新标记.</summary>
    internal static void Apply(TOM.Table source, CalendarMirrorBinding? binding, IReadOnlyList<ModelConfigurationCalendarColumn> final,
        IReadOnlyDictionary<string, string>? renames = null)
    {
        if (binding is null) return;
        var mirror = binding.Table;
        var retained = final.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        // 镜像保留自己的显示别名, 已有源名按本轮精确更名表同步, 不按变化后的名字误删重建.
        string SourceOf(TOM.Column column) => renames?.GetValueOrDefault(binding.Sources.GetValueOrDefault(column.Name) ?? "")
            ?? binding.Sources.GetValueOrDefault(column.Name) ?? "";
        // 源字段的内部排序同样可随选集变化, 外部排序/层级/安全等引用已由共享保护器映射保留.
        foreach (var column in mirror.Columns.OfType<TOM.CalculatedTableColumn>()) column.SortByColumn = null;
        foreach (var column in mirror.Columns.OfType<TOM.CalculatedTableColumn>().ToArray())
        {
            var actual = SourceOf(column);
            if (!retained.Contains(actual)) mirror.Columns.Remove(column);
            else column.SourceColumn = Reference(source.Name, actual);
        }
        foreach (var item in final)
        {
            if (mirror.Columns.OfType<TOM.CalculatedTableColumn>().Any(column => SourceOf(column) == item.Name)) continue;
            if (mirror.Columns.ContainsName(item.Name)) throw new AppException("SCAFFOLD_CALENDAR_MAPPING", "日期镜像新增列与已有别名冲突", 3);
            mirror.Columns.Add(new TOM.CalculatedTableColumn
            {
                Name = item.Name,
                SourceColumn = Reference(source.Name, item.Name),
                DataType = Enum.Parse<TOM.DataType>(ScaffoldCalendarFields.Get(item.Field).DataType)
            });
        }
        foreach (var item in final)
        {
            var column = mirror.Columns.OfType<TOM.CalculatedTableColumn>().Single(value => value.SourceColumn == Reference(source.Name, item.Name)
                || SourceOf(value) == item.Name);
            var sort = source.Columns[item.Name].SortByColumn;
            if (sort is not null)
                column.SortByColumn = mirror.Columns.OfType<TOM.CalculatedTableColumn>().FirstOrDefault(value => value.SourceColumn == Reference(source.Name, sort.Name)
                    || SourceOf(value) == sort.Name);
        }
        ModelConfigurationOwnership.Mark(mirror, mirror.Annotations);
    }

    /// <summary>按模型标识符编码日期输出列, 不将名称当作可执行 DAX 片段.</summary>
    private static string Reference(string table, string column)
    {
        var identifier = BareIdentifier().IsMatch(table)
            ? table : "'" + table.Replace("'", "''", StringComparison.Ordinal) + "'";
        return identifier + "[" + column.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>编译期生成裸表标识符匹配器, 其他名称使用 DAX 单引号转义.</summary>
    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareIdentifier();
}
