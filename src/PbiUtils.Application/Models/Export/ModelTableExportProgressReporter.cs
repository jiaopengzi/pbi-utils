// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Export\ModelTableExportProgressReporter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 同步记录真实游标读取和表复核阶段, 对行进度节流而不改变消费或发布语义.

using PbiUtils.Core;

namespace PbiUtils.Application.Models.Export;

/// <summary>单次串行导出的计数器, 通知在原执行线程同步有序调用, 不建立异步进度队列.</summary>
/// <param name="report">宿主提供的同步通知, null时不增加逐行包装成本.</param>
/// <param name="totalTables">请求中冻结的所选表总数.</param>
/// <param name="clock">单调时钟, 仅用于限制通知频率, 不推断任务完成度.</param>
internal sealed class ModelTableExportProgressReporter(Action<ModelTableExportProgress>? report, int totalTables, TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private int tableIndex;
    private int completedTables;
    private long rowsRead;
    private long reportedAt;

    /// <summary>开始下一张实际表并立即通知0行, 不在模型返回第一行前伪造读取数量.</summary>
    internal void StartTable()
    {
        tableIndex++;
        rowsRead = 0;
        reportedAt = clock.GetTimestamp();
        Stage("reading");
    }

    /// <summary>仅在调用方需要进度时包装原游标, 原始类型和值及取消行为保持不变.</summary>
    /// <param name="rows">已经应用抽样超量守卫的原游标.</param>
    /// <param name="token">读取前后检查的原取消令牌.</param>
    /// <returns>原游标或轻量同步计数包装, 生命周期仍由数据适配器持有.</returns>
    internal IModelTableRows Observe(IModelTableRows rows, CancellationToken token) => report is null ? rows : new ObservedRows(rows, this, token);

    /// <summary>表数据读取完成后报告精确实际数量, 权威复核成功之前不增加完成表数.</summary>
    internal void Verifying() => Stage("verifying");

    /// <summary>仅在权威身份复核成功后增加完成表数, 不代表容器或文件已经发布.</summary>
    internal void Verified()
    {
        completedTables++;
        Stage("verifying");
    }

    /// <summary>同步发布固定阶段事实, 不使用Progress队列造成跨阶段迟到或乱序.</summary>
    /// <param name="stage">编排器选定的固定安全阶段.</param>
    internal void Stage(string stage) => report?.Invoke(new(stage, completedTables, totalTables, tableIndex, rowsRead));

    /// <summary>每次实际成功Read加一, 最多每250ms通知一次, 表尾由阶段事件补齐不丢失末行数.</summary>
    private void ReadRow()
    {
        rowsRead = checked(rowsRead + 1);
        var now = clock.GetTimestamp();
        if (clock.GetElapsedTime(reportedAt, now) < Interval) return;
        reportedAt = now;
        Stage("reading");
    }

    /// <summary>仅计数源游标成功返回的行, 原始列与标量完全透传, 不额外读取或持有SDK资源.</summary>
    private sealed class ObservedRows(IModelTableRows source, ModelTableExportProgressReporter owner, CancellationToken token) : IModelTableRows
    {
        public IReadOnlyList<string> Columns => source.Columns;
        /// <summary>源读取或抽样守卫失败不计数, 取消在回调后再复核以阻止继续消费当前行.</summary>
        public bool Read()
        {
            token.ThrowIfCancellationRequested();
            var available = source.Read();
            token.ThrowIfCancellationRequested();
            if (available) owner.ReadRow();
            token.ThrowIfCancellationRequested();
            return available;
        }
        /// <summary>原样取得当前行指定列, 任何SDK错误沿原调用栈返回.</summary>
        public object? GetValue(int ordinal) => source.GetValue(ordinal);
    }
}
