// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportBridgeTests.SamplingAndLocations.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证抽样参数及宿主导出目录记录/打开的真实桥接行为, 不启动原生窗口或程序.

using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class ModelTableExportBridgeTests
{
    /// <summary>七字段中的抽样上限原样绑定保存选择、平台读取和最终回执, null保持全量意图.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1)]
    [InlineData(null)]
    public async Task SamplingLimitFlowsThroughBoundBridge(int? limit)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", ExportParameters(["Sales"], "xlsx", limit), "sample");
        Assert.Equal(limit, fixture.SelectedRequest!.RowLimit);
        Assert.Equal([limit], fixture.ReadLimits);
        var result = fixture.Response("sample").GetProperty("result").GetProperty("rowLimit");
        Assert.Equal(limit, result.ValueKind == JsonValueKind.Null ? null : (int?)result.GetInt32());
        Assert.True(fixture.Committed); Assert.Equal(1, fixture.SelectionCount);
    }

    /// <summary>非法0/负值/小数/越界在任何保存选择或数据读取之前拒绝, 不自动转为全部行.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public async Task InvalidRowLimitNeverOpensDestination(string value)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", new
        {
            projectId = "project",
            baselineId = "baseline",
            tables = SalesTables,
            format = "xlsx",
            includeLog = false,
            locale = "zh-cn",
            rowLimit = JsonSerializer.Deserialize<JsonElement>(value)
        }, "invalid-sample");
        Assert.Equal("VALIDATION_ERROR", fixture.Response("invalid-sample").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.SelectionCount); Assert.Equal(0, fixture.CreatedFiles); Assert.Empty(fixture.ReadLimits);
    }

    /// <summary>发布唯一成功之前保留旧记录; 单文件记录parent, 多CSV记录新目录本身, 不混淆上层目录.</summary>
    [Theory]
    [InlineData("xlsx", false)]
    [InlineData("csv", false)]
    [InlineData("csv", true)]
    public async Task OnlyCompletedPublicationRecordsCorrectDirectory(string format, bool multiple)
    {
        await using var fixture = new Fixture(locations: true) { BlockCommit = true };
        var previous = fixture.Locations!.Remember(fixture.DirectoryFor("previous"), DateTimeOffset.UtcNow.AddMinutes(-1));
        if (multiple) AddOtherTable(fixture);
        var expected = fixture.DirectoryFor("chosen-parent");
        fixture.Destination = Path.Combine(expected, multiple ? "new-csv-batch" : "Sales." + format);
        if (multiple) expected = fixture.Destination;
        await fixture.ConnectAsync();
        var exporting = fixture.Call("model.exportTables", ExportParameters(multiple ? ["Sales", "Other"] : ["Sales"], format, 100), "export-location");
        await fixture.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(previous, fixture.Locations.Read());
        Assert.False(fixture.Committed);
        fixture.CommitRelease.TrySetResult();
        await exporting.WaitAsync(TimeSpan.FromSeconds(3));
        var recorded = fixture.Locations.Read();
        Assert.True(fixture.Committed); Assert.NotEqual(previous.Id, recorded.Id);
        Assert.Equal(expected, recorded.DisplayPath); Assert.True(recorded.Available); Assert.Null(recorded.Warning);
        Assert.Equal(JsonValueKind.Null, fixture.Response("export-location").GetProperty("result").GetProperty("locationWarning").ValueKind);
    }

    /// <summary>用户取消选择或导出写入失败都不能替换最近成功目录, 不制造可打开的新成功记录.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureOrCancelledSelectionKeepsPreviousLocation(bool cancel)
    {
        await using var fixture = new Fixture(locations: true) { CancelSelection = cancel, FailWrite = !cancel };
        var previous = fixture.Locations!.Remember(fixture.DirectoryFor("previous"), DateTimeOffset.UtcNow.AddMinutes(-1));
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "unsuccessful");
        Assert.Equal(cancel ? "CANCELLED" : "EXPORT_VALUE_UNSUPPORTED", fixture.Response("unsuccessful").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(previous, fixture.Locations.Read()); Assert.False(fixture.Committed);
    }

    /// <summary>文件已经发布时目录持久化故障仅作为成功警告返回, 保留会话记录而不诱导重放导出.</summary>
    [Fact]
    public async Task LocationPersistenceFailureKeepsExportSuccessfulWithWarning()
    {
        await using var fixture = new Fixture(locations: true, failLocationStore: true);
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "published");
        var result = fixture.Response("published").GetProperty("result");
        Assert.True(fixture.Committed); Assert.False(fixture.Response("published").TryGetProperty("error", out _));
        Assert.Equal("文件已导出, 但无法保存导出目录记录", result.GetProperty("locationWarning").GetString());
        await fixture.Call("model.exportLocation", new { }, "read-fallback");
        var location = fixture.Response("read-fallback").GetProperty("result");
        Assert.True(location.GetProperty("available").GetBoolean());
        Assert.Equal(Path.GetDirectoryName(fixture.Destination), location.GetProperty("displayPath").GetString());
        Assert.Equal(1, fixture.SelectionCount); Assert.Equal(1, fixture.CreatedFiles);
    }

    /// <summary>最近成功目录与模型连接无关, 尚未连接模型也能读取并用宿主身份打开.</summary>
    [Fact]
    public async Task LocationCanBeReadAndOpenedWithoutSelectedModel()
    {
        await using var fixture = new Fixture(locations: true);
        var expected = fixture.Locations!.Remember(fixture.DirectoryFor("existing"), DateTimeOffset.UtcNow);
        await fixture.Call("model.exportLocation", new { }, "location");
        var response = fixture.Response("location").GetProperty("result");
        Assert.Equal(expected.Id, response.GetProperty("id").GetString());
        Assert.Equal(expected.DisplayPath, response.GetProperty("displayPath").GetString());
        await fixture.Call("model.openExportDirectory", new { id = expected.Id }, "open-location");
        Assert.True(fixture.Response("open-location").GetProperty("result").GetProperty("opened").GetBoolean());
        Assert.Equal(expected.DisplayPath, Assert.Single(fixture.OpenedDirectories));
        Assert.False(fixture.OpenToken.CanBeCanceled); Assert.Equal(0, fixture.SelectionCount);
    }

    /// <summary>只允许空参数读取和单一记录id打开, 网页不能添加path或把路径当作权限.</summary>
    [Theory]
    [InlineData("model.exportLocation")]
    [InlineData("model.openExportDirectory")]
    public async Task LocationMethodsRejectWebPaths(string method)
    {
        await using var fixture = new Fixture(locations: true);
        var existing = fixture.Locations!.Remember(fixture.DirectoryFor("existing"), DateTimeOffset.UtcNow);
        var parameters = method == "model.exportLocation" ? (object)new { path = existing.DisplayPath } : new { id = existing.Id, path = existing.DisplayPath };
        await fixture.Call(method, parameters, "path-rejected");
        Assert.Equal("VALIDATION_ERROR", fixture.Response("path-rejected").GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.OpenedDirectories);
    }

    /// <summary>另一个成功记录出现后旧id不得悄悄打开新目录; 必须由界面重新读取当前记录.</summary>
    [Fact]
    public async Task StaleLocationIdDoesNotOpenDifferentDirectory()
    {
        await using var fixture = new Fixture(locations: true);
        var previous = fixture.Locations!.Remember(fixture.DirectoryFor("previous"), DateTimeOffset.UtcNow.AddMinutes(-1));
        var current = fixture.Locations.Remember(fixture.DirectoryFor("current"), DateTimeOffset.UtcNow);
        await fixture.Call("model.openExportDirectory", new { id = previous.Id }, "stale-location");
        Assert.Equal("EXPORT_LOCATION_CHANGED", fixture.Response("stale-location").GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.OpenedDirectories); Assert.Equal(current, fixture.Locations.Read());
    }

    /// <summary>被记录目录消失时明确拒绝打开, 即使其父目录存在也不能回退到父目录.</summary>
    [Fact]
    public async Task MissingDirectoryNeverFallsBackToParent()
    {
        await using var fixture = new Fixture(locations: true);
        var directory = fixture.DirectoryFor("removed");
        var previous = fixture.Locations!.Remember(directory, DateTimeOffset.UtcNow);
        Directory.Delete(directory);
        Assert.True(Directory.Exists(Path.GetDirectoryName(directory)));
        await fixture.Call("model.openExportDirectory", new { id = previous.Id }, "missing-location");
        Assert.Equal("EXPORT_LOCATION_UNAVAILABLE", fixture.Response("missing-location").GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.OpenedDirectories);
    }

    /// <summary>模型读操作仍排空时不能打开目录, 不通过新系统操作绕过共享projectGate.</summary>
    [Fact]
    public async Task ModelOperationPreventsDirectoryLaunchUntilDrained()
    {
        await using var fixture = new Fixture(locations: true) { BlockRead = true };
        var location = fixture.Locations!.Remember(fixture.DirectoryFor("existing"), DateTimeOffset.UtcNow);
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "reading");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("model.openExportDirectory", new { id = location.Id }, "blocked-open");
        Assert.Equal("BUSY", fixture.Response("blocked-open").GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.OpenedDirectories);
        await fixture.Call("request.cancel", new { requestId = "reading" }, "cancel-reading");
        fixture.ReadRelease.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(location, fixture.Locations.Read());
    }

    /// <summary>原生打开已取得提交所有权之后取消不受理, 锁持续到平台打开回调退出并只发一个成功终态.</summary>
    [Fact]
    public async Task DirectoryLaunchAfterCommitRejectsCancellationAndDrains()
    {
        await using var fixture = new Fixture(locations: true) { BlockOpen = true };
        var location = fixture.Locations!.Remember(fixture.DirectoryFor("existing"), DateTimeOffset.UtcNow);
        await fixture.ConnectAsync();
        var opening = fixture.Call("model.openExportDirectory", new { id = location.Id }, "opening");
        await fixture.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("request.cancel", new { requestId = "opening" }, "cancel-opening");
        Assert.False(fixture.Response("cancel-opening").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.False(opening.IsCompleted); Assert.False(fixture.OpenToken.CanBeCanceled);
        await fixture.Call("model.tableRowCounts", CountParameters(["Sales"]), "blocked-count");
        Assert.Equal("BUSY", fixture.Response("blocked-count").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.CountCalls);
        fixture.OpenRelease.TrySetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Response("opening").GetProperty("result").GetProperty("opened").GetBoolean());
        Assert.Single(fixture.OpenedDirectories); Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>构造当前严格七字段请求, 此辅助器不增加路径或查询授权.</summary>
    private static object ExportParameters(string[] tables, string format, int? rowLimit)
        => new { projectId = "project", baselineId = "baseline", tables, format, includeLog = false, locale = "zh-cn", rowLimit };

    /// <summary>增加当前权威快照中的第二个实际表, 不伪造请求内的来源能力.</summary>
    private static void AddOtherTable(Fixture fixture) => fixture.Snapshot = fixture.Snapshot with
    {
        Model = fixture.Snapshot.Model with { Tables = [.. fixture.Snapshot.Model.Tables, new TableInfo("Other", null, false, [new("Value", "Data", "Int64", false, null)], [])] }
    };
}
