// FilePath    : pbi-utils\src\PbiUtils.Application\Models\ModelSummary.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为不同模型来源共用展示摘要, 保留未读取能力与实际零数量的区别.

namespace PbiUtils.Application.Models;

/// <summary>不含表达式、业务值或连接地址的展示摘要; 未读取的项目保持 null.</summary>
/// <param name="CompatibilityLevel">实际兼容级别; 架构连接或无有效级别时为 null.</param>
/// <param name="TableCount">实际返回的表数量; 未读取时为 null.</param>
/// <param name="MeasureCount">实际返回的度量值数量; 未读取时为 null.</param>
/// <param name="ReportPageCount">已读取报表的页面数量; 在线模型不提供布局时为 null.</param>
public sealed record ModelSummary(int? CompatibilityLevel, int? TableCount, int? MeasureCount, int? ReportPageCount);

/// <summary>纯映射共享的模型展示能力, 不依赖 SDK、文件系统或业务日志.</summary>
public static class ModelSummaryFactory
{
    /// <summary>从已读取的对象计数生成摘要, 不将占位兼容级别伪装成实际能力.</summary>
    /// <param name="compatibilityLevel">适配器返回的兼容级别.</param>
    /// <param name="tableCount">已读取的表数量, 包含隐藏表.</param>
    /// <param name="measureCount">已读取的度量值数量, 包含隐藏度量值.</param>
    /// <param name="reportPageCount">已读取报表布局的页面数量; 未读取时为 null.</param>
    /// <param name="isLimited">是否为不提供兼容级别的架构连接.</param>
    /// <returns>保留实际零计数和未读取状态的展示摘要.</returns>
    public static ModelSummary FromCounts(int compatibilityLevel, int tableCount, int measureCount, int? reportPageCount = null, bool isLimited = false)
        => new(isLimited || compatibilityLevel <= 0 ? null : compatibilityLevel, tableCount, measureCount, reportPageCount);

    /// <summary>从当前权威快照生成摘要; 在线空页面集合表示布局未提供, 不表示零页面.</summary>
    /// <param name="snapshot">宿主实际读取的当前模型或离线 PBIP 快照.</param>
    /// <returns>共用对象计数和来源能力的摘要; 在线页面数量始终为 null.</returns>
    public static ModelSummary FromSnapshot(ProjectSnapshot snapshot)
    {
        var hasReportLayout = snapshot.Connection?.Kind is null or "pbip"
            && !snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal);
        return FromCounts(snapshot.Model.CompatibilityLevel, snapshot.Model.Tables.Count, snapshot.Model.Measures.Count,
            hasReportLayout ? snapshot.Pages.Count : null, snapshot.Connection?.IsLimited == true);
    }
}
