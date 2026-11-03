// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteConnectionsSettingsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP 设置、日志、学习及远端历史维护的真实隔离副作用和持久化边界.

using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Connections;
using PbiUtils.Application.Settings;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Security;
using PbiUtils.Application;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;
using static PbiUtils.UnitTests.McpWriteConnectionsFixture;

namespace PbiUtils.UnitTests;

/// <summary>本地设置及 DPAPI 文件真实执行, 系统目录打开和浏览器只采用受控适配器.</summary>
public sealed class McpWriteConnectionsSettingsTests
{
    /// <summary>保存全部界面偏好后从新存储实例回读, 调用之前保持默认设置和无设置文件.</summary>
    [Fact]
    public async Task UiSettingsPersistExactPatchWithoutConfirmation()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var result = Assert.IsType<UiSettings>(await fixture.Write("ui.saveSettings",
            new { locale = "en-us", showRecentFiles = false, daxFormatStyle = "short" }, () =>
            {
                Assert.Equal(new UiSettings(), fixture.Settings.Read());
                Assert.False(File.Exists(Path.Combine(fixture.Root, "ui-settings.json")));
            }));
        Assert.Equal(new UiSettings("en-us", false, "short"), result);
        Assert.Equal(result, new LocalUiSettingsStore(fixture.Root).Read());
        Assert.Equal(result, await fixture.Invoke("ui.settings", new { }));
    }

    /// <summary>日志真实持久化后重建运行时验证, 打开目录只传宿主确定的隔离日志目录.</summary>
    [Fact]
    public async Task LoggingSaveAndOpenDirectoryUsePersistedSettingsAndOwnedDirectory()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var saved = Assert.IsType<LoggingView>(await fixture.Write("logging.save", new { level = "Debug" }, () =>
        {
            Assert.Equal("Information", fixture.Logging.Read().Level);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "logging-settings.json")));
        }));
        Assert.Equal("Debug", saved.Level);
        Assert.Equal(Path.Combine(fixture.Root, "logs"), saved.Directory);
        Assert.Equal(saved, await fixture.Invoke("logging.settings", new { }));
        using (var reread = new LoggingRuntime(fixture.Root, "test")) Assert.Equal("Debug", reread.Read().Level);
        var opened = Json(await fixture.Write("logging.openDirectory", new { }, () =>
            Assert.Empty(fixture.OpenedLogDirectories)) ?? throw new InvalidOperationException());
        Assert.True(opened.GetProperty("opened").GetBoolean());
        Assert.Equal([saved.Directory], fixture.OpenedLogDirectories);
    }

    /// <summary>学习网站只有 commit_started 落盘后才调用固定宿主打开入口.</summary>
    [Fact]
    public async Task LearningOpenInvokesOwnedBrowserAdapterWithoutConfirmation()
    {
        await using var fixture = new McpWriteConnectionsFixture();
        var result = Json(await fixture.Write("learning.open", new { }, () =>
            Assert.Equal(0, fixture.LearningOpens)) ?? throw new InvalidOperationException());
        Assert.True(result.GetProperty("opened").GetBoolean());
        Assert.Equal(1, fixture.LearningOpens);
        Assert.Empty(fixture.Reader.Targets);
    }

    /// <summary>删除或清除密码针对本地展示记录, 真实 DPAPI 只保护测试密码, 不调用网络或用户账号.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("clearPassword")]
    public async Task RemoteMutationPersistsSelectedRecordChangeWithoutConfirmation(string action)
    {
        await using var fixture = new McpWriteConnectionsFixture();
        using var secret = new SecureString();
        foreach (var character in "isolated-test-secret") secret.AppendChar(character);
        using var address = new RemoteModelAddress("fixture-server", "fixture-model", new RemoteWindowsCredential("fixture-domain\\fixture-user", secret));
        var selectedId = fixture.Remote.Save(address, true, default);
        using var otherAddress = new RemoteModelAddress("other-fixture-server", "other-fixture-model");
        var otherId = fixture.Remote.Save(otherAddress, false, default);
        var form = Assert.IsType<RemoteConnectionFormView>(await fixture.Invoke("remote.form", new { }));
        var parameters = new { formId = form.FormId, action, recordId = selectedId };
        var mutated = Assert.IsType<RemoteConnectionFormView>(await fixture.Write("remote.mutate", parameters, () =>
        {
            var before = fixture.Remote.Load();
            Assert.Equal(2, before.Count);
            Assert.True(Assert.Single(before, item => item.Id == selectedId).HasSavedPassword);
        }));
        Assert.NotEqual(form.FormId, mutated.FormId);
        var reread = new LocalRemoteConnectionStore(fixture.Root);
        var stored = reread.Load();
        Assert.Equal(stored.ToArray(), mutated.Recent.ToArray());
        Assert.Contains(stored, item => item.Id == otherId);
        if (action == "delete")
        {
            Assert.Single(stored);
            Assert.DoesNotContain(stored, item => item.Id == selectedId);
        }
        else
        {
            Assert.Equal(2, stored.Count);
            Assert.False(Assert.Single(stored, item => item.Id == selectedId).HasSavedPassword);
            Assert.Null(reread.ReadCredential(selectedId));
        }
        Assert.Empty(fixture.Reader.RemoteAddresses);
        Assert.DoesNotContain("isolated-test-secret", File.ReadAllText(Path.Combine(fixture.Root, "remote-connections.json")));
        Assert.DoesNotContain("isolated-test-secret", string.Join('\n', fixture.Messages.Select(item => item.GetRawText())));
    }
}
