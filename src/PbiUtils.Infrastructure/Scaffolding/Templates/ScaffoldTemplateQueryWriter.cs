// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\Templates\ScaffoldTemplateQueryWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在新建 staging 写入选中的固定辅助表及 M 查询, 保留模板引用并拒绝用户命名冲突.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;

using PbiUtils.Infrastructure.Models;

namespace PbiUtils.Infrastructure.Scaffolding.Templates;

/// <summary>只更改调用方拥有的候选模型; 不执行 M、不新增现有模型写入通道.</summary>
internal static class ScaffoldTemplateQueryWriter
{
    /// <summary>固定 Hour24/MQHY/RefreshTime 启用加载, TypeDetails 保留为查询; 对未知用户同名对象整批拒绝.</summary>
    /// <param name="model">新建工作区的完整私有模型.</param>
    /// <param name="selected">固定目录 ID; null 或空数组保留原模板.</param>
    /// <param name="builtin">经模板目录和基线核验的内置来源, 不由网页声明.</param>
    /// <param name="groups">已校验的分组覆盖; 缺少键沿用目录, 空字符串明确不分组, 不更改未选中对象.</param>
    internal static void Apply(TOM.Model model, string[]? selected, bool builtin, IReadOnlyDictionary<string, string>? groups = null)
    {
        if (selected is not { Length: > 0 }) return;
        var queries = ScaffoldTemplateQueries.All.Where(query => selected.Contains(query.Id, StringComparer.Ordinal)).ToArray();
        foreach (var query in queries)
        {
            var prior = model.Expressions.Find(query.Id);
            if (model.Tables.ContainsName(query.Id)
                || model.Tables.SelectMany(table => table.Partitions).Any(partition => partition.Source is TOM.MPartitionSource && partition.Name.Equals(query.Id, StringComparison.OrdinalIgnoreCase))
                || prior is not null && !(builtin && prior.Kind == TOM.ExpressionKind.M))
                throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "可选 M 查询与模板现有表或查询名称冲突", 2);
        }
        var queryOrder = AppendQueryOrder(model, queries);
        foreach (var query in queries)
        {
            var folder = groups?.GetValueOrDefault(query.Id) ?? query.QueryGroup;
            TOM.QueryGroup? group = null;
            if (folder.Length > 0)
            {
                group = model.QueryGroups.Find(folder);
                if (group is null) { group = new TOM.QueryGroup { Folder = folder }; model.QueryGroups.Add(group); }
            }
            var expression = model.Expressions.Find(query.Id);
            if (query.Id is "Hour24" or "MQHY" or "RefreshTime")
            {
                // 列、类型、格式和排序复用正式内嵌样例定义; M 原文仍取宿主与预览共用的固定目录.
                var table = ScaffoldSampleModel.CloneAuxiliary(query.Id);
                table.ExcludeFromModelRefresh = false;
                table.Partitions.Clear();
                ModelImportPartition.Set(table, query.Expression);
                table.Partitions[0].QueryGroup = group;
                if (expression is not null) model.Expressions.Remove(expression);
                model.Tables.Add(table);
                ModelConfigurationOwnership.Mark(table, table.Annotations);
                continue;
            }
            if (expression is null)
            {
                expression = new TOM.NamedExpression
                {
                    Name = query.Id,
                    Kind = TOM.ExpressionKind.M,
                    LineageTag = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("pbi-utils/template-query/v1/" + query.Id)).AsSpan(0, 16)).ToString()
                };
                model.Expressions.Add(expression);
            }
            expression.Expression = query.Expression;
            expression.QueryGroup = group;
            if (expression.Annotations.Find("PBI_ResultType") is { } resultType) resultType.Value = "Table";
            else expression.Annotations.Add(new TOM.Annotation { Name = "PBI_ResultType", Value = "Table" });
        }
        if (model.Annotations.Find("PBI_QueryOrder") is { } annotation) annotation.Value = queryOrder;
        else model.Annotations.Add(new TOM.Annotation { Name = "PBI_QueryOrder", Value = queryOrder });
    }

    /// <summary>在写模型前核验原顺序并生成追加结果, 保留用户成员; 损坏元数据在修改候选前拒绝.</summary>
    /// <param name="model">尚未更改的私有候选模型.</param>
    /// <param name="queries">本次已通过命名冲突检查的固定查询.</param>
    /// <returns>保留原成员的查询顺序 JSON; 损坏原值抛出稳定冲突错误.</returns>
    private static string AppendQueryOrder(TOM.Model model, ScaffoldTemplateQuery[] queries)
    {
        var annotation = model.Annotations.Find("PBI_QueryOrder");
        List<string> order;
        try { order = annotation is null ? [] : JsonSerializer.Deserialize<List<string>>(annotation.Value) ?? throw new JsonException(); }
        catch (JsonException) { throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "模板查询顺序元数据无效, 未覆盖", 2); }
        foreach (var query in queries)
            if (!order.Contains(query.Id, StringComparer.OrdinalIgnoreCase)) order.Add(query.Id);
        return JsonSerializer.Serialize(order);
    }
}
