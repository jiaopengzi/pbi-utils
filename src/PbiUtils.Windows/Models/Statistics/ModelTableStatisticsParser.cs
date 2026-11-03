// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Statistics\ModelTableStatisticsParser.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 聚合固定统计行集, 不把缺失、歧义或不完整组成伪装为零或完整存储占用.

using PbiUtils.Application.Models;
using System.Data;
using System.Globalization;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Statistics;

/// <summary>只解析已读取的固定元数据, 不连接模型或修改模型定义.</summary>
public static class ModelTableStatisticsParser
{
    /// <summary>实际查询计数必须完整覆盖本批选择, 非法/重复/缺失或模型外结果明确失败, 不发布部分成功.</summary>
    /// <param name="tables">本批已核验实际表名, 保留用户选择顺序.</param>
    /// <param name="counts">固定别名[__Table]/[__Rows]的完整计数行集.</param>
    /// <param name="token">元数据遍历与返回前检查的取消令牌.</param>
    /// <returns>仅本批表的当前账号查询计数, 不提供大小且不作估计.</returns>
    /// <exception cref="AppException">结果结构或计数不完整/无效, 不把服务失败作为0.</exception>
    public static IReadOnlyList<ModelTableStatistics> FromRowCounts(IReadOnlyList<string> tables, DataTable counts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (counts.Columns.Count != 2 || !Has(counts, "[__Table]", "[__Rows]") || counts.Rows.Count != tables.Count) throw InvalidCounts();
        var selected = tables.ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (DataRow row in counts.Rows)
        {
            token.ThrowIfCancellationRequested();
            var name = Text(row, "[__Table]"); var count = Integer(row, "[__Rows]");
            if (!selected.Contains(name) || count is null || !results.TryAdd(name, count.Value)) throw InvalidCounts();
        }
        return tables.Select(table => new ModelTableStatistics(table, results[table], null, false, true)).ToArray();
    }

    /// <summary>精确计数协议异常采用固定安全提示, 不携带模型值或SDK异常正文.</summary>
    private static AppException InvalidCounts() => new("MODEL_ROW_COUNT_INVALID", "精确行数结果不完整或格式无效", 3);

    /// <summary>架构关键属性成员数作为估计行数, 未返回的统计与存储大小保持未知.</summary>
    /// <param name="tables">当前权威模型实际表名, 不匹配的内部或其他对象不会进入结果.</param>
    /// <param name="dimensions">固定MDSCHEMA_DIMENSIONS行集, 可能未提供.</param>
    /// <param name="token">每张表及元数据遍历时检查取消.</param>
    /// <returns>与原表顺序一致的估计统计, 不从架构推导大小.</returns>
    public static IReadOnlyList<ModelTableStatistics> FromDimensions(IReadOnlyList<string> tables, DataTable? dimensions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var available = Has(dimensions, "DIMENSION_UNIQUE_NAME", "DIMENSION_TYPE", "DIMENSION_CARDINALITY");
        var rows = new Dictionary<string, List<DataRow>>(StringComparer.Ordinal);
        if (available)
        {
            foreach (DataRow row in dimensions!.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (Text(row, "DIMENSION_TYPE") == "2") continue;
                var name = DaxName(Text(row, "DIMENSION_UNIQUE_NAME"));
                if (!rows.TryGetValue(name, out var values)) rows[name] = values = [];
                values.Add(row);
            }
        }
        return tables.Select(table =>
        {
            token.ThrowIfCancellationRequested();
            var found = rows.GetValueOrDefault(table);
            var count = found?.Count == 1 ? Integer(found[0], "DIMENSION_CARDINALITY") : null;
            return new ModelTableStatistics(table, count, null, count is not null);
        }).ToArray();
    }

