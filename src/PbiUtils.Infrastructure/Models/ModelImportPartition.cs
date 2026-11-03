// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\ModelImportPartition.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对明确的单一 M 导入分区设置表达式及相同类型元数据, 不提交或保存模型.

using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Models;

/// <summary>创建与持续配置共用的 TOM 分区适配, 事务仍由原调用方拥有.</summary>
internal static class ModelImportPartition
{
    /// <summary>只更换导入分区, 原计算表分区不进入此入口.</summary>
    /// <param name="table">本轮拥有的实际目标表, 不克隆或提交 SDK 对象.</param>
    /// <param name="expression">已由调用方生成和验证的 M 表达式.</param>
    /// <param name="nativeDate">日期生成器明确需要 Date 元数据时为 true, 普通 CSV 导入为 false.</param>
    /// <exception cref="AppException">现有分区不是唯一 M 导入分区时拒绝替换.</exception>
    internal static void Set(TOM.Table table, string expression, bool nativeDate = false)
    {
        if (table.Partitions.Count == 0) table.Partitions.Add(new TOM.Partition { Name = table.Name, Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = expression } });
        else
        {
            if (table.Partitions.Count != 1 || table.Partitions[0].Source is not TOM.MPartitionSource) throw new AppException("SCAFFOLD_PARTITION_MAPPING", "只支持明确的单一导入分区映射", 2);
            ((TOM.MPartitionSource)table.Partitions[0].Source).Expression = expression;
        }
        if (!nativeDate) TomModelMetadata.ImportedDateMetadata(table);
        else foreach (var column in table.Columns.OfType<TOM.DataColumn>().Where(column => column.DataType == TOM.DataType.DateTime))
        {
            if (column.Annotations.Find("UnderlyingDateTimeDataType") is { } old) column.Annotations.Remove(old);
            column.Annotations.Add(new TOM.Annotation { Name = "UnderlyingDateTimeDataType", Value = "Date" });
        }
    }
}
