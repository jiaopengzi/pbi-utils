// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelTableRowCountsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证Service 自动表计数的固定批次、当前账号语义、目标身份、失败与取消排空.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Windows.Models.Connected;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed partial class ModelTableStatisticsTests
{
    /// <summary>架构连接未返回字段不证明表无可查询列, 必须保留真实COUNTROWS及其当前账号结果.</summary>
    [Fact]
    public async Task LimitedSchemaWithoutColumnsStillQueriesRowCounts()
    {
        var schema = new Schema { Tables = ["Sales"], ColumnlessTables = ["Sales"] };
        var session = new Session { Count = _ => 60L };
        var (reader, snapshot) = await Connect(schema, new(), session);
        try
        {
            Assert.True(snapshot.Connection!.IsLimited);
            Assert.Empty(Assert.Single(snapshot.Model.Tables).Columns);
            var result = await reader.ReadStatisticsAsync(snapshot, default);
            var count = Assert.Single(result.Tables);
            Assert.Equal(60L, count.RowCount);
            Assert.True(count.IsQueryCount);
            Assert.Null(count.RowCountUnavailableReason);
            Assert.Equal(["Sales"], Assert.Single(session.CountBatches));
            Assert.Equal(3, schema.OpenCount);
            Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>纯度量表没有可查询列时明确不适用, 其它表仍读取精确值并保持模型顺序.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnlessTablesDoNotBreakServiceStatistics(bool rowNumberOnly)
    {
        var schema = new Schema { Tables = ["Sales", "Measures", "Empty"], ColumnlessTables = ["Measures"] };
        using var session = new Session { Count = name => name == "Measures" ? throw new InvalidOperationException("No queryable columns") : name == "Empty" ? 0L : 60L };
        var (reader, snapshot) = await Connect(schema, new(), session);
        if (rowNumberOnly)
            snapshot = snapshot with { Model = snapshot.Model with { Tables = snapshot.Model.Tables.Select(table => table.Name == "Measures" ? table with { Columns = [new("RowNumber-internal", "RowNumber", "Int64", true, null)] } : table).ToArray() } };
        try
        {
            // 此消费边界使用完整结构; Service 注册仅提供测试快照, 不将有限架构伪装为完整引擎验收.
            var complete = snapshot with { Connection = snapshot.Connection! with { IsLimited = false } };
            var result = ConnectedModelReader.ReadServiceStatistics(complete, [], session, default);
            Assert.Equal(["Sales", "Measures", "Empty"], result.Select(table => table.Table));
            Assert.Equal([60L, null, 0L], result.Select(table => table.RowCount));
            Assert.False(result[1].IsQueryCount);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(result[1], System.Text.Json.JsonSerializerOptions.Web);
            Assert.Equal("no_queryable_columns", json.GetProperty("rowCountUnavailableReason").GetString());
            Assert.Equal(["Sales", "Empty"], Assert.Single(session.CountBatches));
            session.CountBatches.Clear();
            var measuresOnly = complete with { Model = complete.Model with { Tables = [complete.Model.Tables.Single(table => table.Name == "Measures")] } };
            var only = Assert.Single(ConnectedModelReader.ReadServiceStatistics(measuresOnly, [], session, default));
            Assert.Null(only.RowCount);
            Assert.Equal("no_queryable_columns", only.RowCountUnavailableReason);
            Assert.Empty(session.CountBatches);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>字段未提供的受限架构仍执行计数, 并完成前后身份核验.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LimitedColumnlessTableStillVerifiesIdentity(bool changed)
    {
        var schema = new Schema { Tables = ["Measures"], ColumnlessTables = ["Measures"] };
        var session = new Session { AfterOpen = () => { if (changed) schema.Revision++; } };
        var (reader, snapshot) = await Connect(schema, new(), session);
        try
        {
            if (changed)
            {
                var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
                Assert.Equal("STALE_BASELINE", error.Code);
            }
            else
            {
                var result = await reader.ReadStatisticsAsync(snapshot, default);
                Assert.True(Assert.Single(result.Tables).IsQueryCount);
                Assert.Equal(3, schema.OpenCount);
            }
            Assert.Single(session.CountBatches); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>自动统计所有表, 包含特殊名称、真实 0 和超过 UInt32 的计数.</summary>
    [Fact]
    public async Task AutomaticCountsUseCurrentAccountForEveryTable()
    {
        var schema = new Schema { Tables = ["Sales", "空表", "导出'O\"Brien]", "Fourth"] };
        var session = new Session { Count = table => table == "空表" ? 0L : table == "Sales" ? 60L : 4294967296L };
        var (reader, snapshot) = await Connect(schema, new(), session);
        try
        {
            var result = await reader.ReadStatisticsAsync(snapshot, default);
            Assert.Equal(["Sales", "空表", "导出'O\"Brien]", "Fourth"], result.Tables.Select(table => table.Table));
            Assert.Equal([60L, 0L, 4294967296L, 4294967296L], result.Tables.Select(table => table.RowCount));
            Assert.All(result.Tables, table => { Assert.Null(table.SizeBytes); Assert.True(table.IsQueryCount); });
            Assert.False(result.IsSimulation); Assert.Equal(snapshot.ProjectId, result.ProjectId); Assert.Equal(snapshot.BaselineId, result.BaselineId);
            Assert.Equal(schema.Tables, Assert.Single(session.CountBatches)); Assert.True(session.Disposed);
            Assert.Equal(3, schema.OpenCount); Assert.Equal(schema.OpenCount, schema.DisposeCount);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>超过50表只做有界固定批次, 顺序稳定, 不退回每表N+1.</summary>
    [Theory]
    [InlineData(51, 2)]
    [InlineData(101, 3)]
    [InlineData(2000, 40)]
    public async Task ExactCountsUseBatchesOfAtMostFiftyTables(int count, int batches)
    {
        var names = Enumerable.Range(0, count).Select(index => "Table" + index).ToArray();
        var session = new Session(); var (reader, snapshot) = await Connect(new() { Tables = names }, new(), session);
        try
        {
            var result = await reader.ReadStatisticsAsync(snapshot, default);
            Assert.Equal(count, result.Tables.Count); Assert.Equal(names, result.Tables.Select(table => table.Table));
            Assert.Equal(batches, session.CountBatches.Count); Assert.All(session.CountBatches, batch => Assert.InRange(batch.Length, 1, 50));
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>后续批次权限失败不发布第一批结果, 不把失败转换成0, 会话仍释放.</summary>
    [Fact]
    public async Task LaterCountBatchFailureNeverPublishesPartialSuccess()
    {
        var names = Enumerable.Range(0, 51).Select(index => "T" + index).ToArray();
        var session = new Session { FailedCountBatch = 2, DisposeFailure = true };
        var (reader, snapshot) = await Connect(new() { Tables = names }, new(), session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal("MODEL_STATISTICS_FAILED", error.Code); Assert.DoesNotContain("secret-test-token", error.Message);
            Assert.Equal(2, session.CountBatches.Count); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>缺表、重复、模型外名称或无效数字均明确拒绝, 不发布不完整或伪造的精确计数.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("fractional")]
    [InlineData("negative")]
    [InlineData("boolean")]
    [InlineData("overflow")]
    [InlineData("columns")]
    [InlineData("extraColumn")]
    public async Task InvalidCountResultsFailExplicitly(string kind)
    {
        var session = new Session { InvalidCount = kind }; var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal("MODEL_ROW_COUNT_INVALID", error.Code); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>查询可乱序返回, 但计数按所选表身份恢复顺序, 不按返回行位置误配.</summary>
    [Fact]
    public void CountResultsAreMatchedByExactTableName()
    {
        using var rows = Data(["[__Table]", "[__Rows]"], ["Second", 2L], ["First", 1L]);
        var result = ModelTableStatisticsParser.FromRowCounts(["First", "Second"], rows, default);
        Assert.Equal([1L, 2L], result.Select(table => table.RowCount));
        Assert.All(result, table => Assert.True(table.IsQueryCount));
    }

    /// <summary>实际计数返回后身份、可见定义或认证失效不发布旧目标结果.</summary>
    [Theory]
    [InlineData("schema", "STALE_BASELINE")]
    [InlineData("account", "STALE_BASELINE")]
    [InlineData("authentication", "POWER_BI_SIGN_IN_REQUIRED")]
    public async Task ExactCountRechecksIdentityBeforePublishing(string changed, string expectedCode)
    {
        var schema = new Schema(); var cloud = new Cloud();
        var session = new Session { AfterCountRead = () => { if (changed == "schema") schema.Revision++; else if (changed == "account") cloud.Target = Cloud.Changed(changed); else cloud.SignedOut = true; } };
        var (reader, snapshot) = await Connect(schema, cloud, session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal(expectedCode, error.Code); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>计数同步边界取消即使二次清理失败也保留取消, 不返回部分结果.</summary>
    [Fact]
    public async Task ExactCountCancellationPreservesTerminalAndDisposes()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterCountRead = cancellation.Cancel, DisposeFailure = true };
        var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadStatisticsAsync(snapshot, cancellation.Token));
            Assert.True(session.Disposed); Assert.Single(session.CountBatches);
        }
        finally { reader.Disconnect(); }
    }
}
