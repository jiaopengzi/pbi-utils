// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\Model\ScaffoldRetailMapping.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 零售样例身份与组织身份分离, 精确调整关系键并保留原关系对象.

using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.SampleData.Model;

/// <summary>锁清单记录明确的原列, 新列与关系身份.</summary>
internal sealed record ScaffoldRelationshipMapping(string Relationship, string FromTable, string OriginalFromColumn, string FromColumn,
    string ToTable, string OriginalToColumn, string ToColumn);

/// <summary>只对已知内置零售副本和明确选择的样例执行.</summary>
internal static class ScaffoldRetailMapping
{
    /// <summary>原组织字段继续为空, 样例地区和门店使用其自身稳定ID参与业务关系.</summary>
    internal static IReadOnlyList<ScaffoldRelationshipMapping> Apply(TOM.Model model)
    {
        foreach (var table in new[] { "D20_大区表", "D21_省份表" }) Add(model.Tables[table], "PbiUtilsRegionId");
        foreach (var table in new[] { "T10_门店表", "T11_门店表_租赁", "T12_门店表_装修", "T30_入库信息表", "T40_订单主表" }) Add(model.Tables[table], "PbiUtilsStoreId");
        var result = new List<ScaffoldRelationshipMapping>();
        foreach (var relation in model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            string? column = relation.ToTable.Name switch
            {
                "D20_大区表" when relation.ToColumn.Name == "大区组织ID" && relation.FromTable.Name == "D21_省份表" => "PbiUtilsRegionId",
                "T10_门店表" when relation.ToColumn.Name == "门店组织ID" && relation.FromTable.Name is "T30_入库信息表" or "T40_订单主表" or "T11_门店表_租赁" or "T12_门店表_装修" => "PbiUtilsStoreId",
                _ => null,
            };
            if (column is null) continue;
            result.Add(new(relation.Name, relation.FromTable.Name, relation.FromColumn.Name, column, relation.ToTable.Name, relation.ToColumn.Name, column));
            relation.FromColumn = relation.FromTable.Columns[column];
            relation.ToColumn = relation.ToTable.Columns[column];
        }
        if (result.Count != 5) throw new AppException("SCAFFOLD_RELATIONSHIP_MAPPING", "零售模板业务关系与固定适配不一致", 2);
        return result;
    }
    /// <summary>新增身份列不隐藏来源事实, 不复用任何原组织字段.</summary>
    private static void Add(TOM.Table table, string name)
    {
        if (table.Columns.ContainsName(name)) throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "样例身份列与模板对象冲突", 2);
        table.Columns.Add(new TOM.DataColumn
        {
            Name = name,
            SourceColumn = name,
            DataType = TOM.DataType.Int64,
            IsHidden = true,
            Description = "pbi-utils 示例身份, 不代表企业组织ID",
            SummarizeBy = TOM.AggregateFunction.None
        });
    }
}
