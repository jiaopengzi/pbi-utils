// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldDesktopConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实桥接和共享文件连接验证新建 Desktop 打开的当前模型, 取消和失败边界.

using PbiUtils.Application.Models;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>控制模型适配器的读取边界, 不启动真实 Desktop 或把 PBIP 存在当作模型连接.</summary>
public sealed class ScaffoldDesktopConnectionTests
{
    /// <summary>成功后仅保留新连接, 返回快照可立即作为当前模型刷新, 最近记录仍使用已创建磁盘入口.</summary>
    [Fact]
    public async Task CreatedDesktopOpenUsesSharedFileConnectionAndPublishesCurrentModel()
    {
        await using var fixture = new ScaffoldDesktopConnectionFixture();
        await fixture.CreateAsync();
        var previous = await fixture.ConnectPreviousAsync();
        await fixture.Call("scaffold.openDesktop", new { });
        var result = fixture.Last.GetProperty("result");
        var project = result.GetProperty("project");
        var connected = project.GetProperty("projectPath").GetString();
        Assert.True(result.GetProperty("opened").GetBoolean());
        Assert.StartsWith("model://", connected);
        Assert.NotEqual(previous, connected);
        Assert.Equal("desktop", project.GetProperty("connection").GetProperty("kind").GetString());
        Assert.False(result.TryGetProperty("entryPath", out _));
        Assert.Equal(fixture.Entry, fixture.OpenedFile);
        Assert.Equal(fixture.CreationIdentity, fixture.ForwardedIdentity);
        Assert.Equal(fixture.Entry, Assert.Single(fixture.Recent.Load()).Path);
        Assert.Equal(connected, Assert.Single(fixture.Reader.Targets.Keys));
        Assert.Equal(1, fixture.FileVerifications);
        await fixture.Call("project.inspect", new { });
        Assert.Equal(connected, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>读取或发布失败都保留原当前模型, 撤销本轮新注册, 不留下成功最近记录.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDesktopConnectionPreservesPreviousModel(bool failPublication)
    {
        await using var fixture = new ScaffoldDesktopConnectionFixture();
        await fixture.CreateAsync();
        var previous = await fixture.ConnectPreviousAsync();
        if (failPublication) fixture.CorruptRecent();
        else fixture.Reader.Fail = true;
        await fixture.Call("scaffold.openDesktop", new { });
        Assert.Equal(failPublication ? "INVALID_RECENT_PROJECTS" : "TEST_READ_FAILED", fixture.Error);
        fixture.Reader.Fail = false;
        await fixture.Call("project.inspect", new { });
        Assert.Equal(previous, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Equal(previous, Assert.Single(fixture.Reader.Targets.Keys));
        if (!failPublication) Assert.Empty(fixture.Recent.Load());
        Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>等待连接时仍可取消, 超时与文档失效同样等待读取排空并保留原模型.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("document")]
    public async Task CancelledDesktopConnectionDrainsAndKeepsPreviousModel(string reason)
    {
        await using var fixture = new ScaffoldDesktopConnectionFixture();
        await fixture.CreateAsync();
        var previous = await fixture.ConnectPreviousAsync();
        fixture.Reader.Block = true;
        var opening = fixture.Call("scaffold.openDesktop", new { }, "opening");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["connecting"], fixture.Phases("opening"));
        if (reason == "cancel")
        {
            await fixture.Call("request.cancel", new { requestId = "opening" });
            Assert.True(fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean());
        }
        else if (reason == "timeout")
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(179));
            Assert.False(fixture.Reader.Token.IsCancellationRequested);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        else fixture.Session.InvalidateDocument();
        Assert.True(fixture.Reader.Token.IsCancellationRequested);
        Assert.False(opening.IsCompleted);
        await fixture.Call("project.connectLocal", new { processId = "300" });
        Assert.Equal("BUSY", fixture.Error);
        fixture.Reader.Release.TrySetResult();
        await opening;
        Assert.Equal(reason == "timeout" ? "TIMEOUT" : "CANCELLED", fixture.Error);
        fixture.Reader.Block = false;
        await fixture.Call("project.inspect", new { });
        Assert.Equal(previous, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Equal(previous, Assert.Single(fixture.Reader.Targets.Keys));
        Assert.Empty(fixture.Recent.Load());
        Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>新建打开占用与连接页连接及刷新相同的模型门闩, 未结束前不会启动第二次连接.</summary>
    [Fact]
    public async Task OpeningCreatedDesktopSharesCurrentModelGate()
    {
        await using var fixture = new ScaffoldDesktopConnectionFixture();
        await fixture.CreateAsync();
        await fixture.ConnectPreviousAsync();
        fixture.Reader.Block = true;
        var opening = fixture.Call("scaffold.openDesktop", new { }, "opening");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var method in new[] { "scaffold.openDesktop", "project.inspect", "project.connectLocal" })
        {
            await fixture.Call(method, method == "project.connectLocal" ? new { processId = "300" } : (object)new { });
            Assert.Equal("BUSY", fixture.Error);
        }
        Assert.Equal(2, fixture.Reader.Targets.Count);
        fixture.Reader.Release.TrySetResult();
        await opening;
        Assert.True(fixture.Last.GetProperty("result").GetProperty("opened").GetBoolean());
        Assert.Single(fixture.Reader.Targets);
    }

    /// <summary>当前连接存在未完成保存时, 新建打开在启动文件连接前明确拒绝并保留原模型.</summary>
    [Fact]
    public async Task UnsavedCurrentModelRejectsCreatedDesktopBeforeOpeningFile()
    {
        await using var fixture = new ScaffoldDesktopConnectionFixture();
        await fixture.CreateAsync();
        var previous = await fixture.ConnectPreviousAsync();
        fixture.Edits.Select(ScaffoldDesktopConnectionFixture.Snapshot(previous) with
        {
            SaveResult = new ModelSaveResult(true, false, false, "未完成保存", "SAVE_FAILED")
        });
        Assert.True(fixture.Session.HasPendingChanges);
        await fixture.Call("scaffold.openDesktop", new { });
        Assert.Equal("UNSAVED_CHANGES", fixture.Error);
        Assert.Null(fixture.OpenedFile);
        Assert.Equal(previous, Assert.Single(fixture.Reader.Targets.Keys));
        Assert.Empty(fixture.Recent.Load());
    }
}
