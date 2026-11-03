// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteConnectionsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过正式 MCP 分发验证隔离连接、最近记录及可信本地远端表单的写入正向行为.

using PbiUtils.Infrastructure.Connections;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;
using static PbiUtils.UnitTests.McpWriteConnectionsFixture;

namespace PbiUtils.UnitTests;

/// <summary>共享连接和存储实际执行, 外部模型读取为受控适配器, 不代表真实引擎验收.</summary>
public sealed class McpWriteConnectionsTests
{
    /// <summary>本地连接和断开无需二次确认, 绑定真实调用参数、工作区代际及持久回执.</summary>
    [Fact]
    public async Task LocalConnectAndDisconnectPublishActualWorkspaceWithoutConfirmation()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        List<long> revisions = [];
        var parameters = new { processId = "701", instanceId = new string('a', 64) };
        var snapshot = Assert.IsType<ProjectSnapshot>(await fixture.Session.InvokeMcpAsync("project.connectLocal", Json(parameters), default,
            observeConnectionRevision: revisions.Add));
        Assert.Equal([701], fixture.Reader.ProcessIds);
        Assert.Equal(1, fixture.Reader.LocalVerifications);
        Assert.Equal("desktop", snapshot.Connection!.Kind);
        Assert.Equal(Json(snapshot).GetRawText(), Json(await fixture.Invoke("project.inspect", new { }) ?? throw new InvalidOperationException()).GetRawText());
        Assert.Equal([1L], revisions);
        fixture.AssertReceipt("project.connectLocal", parameters);
        var disconnected = Json(await fixture.Session.InvokeMcpAsync("project.disconnect", Json(new { }), default, 1, revisions.Add) ?? throw new InvalidOperationException());
        Assert.True(disconnected.GetProperty("disconnected").GetBoolean());
        Assert.Empty(fixture.Reader.Targets);
        Assert.Equal(1, fixture.Reader.Disconnects);
        Assert.Equal([1L, 2L], revisions);
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("uiDialog", out _));
        Assert.Equal("PROJECT_NOT_SELECTED", (await Assert.ThrowsAsync<AppException>(() => fixture.Invoke("project.inspect", new { }))).Code);
        fixture.AssertReceipt("project.disconnect", new { });
    }

    /// <summary>完整路径入口复用文件核验和历史存储, 无选择器或额外通用确认.</summary>
    [Fact]
    public async Task ExactPathOpenUsesFileWorkflowWithoutPickerAndPublishesRevision()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        List<long> revisions = [];
        var parameters = new { path = fixture.ProjectFile };
        var snapshot = Assert.IsType<ProjectSnapshot>(await fixture.Session.InvokeMcpAsync("project.openPath", Json(parameters), default,
            observeConnectionRevision: revisions.Add));
        Assert.Equal([fixture.ProjectFile], fixture.Reader.OpenedFiles);
        Assert.Equal(1, fixture.Reader.FileVerifications);
        Assert.Equal(0, fixture.FileSelections);
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("uiDialog", out _));
        Assert.Equal([1L], revisions);
        Assert.Equal(fixture.ProjectFile, Assert.Single(fixture.Recent.Load()).Path);
        Assert.Equal(snapshot.ProjectId, Assert.IsType<ProjectSnapshot>(await fixture.Invoke("project.inspect", new { })).ProjectId);
        fixture.AssertReceipt("project.openPath", parameters);
    }

    /// <summary>MCP 本地连接须提供发现身份, 不得仅按 PID 连接可能重启的同名实例.</summary>
    [Fact]
    public async Task LocalConnectRejectsMissingOrChangedDiscoveredIdentity()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        Assert.Equal("INVALID_INSTANCE", (await Assert.ThrowsAsync<AppException>(() => fixture.Invoke("project.connectLocal", new { processId = "701" }))).Code);
        Assert.Equal("POWER_BI_INSTANCE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => fixture.Invoke("project.connectLocal",
            new { processId = "701", instanceId = new string('b', 64) }))).Code);
        Assert.Empty(fixture.Reader.Targets);
        Assert.Empty(fixture.Recent.Load());
    }

    /// <summary>网页不能取得 MCP 路径打开能力, 失败不触发选择器、文件打开和历史写入.</summary>
    [Fact]
    public async Task WebCannotCallExactPathOpen()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        await fixture.Session.ReceiveAsync(PbiUtils.Infrastructure.Gui.OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id = "web-path", method = "project.openPath", @params = new { path = fixture.ProjectFile } }));
        var response = Assert.Single(fixture.Messages, value => value.TryGetProperty("id", out var id) && id.GetString() == "web-path");
        Assert.Equal("MCP_ONLY", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.Reader.OpenedFiles);
        Assert.Equal(0, fixture.FileSelections);
        Assert.Empty(fixture.Recent.Load());
    }

    /// <summary>文件选择和最近重开经过共享连接及真实历史存储, 删除历史不修改测试工程文件.</summary>
    [Fact]
    public async Task FileOpenRecentReopenAndDeletePersistOnlyAuthorizedHistory()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var opened = Assert.IsType<ProjectSnapshot>(await fixture.Write("project.open", new { }, () =>
        {
            Assert.Equal(0, fixture.FileSelections);
            Assert.Empty(fixture.Reader.OpenedFiles);
            Assert.Empty(fixture.Recent.Load());
        }));
        Assert.Equal(1, fixture.FileSelections);
        Assert.Equal([fixture.ProjectFile], fixture.Reader.OpenedFiles);
        var recent = Assert.Single(new LocalRecentProjects(fixture.Root).Load());
        Assert.Equal(fixture.ProjectFile, recent.Path);
        Assert.Equal(opened.ProjectPath, Assert.IsType<ProjectSnapshot>(await fixture.Invoke("project.inspect", new { })).ProjectPath);
        var reopened = Assert.IsType<ProjectSnapshot>(await fixture.Write("project.openRecent", new { id = recent.Id }, () =>
        {
            Assert.Single(fixture.Reader.OpenedFiles);
            Assert.Equal(1, fixture.FileSelections);
        }));
        Assert.NotEqual(opened.ProjectPath, reopened.ProjectPath);
        Assert.Equal([fixture.ProjectFile, fixture.ProjectFile], fixture.Reader.OpenedFiles);
        Assert.Equal(2, fixture.Reader.FileVerifications);
        Assert.Equal(4, fixture.Reader.LocalVerifications);
        Assert.Single(fixture.Reader.Targets);
        var deleted = Assert.IsType<RecentRecordDeletionResult>(await fixture.Write("project.deleteRecent", new { id = recent.Id }, () =>
            Assert.Equal(recent.Id, Assert.Single(fixture.Recent.Load()).Id)));
        Assert.True(deleted.Deleted);
        Assert.Empty(new LocalRecentProjects(fixture.Root).Load());
        Assert.Equal("isolated-file-picker-sentinel", await File.ReadAllTextAsync(fixture.ProjectFile));
        Assert.Equal(reopened.ProjectPath, Assert.IsType<ProjectSnapshot>(await fixture.Invoke("project.inspect", new { })).ProjectPath);
    }

    /// <summary>MCP 无秘密地址直接连接, 返回真实只读快照和回执, 不保存最近记录或弹出表单.</summary>
    [Fact]
    public async Task RemoteConnectionUsesCurrentIdentityWithoutSavingConnection()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var parameters = new { server = "fixture-server", database = "fixture-database" };
        var snapshot = Assert.IsType<ProjectSnapshot>(await fixture.Invoke("project.connectRemote", parameters));
        Assert.Equal([("fixture-server", "fixture-database")], fixture.Reader.RemoteAddresses);
        Assert.Empty(new LocalRemoteConnectionStore(fixture.Root).Load());
        Assert.Equal(Json(snapshot).GetRawText(), Json(await fixture.Invoke("project.inspect", new { }) ?? throw new InvalidOperationException()).GetRawText());
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("uiDialog", out _));
        fixture.AssertReceipt("project.connectRemote", parameters);
    }
}
