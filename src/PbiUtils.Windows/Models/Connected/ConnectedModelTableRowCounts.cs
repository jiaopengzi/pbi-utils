// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ConnectedModelTableRowCounts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅对用户显式所选表按当前账号安全上下文批量COUNTROWS, 全批成功及身份复核后才返回.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader
{
    /// <summary>精确计数只在明确请求后执行, 当前账号RLS/OLS保持, 不刷新、提权或返回业务行.</summary>
    /// <param name="snapshot">共享模型门闩内取得的权威当前快照.</param>
    /// <param name="tables">1至2000个当前模型唯一实际表名, 不接受查询/连接或身份.</param>
    /// <param name="token">整个操作的原取消预算, SDK每批最多30秒, 内部调用不承诺即时中断.</param>
    /// <returns>仅所选表的实际查询行数或无可查询列的不适用原因, 所有批次及前后核验完成后才返回.</returns>
    /// <exception cref="AppException">来源、选择、身份、协议、权限、认证或计数失败; 无部分成功.</exception>
    public Task<ModelTableStatisticsResult> ReadRowCountsAsync(ProjectSnapshot snapshot, IReadOnlyList<string> tables, CancellationToken token) =>
        BusinessLog.RunAsync<ModelTableStatisticsResult>(log, "ConnectedModelReader.TableRowCounts", async () =>
        {
            token.ThrowIfCancellationRequested();
            var selected = ValidateRowCountSelection(snapshot, tables);
            if (!snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal))
                throw new AppException("MODEL_ROW_COUNT_UNSUPPORTED", "仅支持已连接模型的精确行数统计", 3);
            // 即使全部表均不适用, 外层仍完成同一连接与完整基线的前后核验; 不走绕过身份的空结果捷径.
            return await ReadBoundStatisticsAsync(snapshot,
                (session, _, cancellation) => ReadSelectedRowCounts(snapshot, selected, session, cancellation), true, token);
        });

    /// <summary>仅完整元数据可证明表无可查询列; 架构连接缺失字段仍须执行实际计数, 不把未查询解释为真实0行.</summary>
    /// <param name="snapshot">已由外层核验的完整或有限架构快照.</param>
    /// <param name="selected">已冻结并验证的有序表选择.</param>
    /// <param name="session">外层持有生命周期的当前账号统计会话.</param>
    /// <param name="token">每批与遍历边界的取消令牌.</param>
    /// <returns>原选择顺序的精确值或已确证的不适用原因; 查询失败不返回部分结果.</returns>
    internal static IReadOnlyList<ModelTableStatistics> ReadSelectedRowCounts(ProjectSnapshot snapshot, string[] selected,
        IPowerBiTableStatisticsSession session, CancellationToken token)
    {
        // RowNumber 是引擎内部列; IsLimited 的空字段也可能来自架构权限限制, 不能据此判定纯度量表.
        var queryable = snapshot.Model.Tables.Where(table => snapshot.Connection?.IsLimited == true
            || table.Columns.Any(column => column.Kind != "RowNumber")).Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
        var result = selected.Where(name => !queryable.Contains(name)).ToDictionary(name => name,
            name => new ModelTableStatistics(name, null, null, RowCountUnavailableReason: "no_queryable_columns"), StringComparer.Ordinal);
        foreach (var batch in selected.Where(queryable.Contains).Chunk(50))
        {
            token.ThrowIfCancellationRequested();
            using var rows = session.ReadRowCounts(batch, token);
            token.ThrowIfCancellationRequested();
            foreach (var count in ModelTableStatisticsParser.FromRowCounts(batch, rows, token)) result.Add(count.Table, count);
        }
        token.ThrowIfCancellationRequested();
        return selected.Select(name => result[name]).ToArray();
    }

    /// <summary>在任何外部读取前冻结并核验完整选择, 不默默去重、改大小写或接收模型外名称.</summary>
    /// <param name="snapshot">当前权威模型表集合.</param>
    /// <param name="tables">当前操作选择的实际表名.</param>
    /// <returns>不受调用方后续修改影响的有序选择副本.</returns>
    /// <exception cref="AppException">空、重复、过多、格式错误或在当前模型中无法唯一定位.</exception>
    private static string[] ValidateRowCountSelection(ProjectSnapshot snapshot, IReadOnlyList<string> tables)
    {
        if (tables is null || tables.Count is < 1 or > 2000) throw InvalidRowCountSelection();
        var selected = tables.ToArray();
        if (selected.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\0'))
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length) throw InvalidRowCountSelection();
        var model = snapshot.Model.Tables.GroupBy(table => table.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (selected.Any(name => model.GetValueOrDefault(name) != 1)) throw InvalidRowCountSelection();
        return selected;
    }

    /// <summary>选择错误只返回固定安全提示, 不包含用户模型或名称.</summary>
    private static AppException InvalidRowCountSelection() => new("VALIDATION_ERROR", "精确行数统计参数无效");
}
