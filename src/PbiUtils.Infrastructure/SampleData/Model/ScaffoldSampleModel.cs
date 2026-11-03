// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\Model\ScaffoldSampleModel.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 新建时复用内嵌样例关系和基础度量, 原位覆盖同名表数据并保留模板引用.

using System.Text.Json;
using Microsoft.AnalysisServices.Tabular.Serialization;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Infrastructure.Models;

namespace PbiUtils.Infrastructure.SampleData.Model;

/// <summary>仅修改新建工作区的私有 TOM 副本, 不用于绕过已有模型的生成归属核验.</summary>
internal static class ScaffoldSampleModel
{
    private const string SourceRoot = "pbip/demo.SemanticModel/definition/";
    private static readonly HashSet<string> Tables = SampleTables.AllFiles.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    /// <summary>在同名模板表保留对象和额外成员, 替换标准字段及导入分区; 不兼容计算列明确失败.</summary>
    /// <param name="model">本轮隔离模型.</param>
    /// <param name="roles">由创建者维护的生成角色.</param>
    /// <param name="warnings">接收额外兼容字段的空值说明.</param>
    internal static void ApplyTables(TOM.Model model, Dictionary<string, string> roles, List<string> warnings)
    {
        foreach (var file in SampleTables.AllFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var table = model.Tables.Find(name);
            if (table is null) { table = new TOM.Table { Name = name }; model.Tables.Add(table); }
            var header = SampleTables.Header(file);
            for (var index = 0; index < header.Count; index++)
            {
                var existing = table.Columns.Find(header[index]);
                if (existing is not null && existing is not TOM.DataColumn)
                    throw new AppException("SCAFFOLD_COLUMN_MAPPING", "同名模拟字段与模板计算列不兼容, 未发布工程", 2);
                if (existing is not TOM.DataColumn column) { column = new TOM.DataColumn { Name = header[index] }; table.Columns.Add(column); }
                column.SourceColumn = header[index];
                column.DataType = ScaffoldDataWriter.SampleType(file, index);
            }
            var extra = table.Columns.OfType<TOM.DataColumn>().Where(column => !header.Contains(column.SourceColumn, StringComparer.Ordinal)).ToArray();
            if (extra.Any(column => string.IsNullOrEmpty(column.SourceColumn))
                || table.Columns.OfType<TOM.DataColumn>().Select(column => column.SourceColumn).Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Columns.OfType<TOM.DataColumn>().Count())
                throw new AppException("SCAFFOLD_COLUMN_MAPPING", "模板兼容字段缺少唯一源列映射, 未发布工程", 2);
            var compatible = extra.Length > 0 || table.Name != name
                || !table.Columns.OfType<TOM.DataColumn>().Select(column => column.SourceColumn).SequenceEqual(header);
            var relative = (compatible ? "data/model/" : "data/") + file;
            table.Partitions.Clear();
            ModelImportPartition.Set(table, ScaffoldDataWriter.CsvExpression(relative, table.Columns.OfType<TOM.DataColumn>()));
            foreach (var prior in roles.Keys.Where(key => key.Equals(table.Name, StringComparison.OrdinalIgnoreCase)).ToArray()) roles.Remove(prior);
            roles[table.Name] = compatible ? "sample-compatible" : "sample-direct";
            if (extra.Length > 0) warnings.Add($"{table.Name}: 同名表已接入本轮模拟数据, 保留的模板额外数据列没有样例映射, 值为空");
        }
    }

