// FilePath    : pbi-utils\src\PbiUtils.Application\Models\ModelTableStatistics.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义独立于模型定义指纹的只读表统计, 未读取值保持空值而非伪造零.

namespace PbiUtils.Application.Models;

/// <summary>单表实际已提供的统计, 存储占用为 VertiPaq 字节而非导出文件大小.</summary>
/// <param name="Table">权威模型中的实际表名.</param>
/// <param name="RowCount">已读取行数; 未提供或无法唯一确定时为 null.</param>
/// <param name="SizeBytes">完整存储结构占用字节; 任一组成未提供时为 null.</param>
/// <param name="IsEstimate">行数来自架构关键属性成员数等估计来源.</param>
/// <param name="IsQueryCount">行数来自当前账号安全上下文的实际COUNTROWS查询, 不代表全模型物理存储行数.</param>
/// <param name="RowCountUnavailableReason">精确计数不适用的机器原因; no_queryable_columns 表示只有内部行号或没有数据列, 不等同于真实0行.</param>
public sealed record ModelTableStatistics(string Table, long? RowCount, long? SizeBytes, bool IsEstimate = false, bool IsQueryCount = false, string? RowCountUnavailableReason = null);

/// <summary>绑定当前身份与模型定义基线的附属统计, 不参与定义序列化或写入授权.</summary>
/// <param name="ProjectId">本次统计所属的权威模型身份.</param>
/// <param name="BaselineId">统计开始及结束核验一致的定义或可见架构基线.</param>
/// <param name="Tables">当前操作范围内表的统计, 元数据读取为全部表, 精确查询仅为所选表; 未提供字段保留 null.</param>
/// <param name="IsSimulation">是否为明确的界面模拟结果, 真实平台读取始终为 false.</param>
public sealed record ModelTableStatisticsResult(string ProjectId, string BaselineId, IReadOnlyList<ModelTableStatistics> Tables, bool IsSimulation = false);

/// <summary>只读取宿主已绑定目标的固定统计元数据, 不执行业务计数查询或刷新数据源.</summary>
public interface IModelTableStatisticsReader
{
    /// <summary>在查询前后核对同一模型及基线, 返回独立统计; 取消、身份变化或认证失败须明确失败.</summary>
    /// <param name="snapshot">宿主当前已核验的模型快照, 不能由网页构造目标.</param>
    /// <param name="token">同步 SDK 边界和元数据遍历时检查的取消令牌.</param>
    /// <returns>实际读取到的表统计; 未提供值为 null, 不宣称完整模型定义已读取.</returns>
    Task<ModelTableStatisticsResult> ReadStatisticsAsync(ProjectSnapshot snapshot, CancellationToken token);
}

/// <summary>仅对用户显式选中的表读取当前账号可见精确行数, 不刷新数据源或更改模型.</summary>
public interface IModelTableRowCountReader
{
    /// <summary>前后核验同一目标和基线, 全批查询成功后才返回所选表计数, 不发布部分结果.</summary>
    /// <param name="snapshot">宿主当前权威快照, 不由网页指定连接或身份.</param>
    /// <param name="tables">1至2000个当前模型内唯一实际表名, 保留选择顺序.</param>
    /// <param name="token">固定查询的同步SDK边界和结果遍历取消令牌.</param>
    /// <returns>按原选择顺序返回结果; 可查询表标记IsQueryCount=true, 无可查询列的表以null计数和明确原因表示不适用, 不伪造0.</returns>
    Task<ModelTableStatisticsResult> ReadRowCountsAsync(ProjectSnapshot snapshot, IReadOnlyList<string> tables, CancellationToken token);
}
