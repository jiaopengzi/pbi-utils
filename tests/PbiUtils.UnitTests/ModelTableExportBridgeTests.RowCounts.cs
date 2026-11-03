// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportBridgeTests.RowCounts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证精确行数查询桥接的身份、所选范围、共享门闩及取消排空, 不执行真实DAX.

using System.Text.Json;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class ModelTableExportBridgeTests
{
    private static readonly long[] OnlyOtherCount = [5L];

    /// <summary>只统计精确选择的表并保留请求顺序, 身份来自已选模型, 不打开保存窗口或返回存储大小.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactCountsUseBoundSnapshotAndSelectedTableOrder(bool includeSales)
    {
        using var fixture = new Fixture();
        AddOtherTable(fixture);
        await fixture.ConnectAsync();
        string[] selected = includeSales ? ["Other", "Sales"] : ["Other"];
        await fixture.Call("model.tableRowCounts", CountParameters(selected), "counts");
        Assert.NotNull(fixture.CountTables);
        Assert.Equal(selected, fixture.CountTables);
        Assert.Equal(fixture.Snapshot.ProjectId, fixture.CountSnapshot!.ProjectId);
        Assert.Equal(fixture.Snapshot.BaselineId, fixture.CountSnapshot.BaselineId);
        Assert.Equal(fixture.Snapshot.ProjectPath, fixture.CountSnapshot.ProjectPath);
        var result = fixture.Response("counts").GetProperty("result");
        Assert.Equal("project", result.GetProperty("projectId").GetString());
        Assert.Equal("baseline", result.GetProperty("baselineId").GetString());
        var tables = result.GetProperty("tables").EnumerateArray().ToArray();
        Assert.Equal(selected, tables.Select(table => table.GetProperty("table").GetString()));
        Assert.Equal(includeSales ? [5L, 2L] : OnlyOtherCount, tables.Select(table => table.GetProperty("rowCount").GetInt64()));
        Assert.All(tables, table =>
        {
            Assert.True(table.GetProperty("isQueryCount").GetBoolean());
            Assert.False(table.GetProperty("isEstimate").GetBoolean());
            Assert.Equal(JsonValueKind.Null, table.GetProperty("sizeBytes").ValueKind);
        });
        Assert.Equal(0, fixture.SelectionCount); Assert.Empty(fixture.ReadLimits); Assert.Equal(1, fixture.CountCalls);
    }

    /// <summary>未选模型及旧project/baseline都在精确读取器调用前拒绝, 不能按同名表重绑目标.</summary>
    [Theory]
    [InlineData(false, "project", "baseline", "PROJECT_NOT_SELECTED")]
    [InlineData(true, "other-project", "baseline", "STALE_BASELINE")]
    [InlineData(true, "project", "old-baseline", "STALE_BASELINE")]
    public async Task ExactCountsRejectUnboundOrStaleIdentity(bool connect, string projectId, string baselineId, string expected)
    {
        using var fixture = new Fixture();
        if (connect) await fixture.ConnectAsync();
        await fixture.Call("model.tableRowCounts", new { projectId, baselineId, tables = SalesTables }, "invalid-identity");
        Assert.Equal(expected, fixture.Response("invalid-identity").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.CountCalls); Assert.Equal(0, fixture.SelectionCount);
    }

    /// <summary>精确计数只接受三字段及唯一表数组, 不把额外query、空选择或重复项交给数据源.</summary>
    [Theory]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"tables\":[\"Sales\"],\"query\":\"EVALUATE Sales\"}")]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"tables\":[]}")]
    [InlineData("{\"projectId\":\"project\",\"baselineId\":\"baseline\",\"tables\":[\"Sales\",\"Sales\"]}")]
    public async Task ExactCountsRejectUnknownFieldsAndInvalidSelection(string parameters)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.tableRowCounts", JsonSerializer.Deserialize<JsonElement>(parameters), "invalid-counts");
        Assert.Equal("VALIDATION_ERROR", fixture.Response("invalid-counts").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.CountCalls); Assert.Empty(fixture.ReadLimits);
    }

    /// <summary>精确计数取消确认不提前释放锁, 同步读取退出后唯一取消终态才允许新导出.</summary>
    [Fact]
    public async Task ExactCountsCancellationRetainsSharedGateUntilReaderDrains()
    {
        using var fixture = new Fixture { BlockCounts = true };
        await fixture.ConnectAsync();
        var counting = fixture.Call("model.tableRowCounts", CountParameters(["Sales"]), "counting");
        await fixture.CountsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("model.exportTables", Fixture.Parameters, "blocked-export");
        Assert.Equal("BUSY", fixture.Response("blocked-export").GetProperty("error").GetProperty("code").GetString());
        await fixture.Call("request.cancel", new { requestId = "counting" }, "cancel-counting");
        Assert.True(fixture.Response("cancel-counting").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.True(fixture.CountToken.IsCancellationRequested); Assert.False(counting.IsCompleted); Assert.False(fixture.CountsDrained);
        Assert.DoesNotContain(fixture.Messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "counting");
        await fixture.Call("project.disconnect", new { }, "blocked-disconnect");
        Assert.Equal("BUSY", fixture.Response("blocked-disconnect").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.SelectionCount);
        fixture.CountsRelease.TrySetResult();
        await counting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.CountsDrained);
        Assert.Equal("CANCELLED", fixture.Response("counting").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.Session.PendingCount);
        await fixture.Call("model.exportTables", Fixture.Parameters, "export-after-counts");
        Assert.True(fixture.Response("export-after-counts").TryGetProperty("result", out _));
    }

    /// <summary>导出在途时精确计数也被同一门闩拒绝, 双向互锁不依赖前端disabled.</summary>
    [Fact]
    public async Task ExportInFlightPreventsExactCountsBeforeReaderInvocation()
    {
        using var fixture = new Fixture { BlockRead = true };
        await fixture.ConnectAsync();
        var exporting = fixture.Call("model.exportTables", Fixture.Parameters, "exporting");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("model.tableRowCounts", CountParameters(["Sales"]), "blocked-counts");
        Assert.Equal("BUSY", fixture.Response("blocked-counts").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.CountCalls);
        fixture.ReadRelease.TrySetResult();
        await exporting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Response("exporting").TryGetProperty("result", out _));
    }

    /// <summary>构造唯一表范围的三字段计数请求, 无路径、地址或DAX.</summary>
    private static object CountParameters(string[] tables) => new { projectId = "project", baselineId = "baseline", tables };
}