    /// <summary>聚合基础表行数与完整VertiPaq组成; 任一组成缺失、无效或歧义均不返回部分尺寸.</summary>
    /// <param name="tables">当前权威模型实际表名.</param>
    /// <param name="storage">固定基础表及内部结构行集.</param>
    /// <param name="columns">固定列字典行集.</param>
    /// <param name="segments">固定数据/层次/关系存储段行集.</param>
    /// <param name="token">元数据遍历及结果生成时检查取消.</param>
    /// <returns>与原表顺序一致的存储统计, 不改变模型基线.</returns>
    public static IReadOnlyList<ModelTableStatistics> FromStorage(IReadOnlyList<string> tables, DataTable? storage, DataTable? columns, DataTable? segments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var storageAvailable = Has(storage, "DIMENSION_NAME", "TABLE_ID", "ROWS_COUNT");
        var columnsAvailable = Has(columns, "DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "COLUMN_TYPE", "DICTIONARY_SIZE");
        var segmentsAvailable = Has(segments, "DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "SEGMENT_NUMBER", "TABLE_PARTITION_NUMBER", "USED_SIZE");
        var tableRows = Group(storageAvailable ? storage : null, token);
        var columnRows = Group(columnsAvailable ? columns : null, token);
        var segmentRows = Group(segmentsAvailable ? segments : null, token);
        return tables.Select(table =>
        {
            token.ThrowIfCancellationRequested();
            var count = RowCount(tableRows.GetValueOrDefault(table) ?? [], token);
            var size = columnsAvailable && segmentsAvailable
                ? StorageSize(columnRows.GetValueOrDefault(table) ?? [], segmentRows.GetValueOrDefault(table) ?? [], count, token) : null;
            return new ModelTableStatistics(table, count, size);
        }).ToArray();
    }

    /// <summary>当前存储表行集提供基础表总量, 不含物理分区身份; 仅唯一基础TABLE_ID行可作为整表计数.</summary>
    private static long? RowCount(IReadOnlyList<DataRow> rows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var values = rows.Where(row => !Internal(Text(row, "TABLE_ID"))).ToArray();
        token.ThrowIfCancellationRequested();
        return values.Length == 1 && Text(values[0], "TABLE_ID").Length > 0 ? Integer(values[0], "ROWS_COUNT") : null;
    }

    /// <summary>列字典、数据段、列层次零号段、用户层次和关系结构均有完整读取后才合计.</summary>
    private static long? StorageSize(IReadOnlyList<DataRow> columns, List<DataRow> segments, long? rowCount, CancellationToken token)
    {
        if (columns.Any(row => Text(row, "COLUMN_TYPE") is not ("BASIC_DATA" or "CALCULATED_DATA" or "RELATIONSHIP"
            or "HIERARCHY_POSITION_TO_DATAID" or "HIERARCHY_DATAID_TO_POSITION"))) return null;
        var dataColumns = columns.Where(row => Text(row, "COLUMN_TYPE") == "BASIC_DATA").ToArray();
        if (dataColumns.Length == 0) return null;
        long total = 0;
        var columnKeys = new HashSet<(string, string)>();
        foreach (var row in dataColumns)
        {
            token.ThrowIfCancellationRequested();
            var table = Text(row, "TABLE_ID"); var column = Text(row, "COLUMN_ID");
            var bytes = Integer(row, "DICTIONARY_SIZE");
            if (table.Length == 0 || column.Length == 0 || bytes is null || !columnKeys.Add((table, column)) || !Add(ref total, bytes.Value)) return null;
        }
        // 未识别的计算数据字典可能还有存储占用, 不能忽略它后宣称完整大小.
        if (columns.Any(row => Text(row, "COLUMN_TYPE") == "CALCULATED_DATA" && Integer(row, "DICTIONARY_SIZE") is not 0)) return null;
        var segmentKeys = new HashSet<(string, string, long, long)>();
        foreach (var row in segments)
        {
            token.ThrowIfCancellationRequested();
            var table = Text(row, "TABLE_ID"); var column = Text(row, "COLUMN_ID");
            var number = Integer(row, "SEGMENT_NUMBER"); var partition = Integer(row, "TABLE_PARTITION_NUMBER");
            var bytes = Integer(row, "USED_SIZE");
            if (table.Length == 0 || column.Length == 0 || number is null || partition is null || bytes is null) return null;
            if (Internal(table) && !table.StartsWith("H$", StringComparison.Ordinal) && !table.StartsWith("U$", StringComparison.Ordinal) && !table.StartsWith("R$", StringComparison.Ordinal)) return null;
            if (table.StartsWith("H$", StringComparison.Ordinal) && number != 0) continue;
            if (!segmentKeys.Add((table, column, number.Value, partition.Value)) || !Add(ref total, bytes.Value)) return null;
        }
        return segments.Count > 0 || rowCount == 0 ? total : null;
    }

    /// <summary>一次批量按实际表名分组, 不为每张表重新扫描完整段行集.</summary>
    private static Dictionary<string, List<DataRow>> Group(DataTable? table, CancellationToken token)
    {
        var result = new Dictionary<string, List<DataRow>>(StringComparer.Ordinal);
        if (table is null) return result;
        foreach (DataRow row in table.Rows)
        {
            token.ThrowIfCancellationRequested();
            var name = Text(row, "DIMENSION_NAME");
            if (!result.TryGetValue(name, out var values)) result[name] = values = [];
            values.Add(row);
        }
        return result;
    }

    /// <summary>缺字段不作为空行集, 避免把权限或协议差异伪装成零统计.</summary>
    private static bool Has(DataTable? table, params string[] columns) => table is not null && columns.All(table.Columns.Contains);
    /// <summary>按固定DMV规则识别内部存储结构, 不依赖可见业务列名称.</summary>
    private static bool Internal(string id) => id.Length > 1 && id[1] == '$';
    /// <summary>文化无关文本仅用于元数据键, 数据库空值保持空键.</summary>
    private static string Text(DataRow row, string name) => row[name] is DBNull ? "" : Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? "";
    /// <summary>读取非负完整字节/行数, 不舍入小数或溢出转换.</summary>
    private static long? Integer(DataRow row, string name)
    {
        if (row[name] is DBNull or bool) return null;
        try
        {
            var value = Convert.ToDecimal(row[name], CultureInfo.InvariantCulture);
            return value is >= 0 and <= long.MaxValue && decimal.Truncate(value) == value ? (long)value : null;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return null; }
    }
    /// <summary>有界累加, 超过64位范围时拒绝生成部分统计.</summary>
    private static bool Add(ref long total, long value)
    {
        if (value > long.MaxValue - total) return false;
        total += value; return true;
    }
    /// <summary>架构唯一名只解外层方括号和标准转义, 不改变实际名称.</summary>
    private static string DaxName(string name) => name.StartsWith('[') && name.EndsWith(']') ? name[1..^1].Replace("]]", "]", StringComparison.Ordinal) : name;
}
