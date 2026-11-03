// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelTableStatisticsParserTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实固定统计行集的完整尺寸组成、唯一基础表计数、未知语义和异常边界.

using PbiUtils.Windows.Models.Statistics;
using System.Data;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ModelTableStatisticsParserTests
{
    private const string Name = "销售]|表";
    private static readonly string[] Names = [Name, "Missing"];

    /// <summary>真实Desktop基础存储DMV没有PARTITION_NAME, 每表唯一基础记录直接提供整表行数.</summary>
    [Fact]
    public void ActualDesktopStorageSchemaProvidesCountsWithoutPartitionName()
    {
        string[] names = ["导出'O\"Brien]", "空表_零行", "Excel超限1048576行", "Excel超长32768字符"];
        using var storage = Data(["DIMENSION_NAME", "TABLE_ID", "TABLE_PARTITIONS_COUNT", "ROWS_COUNT"],
            [names[0], "Base(1)", 1L, 8L], [names[1], "Base(2)", 1L, 0L],
            [names[2], "Base(3)", 1L, 1048576L], [names[3], "Base(4)", 1L, 1L]);
        var result = ModelTableStatisticsParser.FromStorage(names, storage, null, null, default);
        Assert.Equal([8L, 0L, 1048576L, 1L], result.Select(table => table.RowCount));
        Assert.All(result, table => Assert.Null(table.SizeBytes));
    }

    /// <summary>尺寸含全部数据段、字典、列层次、用户层次和关系, 不把列层次重复段计入.</summary>
    [Fact]
    public void StorageTotalsContainAllComponentsAndPreserveUnknownTables()
    {
        using var tables = Tables([Name, "Base(1)", 12L], [Name, "H$Base(1)", 999L]);
        using var columns = Columns([Name, "Base(1)", "c1", "BASIC_DATA", 100m], [Name, "Base(1)", "c2", "BASIC_DATA", 50m]);
        using var segments = Segments([Name, "Base(1)", "c1", 0L, 0L, 20m], [Name, "Base(1)", "c1", 1L, 0L, 30m],
            [Name, "Base(1)", "c2", 0L, 0L, 40m], [Name, "H$Base(1)$c1", "map", 0L, 0L, 10m],
            [Name, "H$Base(1)$c1", "map", 1L, 0L, 999m], [Name, "U$Hierarchy", "u", 0L, 0L, 12m], [Name, "R$Relation", "r", 0L, 0L, 14m]);
        var result = ModelTableStatisticsParser.FromStorage(Names, tables, columns, segments, default);
        Assert.Equal(12L, result[0].RowCount); Assert.Equal(276L, result[0].SizeBytes); Assert.False(result[0].IsEstimate);
        Assert.Equal("Missing", result[1].Table); Assert.Null(result[1].RowCount); Assert.Null(result[1].SizeBytes);
    }

    /// <summary>真实投影不含物理分区身份, 多基础记录不论TABLE_ID相同或不同均保持歧义未知.</summary>
    [Theory]
    [InlineData("Base(1)")]
    [InlineData("Base(2)")]
    public void MultipleBaseRecordsRemainUnknown(string secondId)
    {
        using var tables = Tables([Name, "Base(1)", 5L], [Name, secondId, 6L]);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, null, null, default)[0].RowCount);
    }

    /// <summary>已知零保持零, 未提供列行集、缺字段或未知内部结构则不能生成部分尺寸成功.</summary>
    [Fact]
    public void KnownZeroAndUnavailableSizeAreDifferent()
    {
        using var tables = Tables([Name, "Base(1)", 0L]);
        using var columns = Columns([Name, "Base(1)", "c1", "BASIC_DATA", 0L]);
        using var segments = Segments();
        Assert.Equal(0L, ModelTableStatisticsParser.FromStorage([Name], tables, columns, segments, default)[0].SizeBytes);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, null, segments, default)[0].SizeBytes);
        segments.Rows.Add(Name, "X$Unsupported", "unknown", 0L, 0L, 1L);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, columns, segments, default)[0].SizeBytes);
    }

    /// <summary>缺失、负数、分数字节或累加溢出均保留未知, 不舍入、绕回或部分相加.</summary>
    [Theory]
    [InlineData("negative")]
    [InlineData("fractional")]
    [InlineData("overflow")]
    [InlineData("duplicate")]
    public void InvalidOrAmbiguousStorageNeverReturnsPartialSize(string kind)
    {
        using var tables = Tables([Name, "Base(1)", 1L]);
        var bytes = kind == "negative" ? -1m : kind == "fractional" ? 1.5m : kind == "overflow" ? long.MaxValue : 1m;
        using var columns = Columns([Name, "Base(1)", "c1", "BASIC_DATA", bytes]);
        using var segments = Segments([Name, "Base(1)", "c1", 0L, 0L, 1m]);
        if (kind == "duplicate") columns.Rows.Add(Name, "Base(1)", "c1", "BASIC_DATA", 1m);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, columns, segments, default)[0].SizeBytes);
    }

    /// <summary>未识别列类型和未计入的计算字典不能作为完整占用, 即使其他组成可用也保持未知.</summary>
    [Theory]
    [InlineData("UNKNOWN", 0L)]
    [InlineData("FUTURE_DATA", 0L)]
    [InlineData("CALCULATED_DATA", 10L)]
    public void UnknownColumnsCannotClaimCompleteStorage(string type, long bytes)
    {
        using var tables = Tables([Name, "Base(1)", 1L]);
        using var columns = Columns([Name, "Base(1)", "c1", "BASIC_DATA", 0L], [Name, "Base(1)", "c2", type, bytes]);
        using var segments = Segments([Name, "Base(1)", "c1", 0L, 0L, 1L]);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, columns, segments, default)[0].SizeBytes);
    }

    /// <summary>唯一基础记录行数拒绝小数、负值、布尔值及越界, 不把错误类型转换成实际计数.</summary>
    [Theory]
    [InlineData("negative")]
    [InlineData("fractional")]
    [InlineData("boolean")]
    [InlineData("overflow")]
    public void InvalidCountsStayUnknown(string kind)
    {
        object count = kind switch { "negative" => -1L, "fractional" => 0.5m, "boolean" => true, _ => ulong.MaxValue };
        using var tables = Tables([Name, "Base(1)", count]);
        Assert.Null(ModelTableStatisticsParser.FromStorage([Name], tables, null, null, default)[0].RowCount);
    }

    /// <summary>架构成员数只作估计, 特殊名称按DAX唯一名解码; 未提供、负值及重复维度保持未知.</summary>
    [Fact]
    public void LimitedSchemaCardinalityIsEstimatedAndSizeIsUnknown()
    {
        using var dimensions = Data(["DIMENSION_UNIQUE_NAME", "DIMENSION_TYPE", "DIMENSION_CARDINALITY"],
            ["[销售]]|表]", 3, 4294967296L], ["[Missing]", 3, DBNull.Value], ["[Measures]", 2, 20L]);
        var result = ModelTableStatisticsParser.FromDimensions(Names, dimensions, default);
        Assert.Equal(4294967296L, result[0].RowCount); Assert.True(result[0].IsEstimate); Assert.Null(result[0].SizeBytes);
        Assert.Null(result[1].RowCount); Assert.Null(result[1].SizeBytes);
        dimensions.Rows.Add("[销售]]|表]", 3, 4L);
        Assert.Null(ModelTableStatisticsParser.FromDimensions(Names, dimensions, default)[0].RowCount);
    }

    /// <summary>取消不能被未知统计掩盖, 即使行集未提供也立即返回取消.</summary>
    [Fact]
    public void CancelledParsingNeverPublishesUnknownSuccess()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelTableStatisticsParser.FromStorage(Names, null, null, null, token.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ModelTableStatisticsParser.FromDimensions(Names, null, token.Token));
    }

    /// <summary>按固定基础存储表列构造给定测试行, 返回调用方释放的内存表.</summary>
    private static DataTable Tables(params object[][] rows) => Data(["DIMENSION_NAME", "TABLE_ID", "ROWS_COUNT"], rows);
    /// <summary>按固定字典行集列构造给定测试行, 返回调用方释放的内存表.</summary>
    private static DataTable Columns(params object[][] rows) => Data(["DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "COLUMN_TYPE", "DICTIONARY_SIZE"], rows);
    /// <summary>按固定段行集列构造给定测试行, 返回调用方释放的内存表.</summary>
    private static DataTable Segments(params object[][] rows) => Data(["DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "SEGMENT_NUMBER", "TABLE_PARTITION_NUMBER", "USED_SIZE"], rows);
    /// <summary>构造纯内存固定列行集, 不读取SDK或用户模型.</summary>
    private static DataTable Data(string[] columns, params object[][] rows)
    {
        var table = new DataTable();
        foreach (var column in columns) table.Columns.Add(column, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }
}
