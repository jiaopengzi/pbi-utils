// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteConnectionsServiceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP Service 登录、模型连接和历史删除复用真实隔离存储及精确回执.

using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;
using static PbiUtils.UnitTests.McpWriteConnectionsFixture;

namespace PbiUtils.UnitTests;

/// <summary>认证、目录与模型为受控边界, 本地持久化和 MCP 分发真实执行, 不访问真实云账号.</summary>
public sealed class McpWriteConnectionsServiceTests
{
    /// <summary>完整登录及服务连接后按账号隔离保存历史, 删除不影响其他账号/云, 退出断开当前服务模型.</summary>
    [Fact]
    public async Task ServiceLoginConnectDeleteHistoryAndSignOutHaveDurableEffects()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var loginParameters = new { email = "fixture@example.com", environmentName = "GlobalCloud", remember = true };
        fixture.Cloud.Committed = () => fixture.RequireCommitStarted("powerbi.serviceSignIn");
        var login = Json(await fixture.Write("powerbi.serviceSignIn", loginParameters, () =>
        {
            Assert.Null(fixture.Cloud.Account);
            Assert.Empty(fixture.Cloud.SignIns);
            Assert.Empty(fixture.Cloud.Discoveries);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "service-connections.json")));
        }) ?? throw new InvalidOperationException());
        Assert.Equal("fixture@example.com", login.GetProperty("account").GetProperty("username").GetString());
        Assert.Equal([("fixture@example.com", "GlobalCloud")], fixture.Cloud.SignIns);
        Assert.Equal(["fixture@example.com"], fixture.Cloud.Discoveries);
        var rememberedLogin = Assert.Single(await new LocalPowerBiLoginStore(fixture.Root).LoadAsync(default));
        Assert.Equal("fixture@example.com", rememberedLogin.Email);
        Assert.Equal("GlobalCloud", rememberedLogin.EnvironmentName);
        await fixture.Invoke("powerbi.serviceModels", new { });
        var connected = Assert.IsType<ProjectSnapshot>(await fixture.Write("project.connectService", new { modelId = "fixture-handle" }, () =>
        {
            Assert.Empty(fixture.Reader.ServiceModels);
            Assert.Empty(fixture.Reader.Targets);
        }));
        Assert.Equal(["fixture-handle"], fixture.Reader.ServiceModels);
        Assert.Equal("powerbi-service", connected.Connection!.Kind);
        Assert.Equal(Json(connected).GetRawText(), Json(await fixture.Invoke("project.inspect", new { }) ?? throw new InvalidOperationException()).GetRawText());
        var recent = Assert.Single(await new LocalPowerBiRecentModelStore(fixture.Root).LoadAsync("isolated-account-id", "GlobalCloud", default));
        Assert.Equal("workspace-id", recent.WorkspaceId);
        Assert.Equal("model-id", recent.ModelId);
        var visible = Assert.IsType<RecentPowerBiServiceModels>(await fixture.Invoke("powerbi.serviceRecentModels", new { }));
        Assert.Equal("fixture-handle", Assert.Single(visible.Items).ModelId);

        await fixture.ServiceRecent.SaveAsync("other-account", "GlobalCloud", "workspace-id", "model-id", default);
        await fixture.ServiceRecent.SaveAsync("isolated-account-id", "ChinaCloud", "workspace-id", "model-id", default);
        var recentFile = Path.Combine(fixture.Root, "service-recent-models.json");
        var beforeRecent = await File.ReadAllTextAsync(recentFile);
        var deletedModel = Assert.IsType<RecentRecordDeletionResult>(await fixture.Write("powerbi.serviceDeleteRecentModel", new { id = recent.Id }, () =>
        {
            Assert.Equal(beforeRecent, File.ReadAllText(recentFile));
            Assert.Single(fixture.Reader.Targets);
        }));
        Assert.True(deletedModel.Deleted);
        Assert.Empty(await fixture.ServiceRecent.LoadAsync("isolated-account-id", "GlobalCloud", default));
        Assert.Single(await fixture.ServiceRecent.LoadAsync("other-account", "GlobalCloud", default));
        Assert.Single(await fixture.ServiceRecent.LoadAsync("isolated-account-id", "ChinaCloud", default));
        Assert.Single(fixture.Reader.Targets);

        var loginFile = Path.Combine(fixture.Root, "service-connections.json");
        var beforeLogin = await File.ReadAllTextAsync(loginFile);
        var deletedLogin = Json(await fixture.Write("powerbi.serviceDeleteLogin", new { id = rememberedLogin.Id }, () =>
        {
            Assert.Equal(beforeLogin, File.ReadAllText(loginFile));
            Assert.NotNull(fixture.Cloud.Account);
        }) ?? throw new InvalidOperationException());
        Assert.Empty(deletedLogin.GetProperty("recentLogins").EnumerateArray());
        Assert.Empty(await new LocalPowerBiLoginStore(fixture.Root).LoadAsync(default));
        Assert.NotNull(fixture.Cloud.Account);

        fixture.Cloud.Committed = () => fixture.RequireCommitStarted("powerbi.serviceSignOut");
        var signedOut = Json(await fixture.Write("powerbi.serviceSignOut", new { }, () =>
        {
            Assert.NotNull(fixture.Cloud.Account);
            Assert.Equal(0, fixture.Cloud.SignOuts);
            Assert.Single(fixture.Reader.Targets);
        }, 1) ?? throw new InvalidOperationException());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, signedOut.GetProperty("account").ValueKind);
        Assert.Null(fixture.Cloud.Account);
        Assert.Equal(1, fixture.Cloud.SignOuts);
        Assert.Empty(fixture.Reader.Targets);
        Assert.Equal("PROJECT_NOT_SELECTED", (await Assert.ThrowsAsync<AppException>(() => fixture.Invoke("project.inspect", new { }))).Code);
    }
}
