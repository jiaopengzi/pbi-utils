// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportProgressTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实消费行驱动的多表进度、节流以及失败取消时不越过复核和发布边界.

using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelTableExportProgressTests
{
    private static readonly string[] ProgressStages = ["reading", "verifying", "finalizing", "publishing"];

    /// <summary>空表与后续数据表都产生实际进度, 完成表数必须在对应权威回读成功后增加.</summary>
    [Fact]
    public async Task ProgressCountsVerifiedTablesAndActualRowsWithoutInventingCompletion()
    {
        var source = new Source([("Empty", 0), ("Data", 3)]);
        var updates = new List<ModelTableExportProgress>();
        var result = await source.Export(value =>
        {
            Assert.InRange(value.CompletedTables, 0, source.SuccessfulVerifications);
            updates.Add(value);
        });
        Assert.Equal(3, result.RowCount);
        Assert.True(source.Committed);
        Assert.Contains(updates, item => item is { Stage: "reading", TableIndex: 1, RowsRead: 0, CompletedTables: 0, TotalTables: 2 });
        Assert.Contains(updates, item => item is { Stage: "reading", TableIndex: 2, RowsRead: 0, CompletedTables: 1 });
        Assert.Contains(updates, item => item is { Stage: "verifying", TableIndex: 2, RowsRead: 3, CompletedTables: 1 });
        Assert.Equal("publishing", updates[^1].Stage);
        Assert.Equal(2, updates[^1].CompletedTables);
        Assert.Equal(3, updates[^1].RowsRead);
        Assert.All(updates, item => Assert.Contains(item.Stage, ProgressStages));
    }

    /// <summary>每行推进受控时钟, 长单表必须连续反馈且消息数量不随每行线性增加.</summary>
    [Fact]
    public async Task LongSingleTableIsThrottledWithoutLosingRows()
    {
        var source = new Source([("Large", 10_000)]) { MillisecondsPerRead = 1 };
        var updates = new List<ModelTableExportProgress>();
        var result = await source.Export(updates.Add);
        var reading = updates.Where(item => item.Stage == "reading").ToArray();
        Assert.Equal(10_000, result.RowCount);
        Assert.Equal(10_001, source.ReadCalls);
        Assert.InRange(reading.Length, 30, 42);
        Assert.Equal(0, reading[0].RowsRead);
        Assert.True(reading[^1].RowsRead >= 9_750);
        Assert.True(reading.Zip(reading.Skip(1), (left, right) => left.RowsRead < right.RowsRead).All(value => value));
        Assert.Equal(10_000, updates.Last(item => item.Stage == "verifying").RowsRead);
    }

    /// <summary>复核失败不能把已读取表误报为已完成, 更不能进入格式完成或发布阶段.</summary>
    [Fact]
    public async Task FailedIdentityVerificationNeverCompletesTheTable()
    {
        var source = new Source([("Data", 4)]) { ChangeBaselineAfterData = true };
        var updates = new List<ModelTableExportProgress>();
        var error = await Assert.ThrowsAsync<AppException>(() => source.Export(updates.Add));
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.All(updates, item => Assert.Equal(0, item.CompletedTables));
        Assert.DoesNotContain(updates, item => item.Stage is "finalizing" or "publishing");
        Assert.False(source.Committed);
        Assert.True(source.Disposed);
    }

    /// <summary>读取进度期间取消仍等待原调用退出, 不发送后续阶段或发布文件.</summary>
    [Fact]
    public async Task CancellationDuringReadingStopsFurtherProgressAndPublication()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source([("Data", 10_000)]) { MillisecondsPerRead = 1 };
        var updates = new List<ModelTableExportProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.Export(value =>
        {
            updates.Add(value);
            if (value.Stage == "reading" && value.RowsRead > 0) cancellation.Cancel();
        }, cancellation.Token));
        Assert.Equal("reading", updates[^1].Stage);
        Assert.InRange(source.ReadCalls, 1, 300);
        Assert.False(source.Committed);
        Assert.True(source.Disposed);
    }

    /// <summary>若SDK在返回权威快照前才收到取消, 不把已取消的复核计入完成表数.</summary>
    [Fact]
    public async Task CancellationAtVerificationReturnCannotAdvanceCompletedTables()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source([("Data", 2)]) { DuringFirstVerification = cancellation.Cancel };
        var updates = new List<ModelTableExportProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.Export(updates.Add, cancellation.Token));
        Assert.All(updates, item => Assert.Equal(0, item.CompletedTables));
        Assert.DoesNotContain(updates, item => item.Stage is "finalizing" or "publishing");
        Assert.False(source.Committed);
    }

    /// <summary>表写完后格式或发布仍可能失败, 进度事件本身绝不宣告终态成功.</summary>
    /// <param name="stage">需要注入失败的实际平台阶段.</param>
    [Theory]
    [InlineData("complete")]
    [InlineData("commit")]
    public async Task FileFailureDoesNotInventSuccessfulTerminalProgress(string stage)
    {
        var source = new Source([("Data", 2)]) { FailureStage = stage };
        var updates = new List<ModelTableExportProgress>();
        await Assert.ThrowsAsync<AppException>(() => source.Export(updates.Add));
        Assert.Equal(stage == "complete" ? "finalizing" : "publishing", updates[^1].Stage);
        Assert.False(source.Committed);
        Assert.True(source.Disposed);
    }

    /// <summary>抽样服务若超量返回, 拒绝行不计入实际读取进度且整轮不发布.</summary>
    [Fact]
    public async Task RejectedExcessSampleRowIsNotReportedAsRead()
    {
        var source = new Source([("Data", 2)]) { MillisecondsPerRead = 250, RowLimit = 1 };
        var updates = new List<ModelTableExportProgress>();
        var error = await Assert.ThrowsAsync<AppException>(() => source.Export(updates.Add));
        Assert.Equal("EXPORT_ROW_LIMIT_EXCEEDED", error.Code);
        Assert.All(updates, item => Assert.InRange(item.RowsRead, 0, 1));
        Assert.DoesNotContain(updates, item => item.Stage == "publishing");
        Assert.False(source.Committed);
    }

    /// <summary>共享用例替身仅替换引擎和文件, 消费游标与计数仍走正式编排.</summary>
    private sealed class Source((string Name, int Rows)[] tables) : IProjectReader, IModelTableDataReader, IModelTableFileWriter, IModelTableExportFile
    {
        private readonly ProgressClock clock = new();
        private bool changed;
        private int snapshotReads;
        internal int ReadCalls { get; private set; }
        internal int SuccessfulVerifications { get; private set; }
        internal int MillisecondsPerRead { get; init; }
        internal int? RowLimit { get; init; }
        internal bool ChangeBaselineAfterData { get; init; }
        internal string? FailureStage { get; init; }
        internal Action? DuringFirstVerification { get; init; }
        internal bool Committed { get; private set; }
        internal bool Disposed { get; private set; }
        public string FileName => "progress.xlsx";
        private ProjectSnapshot Snapshot => new("model://progress", "project", changed ? "changed" : "baseline", "TOM", "", "", null,
            new("model", 1606, "en-US", tables.Select(table => new TableInfo(table.Name, null, false, [new("Value", "Data", "String", false, null)], [])).ToArray(), [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        /// <summary>冻结固定请求并使用受控时钟运行实际共享导出用例.</summary>
        internal Task<ModelTableExportResult> Export(Action<ModelTableExportProgress> progress, CancellationToken token = default)
            => new ModelTableExportService(this, this, this, clock).ExportAsync("model://progress", new("project", "baseline", tables.Select(table => table.Name).ToArray(), "xlsx", RowLimit: RowLimit), "selected.xlsx", token, progress: progress);
        /// <summary>权威回读采用当前基线, 在成功回读边界记录验证次数.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++snapshotReads == 2) DuringFirstVerification?.Invoke();
            if (snapshotReads > 1 && !changed) SuccessfulVerifications++;
            return Task.FromResult(Snapshot);
        }
        /// <summary>模型返回原始行数, 绝不在夹具内按RowLimit截断以掩盖产品超量守卫.</summary>
        public Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            var rows = consume(new Rows(tables.Single(item => item.Name == table).Rows, () => { ReadCalls++; clock.Advance(MillisecondsPerRead); }), token);
            if (ChangeBaselineAfterData) changed = true;
            return Task.FromResult(rows);
        }
        /// <summary>返回本轮私有文件替身, 不操作磁盘.</summary>
        public Task<IModelTableExportFile> CreateAsync(string destination, ModelTableExportRequest request, CancellationToken token) => Task.FromResult<IModelTableExportFile>(this);
        /// <summary>真正遍历产品传入游标, 确认进度包装不会漏行或额外读数据.</summary>
        public long Write(string table, IModelTableRows rows, CancellationToken token)
        {
            long count = 0;
            while (rows.Read()) { token.ThrowIfCancellationRequested(); Assert.Equal("duplicate", rows.GetValue(0)); count++; }
            return count;
        }
        /// <summary>模拟格式结尾失败, 该阶段仍未发布.</summary>
        public void Complete(CancellationToken token) { token.ThrowIfCancellationRequested(); if (FailureStage == "complete") throw new AppException("EXPORT_FILE_FAILED", "file failed"); }
        /// <summary>保持正式提交裁决边界, 后续故障不变成成功.</summary>
        public Task CommitAsync(Action? beginCommit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); beginCommit?.Invoke();
            if (FailureStage == "commit") throw new AppException("EXPORT_FILE_FAILED", "file failed");
            Committed = true;
            return Task.CompletedTask;
        }
        /// <summary>记录释放事实, 失败与取消均应到达.</summary>
        public void Dispose() => Disposed = true;
    }

    /// <summary>允许每次源Read推进时间, 不使用不稳定的墙上等待验证节流.</summary>
    private sealed class ProgressClock : TimeProvider
    {
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        /// <summary>读取单调递增的毫秒时间.</summary>
        public override long GetTimestamp() => milliseconds;
        /// <summary>按源读取成本推进时钟.</summary>
        internal void Advance(int duration) => milliseconds += duration;
    }

    /// <summary>固定重复值游标保留源出现次数, 回调仅用于推进受控读取成本.</summary>
    private sealed class Rows(int count, Action onRead) : IModelTableRows
    {
        private int index;
        public IReadOnlyList<string> Columns => ["Value"];
        /// <summary>移动原始结果并记录一次真实Read调用.</summary>
        public bool Read() { onRead(); return ++index <= count; }
        /// <summary>返回当前原始重复值, 不做类型或数量变化.</summary>
        public object? GetValue(int ordinal) => "duplicate";
    }
}
