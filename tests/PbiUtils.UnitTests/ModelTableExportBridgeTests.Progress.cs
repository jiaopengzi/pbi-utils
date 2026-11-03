// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportBridgeTests.Progress.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实Bridge只发送绑定请求的安全导出计数, 取消和失败不伪造进度终态.

using System.Text.Json;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class ModelTableExportBridgeTests
{
    /// <summary>实际共享编排通过Bridge发送安全计数, 不包含名称、路径或伪造100%字段.</summary>
    [Fact]
    public async Task ExportProgressIsBoundToTheRequestAndContainsOnlySafeCounters()
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "progress-export");
        var progress = ProgressFor(fixture, "progress-export");
        Assert.NotEmpty(progress);
        Assert.Equal("reading", progress[0].GetProperty("stage").GetString());
        Assert.Equal("publishing", progress[^1].GetProperty("stage").GetString());
        Assert.All(progress, item =>
        {
            Assert.Equal(["completedTables", "kind", "requestId", "rowsRead", "stage", "tableIndex", "totalTables"], item.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            Assert.Equal("tableExport", item.GetProperty("kind").GetString());
            Assert.Equal("progress-export", item.GetProperty("requestId").GetString());
            Assert.Equal(1, item.GetProperty("tableIndex").GetInt32());
            Assert.Equal(1, item.GetProperty("totalTables").GetInt32());
            Assert.Equal(0, item.GetProperty("rowsRead").GetInt64());
        });
        Assert.True(fixture.Response("progress-export").TryGetProperty("result", out _));
        Assert.True(fixture.Messages.Last().TryGetProperty("result", out _));
    }

    /// <summary>同步SDK等待已接受取消后才返回, 后续进度不得发给已经取消的请求.</summary>
    [Fact]
    public async Task AcceptedCancellationStopsProgressWhileOriginalExportDrains()
    {
        using var fixture = new Fixture { BlockRead = true };
        await fixture.ConnectAsync();
        var exporting = fixture.Call("model.exportTables", Fixture.Parameters, "progress-export");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var count = ProgressFor(fixture, "progress-export").Length;
        Assert.True(count > 0);
        await fixture.Call("request.cancel", new { requestId = "progress-export" }, "cancel-progress");
        Assert.True(fixture.Response("cancel-progress").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.False(exporting.IsCompleted);
        fixture.ReadRelease.TrySetResult();
        await exporting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(count, ProgressFor(fixture, "progress-export").Length);
        Assert.Equal("CANCELLED", fixture.Response("progress-export").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.Committed);
    }

    /// <summary>数据写入首错终止当前轮, 实际读完和发布阶段不得通过无条件进度事件伪造.</summary>
    [Fact]
    public async Task FailedWriteNeverReportsCompletedOrPublishingProgress()
    {
        using var fixture = new Fixture { FailWrite = true };
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "progress-export");
        var progress = ProgressFor(fixture, "progress-export");
        Assert.NotEmpty(progress);
        Assert.All(progress, item =>
        {
            Assert.Equal("reading", item.GetProperty("stage").GetString());
            Assert.Equal(0, item.GetProperty("completedTables").GetInt32());
        });
        Assert.True(fixture.Response("progress-export").TryGetProperty("error", out _));
        Assert.False(fixture.Committed);
    }

    /// <summary>只提取明确绑定本请求的结构化进度, 不把阶段或终态响应当成进度.</summary>
    /// <param name="fixture">本组当前Bridge会话.</param>
    /// <param name="id">需要核验的原始请求身份.</param>
    /// <returns>保持宿主发送顺序的安全计数对象.</returns>
    private static JsonElement[] ProgressFor(Fixture fixture, string id) => [.. fixture.Messages
        .Where(message => message.TryGetProperty("requestProgress", out var progress) && progress.GetProperty("requestId").GetString() == id)
        .Select(message => message.GetProperty("requestProgress"))];
}
