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
/// <param name="IsQueryCount">行数来自当前账号安全上下文的实际COUNTROWS查询, 不代表全模型物理存储行数.</param>
/// <param name="RowCountUnavailableReason">精确计数不适用的机器原因; no_queryable_columns 表示只有内部行号或没有数据列, 不等同于真实0行.</param>
public sealed record ModelTableStatistics(string Table, long? RowCount, long? SizeBytes, bool IsQueryCount = false, string? RowCountUnavailableReason = null);

/// <summary>绑定当前身份与模型定义基线的附属统计, 不参与定义序列化或写入授权.</summary>
/// <param name="ProjectId">本次统计所属的权威模型身份.</param>
/// <param name="BaselineId">统计开始及结束核验一致的定义或可见架构基线.</param>
/// <param name="Tables">当前模型表的统计, 未提供字段保留 null.</param>
/// <param name="IsSimulation">是否为明确的界面模拟结果, 真实平台读取始终为 false.</param>
public sealed record ModelTableStatisticsResult(string ProjectId, string BaselineId, IReadOnlyList<ModelTableStatistics> Tables, bool IsSimulation = false);

/// <summary>读取宿主已绑定目标的固定存储统计, Service 使用当前账号查询实际行数.</summary>
public interface IModelTableStatisticsReader
{
    /// <summary>在查询前后核对同一模型及基线, 返回独立统计; 取消、身份变化或认证失败须明确失败.</summary>
    /// <param name="snapshot">宿主当前已核验的模型快照, 不能由网页构造目标.</param>
    /// <param name="token">同步 SDK 边界、计数批次和元数据遍历时检查的取消令牌.</param>
    /// <returns>实际读取到的表统计; 未提供值为 null, 不宣称完整模型定义已读取.</returns>
    Task<ModelTableStatisticsResult> ReadStatisticsAsync(ProjectSnapshot snapshot, CancellationToken token);
}