    /// <summary>标准十一表直接消费原始行; 模板额外数据列按原类型保留为空, 不解析或执行模板查询.</summary>
    /// <param name="table">具有标准模拟字段及可选兼容数据列的目标表.</param>
    /// <param name="storage">已完成本轮生成的固定十一表存储.</param>
    /// <returns>按目标源列顺序排列的流式记录; 未知表或损坏源记录明确失败.</returns>
    internal static IEnumerable<string[]> CompatibleRows(TOM.Table table, SampleCsvStorage storage)
    {
        var file = SampleTables.AllFiles.Single(value => Path.GetFileNameWithoutExtension(value).Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        var header = SampleTables.Header(file);
        var indexes = table.Columns.OfType<TOM.DataColumn>().Select(column =>
            header.Select((name, index) => (name, index)).Where(value => value.name == column.SourceColumn).Select(value => value.index).DefaultIfEmpty(-1).Single()).ToArray();
        foreach (var row in storage.Read(file)) yield return indexes.Select(index => index < 0 ? "" : row[index]).ToArray();
    }

    /// <summary>读取固定资源定义, 关系只绑定真实端点; 基础度量同表覆盖、跨表同名拒绝, 缺少日期字段在摘要说明.</summary>
    /// <param name="model">已完成日期字段规划的隔离模型.</param>
    /// <param name="roles">已有生成角色, 用于定位实际日期表.</param>
    /// <param name="warnings">关系缺失及覆盖摘要.</param>
    internal static void ApplyDefinitions(TOM.Model model, Dictionary<string, string> roles, List<string> warnings)
    {
        using var source = LoadSource();
        var measures = model.Tables.Find("Measure");
        var measureNames = source.Model.Tables["Measure"].Measures.Select(measure => measure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (model.Tables.Where(table => table != measures).SelectMany(table => table.Measures).Any(measure => measureNames.Contains(measure.Name)))
            throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "模拟数据基础度量值名称与其他表已有对象冲突, 未发布工程", 2);
        AddAuxiliary(model, source.Model.Tables["MQHY"], roles);
        AddAuxiliary(model, source.Model.Tables["RefreshTime"], roles);
        var calendarName = roles.FirstOrDefault(pair => pair.Value == "calendar").Key;
        var calendar = calendarName is null ? model.Tables.Find("Calendar") : model.Tables.Find(calendarName);
        TOM.Column? CalendarColumn(string name)
        {
            if (calendar is null) return null;
            var field = name switch { "dates" => "Dates", "M" => "MonthM", "Q" => "QuarterQ", "H" => "HalfOfYearEN", "FY" => "FY", _ => null };
            if (field is null) return null;
            if (calendar.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null)
            {
                var mapping = ScaffoldCalendarWriter.ReadMapping(calendar).FirstOrDefault(value => value.Field == field);
                return mapping is null ? null : calendar.Columns.OfType<TOM.DataColumn>().FirstOrDefault(column => column.SourceColumn == mapping.Name);
            }
            return calendar.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || ScaffoldCalendarWriter.Semantic(column.Name) == field);
        }
        // 样例内部关系及其日期绑定整体采用固定资源, 避免保留旧区县链后再增加城市直连产生重复活动路径.
        foreach (var relation in model.Relationships.OfType<TOM.SingleColumnRelationship>().Where(relation =>
            Tables.Contains(relation.FromTable.Name) && (Tables.Contains(relation.ToTable.Name) || relation.ToTable == calendar)
            || Tables.Contains(relation.ToTable.Name) && relation.FromTable == calendar).ToArray()) model.Relationships.Remove(relation);
        var installed = 0;
        var skipped = 0;
        foreach (var original in source.Model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            TOM.Column? Resolve(TOM.Column column) => column.Table.Name == "Calendar"
                ? CalendarColumn(column.Name) : model.Tables.Find(column.Table.Name)?.Columns.Find(column.Name);
            var from = Resolve(original.FromColumn);
            var to = Resolve(original.ToColumn);
            if (from is null || to is null) { skipped++; continue; }
            var relation = (TOM.SingleColumnRelationship)original.Clone();
            relation.FromColumn = from; relation.ToColumn = to;
            foreach (var existing in model.Relationships.OfType<TOM.SingleColumnRelationship>()
                .Where(value => value.FromColumn == from && value.ToColumn == to).ToArray()) model.Relationships.Remove(existing);
            if (model.Relationships.ContainsName(relation.Name)) relation.Name = "PbiUtilsSample-" + relation.Name;
            model.Relationships.Add(relation); installed++;
        }
        if (skipped > 0) warnings.Add($"模拟数据已复用 {installed} 条关系; 缺少已选日期字段或日期表的 {skipped} 条关系未生成, 可配置日期字段后再建立");
        if (measures is null)
        {
            measures = new TOM.Table { Name = "Measure", ExcludeFromModelRefresh = true };
            model.Tables.Add(measures);
            ModelImportPartition.Set(measures, "#table({}, {})");
        }
        foreach (var original in source.Model.Tables["Measure"].Measures)
        {
            if (measures.Measures.Find(original.Name) is { } existing) original.CopyTo(existing);
            else measures.Measures.Add(original.Clone());
            ModelConfigurationOwnership.Mark(measures.Measures[original.Name], measures.Measures[original.Name].Annotations);
        }
        roles[measures.Name] = "sample-measures";
    }

