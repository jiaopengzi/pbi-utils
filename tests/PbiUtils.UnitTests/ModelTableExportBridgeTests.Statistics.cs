// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportBridgeTests.Statistics.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证整模型统计在桥接入口按当前快照读取, 拒绝额外查询参数并复用导出门闩.

using System.Text.Json;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class ModelTableExportBridgeTests
{
    /// <summary>未选模型及旧 projectId 或 baselineId 都在统计读取器调用前拒绝.</summary>
    [Theory]
    [InlineData(false, "project", "baseline", "PROJECT_NOT_SELECTED")]
    [InlineData(true, "other-project", "baseline", "STALE_BASELINE")]
    [InlineData(true, "project", "old-baseline", "STALE_BASELINE")]
    public async Task StatisticsRejectsUnboundOrStaleIdentity(bool connect, string projectId, string baselineId, string expected)
    {
        using var fixture = new Fixture();
        if (connect) await fixture.ConnectAsync();
        await fixture.Call("model.tableStatistics", new { projectId, baselineId }, "invalid-identity");
        Assert.Equal(expected, fixture.Response("invalid-identity").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.StatisticsEntered.Task.IsCompleted);
        Assert.Equal(0, fixture.SelectionCount);
    }

    /// <summary>统计只接受当前两项模型身份, 网页不能指定表范围、查询或连接.</summary>
    [Theory]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"query\":\"EVALUATE Sales\"}")]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"tables\":[\"Sales\"]}")]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"server\":\"localhost\"}")]
    public async Task StatisticsRejectsUnknownFields(string parameters)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.tableStatistics", JsonSerializer.Deserialize<JsonElement>(parameters), "invalid-statistics");
        Assert.Equal("VALIDATION_ERROR", fixture.Response("invalid-statistics").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.StatisticsEntered.Task.IsCompleted);
    }

    /// <summary>导出在途时统计在同一门闩拒绝, 读取器不被调用.</summary>
    [Fact]
    public async Task ExportInFlightPreventsStatisticsBeforeReaderInvocation()
    {
        using var fixture = new Fixture { BlockRead = true };
        await fixture.ConnectAsync();
        var exporting = fixture.Call("model.exportTables", Fixture.Parameters, "exporting");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("model.tableStatistics", new { projectId = "project", baselineId = "baseline" }, "blocked-statistics");
        Assert.Equal("BUSY", fixture.Response("blocked-statistics").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.StatisticsEntered.Task.IsCompleted);
        fixture.ReadRelease.TrySetResult();
        await exporting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Response("exporting").TryGetProperty("result", out _));
    }
}