    /// <summary>辅助查询启用加载时复用正式表定义, 返回无跨模型分组引用的独立克隆.</summary>
    /// <param name="name">仅由固定查询目录提供的标准辅助表名.</param>
    /// <returns>调用者拥有的完整表定义; 内嵌资源缺失或表名未知时明确失败.</returns>
    internal static TOM.Table CloneAuxiliary(string name)
    {
        using var source = LoadSource();
        var table = source.Model.Tables[name].Clone();
        foreach (var partition in table.Partitions) partition.QueryGroup = null;
        return table;
    }

    /// <summary>辅助表仅新增固定内嵌定义; 已有同名对象须具备全部准确类型字段, 不替换用户数据源.</summary>
    /// <param name="model">新建过程独占的候选模型.</param>
    /// <param name="source">已核验内嵌资源中的完整辅助表定义.</param>
    /// <param name="roles">仅登记本次新增的辅助表, 不给既有用户对象补生成归属.</param>
    /// <exception cref="AppException">与用户查询冲突或已有同名表字段不兼容时拒绝.</exception>
    private static void AddAuxiliary(TOM.Model model, TOM.Table source, Dictionary<string, string> roles)
    {
        if (model.Expressions.ContainsName(source.Name)
            || model.Tables.Where(table => !table.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(table => table.Partitions).Any(partition => partition.Source is TOM.MPartitionSource
                    && partition.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "样例辅助表与模板已有查询名称冲突, 未发布工程", 2);
        if (model.Tables.Find(source.Name) is { } existing)
        {
            if (source.Columns.Any(column => existing.Columns.Find(column.Name) is not { } actual || actual.DataType != column.DataType))
                throw new AppException("SCAFFOLD_COLUMN_MAPPING", "样例辅助表与模板同名对象不兼容, 未发布工程", 2);
            return;
        }
        var table = source.Clone();
        foreach (var partition in table.Partitions) partition.QueryGroup = null;
        model.Tables.Add(table); roles[table.Name] = "sample-helper";
    }

    /// <summary>使用官方 TMDL 上下文读取内嵌元数据, 不释放额外工程或执行资源中的 M/DAX.</summary>
    /// <returns>由调用者释放的独立资源数据库; 资源缺失或引用不完整时抛出加载错误.</returns>
    private static TOM.Database LoadSource()
    {
        var context = MetadataSerializationContext.Create(MetadataSerializationStyle.Tmdl);
        using var manifest = JsonDocument.Parse(SampleResources.Read("manifest.json"));
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            if (!path.StartsWith(SourceRoot, StringComparison.Ordinal) || !path.EndsWith(".tmdl", StringComparison.Ordinal)) continue;
            using var stream = new MemoryStream(SampleResources.Read(path), false);
            context.ReadFromDocument(path[SourceRoot.Length..^5], stream);
        }
        return context.ToDatabase();
    }
}
