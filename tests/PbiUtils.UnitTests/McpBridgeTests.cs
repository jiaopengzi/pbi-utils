// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证本机 MCP 复用实际桥接校验、静默写入、取消及独立回执, 不访问用户模型.

using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Execution;
using PbiUtils.Application.Models.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class McpBridgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-bridge-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>无模型或非 Service 模型时, 账户登出不需要或更新当前模型代际, 无需额外确认.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceSignOutWithoutServiceModelDoesNotRequireConnectionObservation(bool connected)
    {
        var target = new ModelTarget(root, false);
        var cloud = new SignOutCloud();
        List<JsonElement> messages = [];
        using var dialogs = new UiDialogBroker(text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)));
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(target), selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(),
            cloudConnection: new(cloud), dialogs: dialogs, mcpJournal: new(root));
        if (connected)
            await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        List<long> observations = [];
        var pending = session.InvokeMcpAsync("powerbi.serviceSignOut", Json(new { }), default, observeConnectionRevision: observations.Add);
        await pending;
        Assert.Empty(messages);
        Assert.Equal(1, cloud.SignOuts);
        Assert.Null(cloud.Account);
        Assert.Empty(observations);
        if (connected) Assert.IsType<ProjectSnapshot>(await session.InvokeMcpAsync("project.inspect", Json(new { }), default));
    }

    /// <summary>已连接 Service 时缺失或过期代际均在弹窗和退出账户之前拒绝, 新鲜代际直接执行.</summary>
    [Fact]
    public async Task ServiceSignOutWithServiceModelRequiresObservedRevision()
    {
        var target = new ModelTarget(root, false);
        target.Snapshot = target.Snapshot with { Connection = new("powerbi-service", "service") };
        var cloud = new SignOutCloud();
        List<JsonElement> messages = [];
        using var dialogs = new UiDialogBroker(text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)));
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(target), selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(),
            cloudConnection: new(cloud), dialogs: dialogs, mcpJournal: new(root));
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        foreach (long? revision in new long?[] { null, 0 })
            Assert.Equal("STALE_CONNECTION", (await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("powerbi.serviceSignOut", Json(new { }), default, revision))).Code);
        Assert.Empty(messages);
        Assert.Equal(0, cloud.SignOuts);
        List<long> observations = [];
        var pending = session.InvokeMcpAsync("powerbi.serviceSignOut", Json(new { }), default, 1, observations.Add);
        await pending;
        Assert.Empty(messages);
        Assert.Equal(1, cloud.SignOuts);
        Assert.Equal([2L], observations);
        Assert.Equal("PROJECT_NOT_SELECTED", (await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("project.inspect", Json(new { }), default))).Code);
    }

    /// <summary>静默账户登出持有共享写准入, 读取期间不能插入另一 GUI 连接切换.</summary>
    [Fact]
    public async Task ServiceSignOutRetainsProjectGateUntilCompletion()
    {
        var target = new ModelTarget(root, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new SignOutCloud { BeforeCommit = async () => { entered.TrySetResult(); await release.Task; } };
        List<JsonElement> messages = [];
        var journal = new ExecutionJournal(root);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)), "test",
            projects: new(target), selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(),
            cloudConnection: new(cloud), mcpJournal: journal);
        var pending = session.InvokeMcpAsync("powerbi.serviceSignOut", Json(new { }), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
            var response = Assert.Single(messages, item => item.TryGetProperty("id", out var id) && id.GetString() == "connect");
            Assert.Equal("USER_DATA_BUSY", response.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, cloud.SignOuts); Assert.NotNull(cloud.Account);
            Assert.DoesNotContain(messages, item => item.TryGetProperty("uiDialog", out _));
        }
        finally { release.TrySetResult(); }
        await pending;
        Assert.Equal(1, cloud.SignOuts);
        Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "powerbi.serviceSignOut").Record.State);
        Assert.DoesNotContain(journal.List(), item => item.Record.Method == "project.open");
    }

    /// <summary>未观察当前目标代际的 MCP 模型写请求必须在授权与提交前拒绝.</summary>
    [Fact]
    public async Task ModelWriteWithoutObservedRevisionIsRejectedBeforeCommit()
    {
        var target = new ModelTarget(root, false);
        using var edits = new ModelEditService(target, target);
        var journal = new ExecutionJournal(root);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>("fixture.pbip"),
            connections: new TestLocalFileConnection(target).Service(), mcpJournal: journal);
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("model.edit",
            Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" }), default));
        Assert.Equal("STALE_CONNECTION", error.Code);
        Assert.Equal(0, target.Writes);
        Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "project.open").Record.State);
        Assert.DoesNotContain(journal.List(), item => item.Record.Method == "model.edit");
    }

    /// <summary>GUI 同名重连后旧 MCP 观察不能写入新目标; 只有重新读取后才能授权提交.</summary>
    [Fact]
    public async Task GuiReconnectRejectsOldMcpTargetUntilInspectObservesNewRevision()
    {
        var target = new ModelTarget(root, false);
        using var edits = new ModelEditService(target, target);
        var journal = new ExecutionJournal(root);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>("fixture.pbip"),
            connections: new TestLocalFileConnection(target).Service(), mcpJournal: journal);
        Task Connect(string id, long revision) => session.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id, method = "project.open", @params = new { }, connectionRevision = revision }));
        List<long> observations = [];
        await Connect("initial", 0);
        await session.InvokeMcpAsync("project.inspect", Json(new { }), default, observeConnectionRevision: observations.Add);
        Assert.Equal([1L], observations);
        await Connect("reconnect", 1);
        var request = Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" });
        var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("model.edit", request, default, observations.Last(), observations.Add));
        Assert.Equal("STALE_CONNECTION", error.Code);
        Assert.Equal(0, target.Writes);
        Assert.Equal(2, journal.List().Count(item => item.Record.Method == "project.open" && item.Record.State == "completed"));
        Assert.DoesNotContain(journal.List(), item => item.Record.Method == "model.edit");
        Assert.Equal([1L], observations);
        await session.InvokeMcpAsync("mcp.configuration", Json(new { }), default, observations.Last(), observations.Add);
        Assert.Equal([1L], observations);
        await session.InvokeMcpAsync("project.inspect", Json(new { }), default, observations.Last(), observations.Add);
        Assert.Equal([1L, 2L], observations);
        var result = Assert.IsType<ProjectSnapshot>(await session.InvokeMcpAsync("model.edit", request, default, observations.Last(), observations.Add));
        Assert.Equal("after", result.Model.Tables[0].Description);
        Assert.Equal([1L, 2L, 3L], observations);
        Assert.Equal(1, target.Writes);
        Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "model.edit").Record.State);
    }

    /// <summary>独立 created 配置成功读取不得更新当前模型观察, current 配置才返回当前代际.</summary>
    [Fact]
    public async Task CreatedConfigurationDoesNotObserveCurrentConnection()
    {
        var file = PbipFixture.Create(root);
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(file, default);
        using var workspace = new ChangeWorkspace();
        using var compiler = new ModelConfigurationCompiler();
        using var configuration = new ModelConfigurationBridge(reader, workspace, compiler);
        configuration.BindCreated(new("plan", "published", true, true, false, file, snapshot.BaselineId, [], []));
        using var edits = new ModelEditService(reader, workspace);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>(file),
            connections: new TestLocalFileConnection(reader).Service(), configuration: configuration);
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        List<long> observations = [];
        Assert.IsType<ModelConfigurationContext>(await session.InvokeMcpAsync("configuration.context", Json(new { target = "created" }), default,
            observeConnectionRevision: observations.Add));
        Assert.Empty(observations);
        var current = Assert.IsType<ModelConfigurationContext>(await session.InvokeMcpAsync("configuration.context", Json(new { target = "current" }), default,
            observeConnectionRevision: observations.Add));
        Assert.Equal([1L], observations);
        foreach (var method in new[] { "configuration.apply", "configuration.complete", "configuration.discard" })
        {
            foreach (var targetId in new[] { current.TargetId, "created-forged" })
            {
                var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync(method,
                    Json(new { targetId, baselineId = current.Project.BaselineId }), default));
                Assert.Equal("STALE_CONNECTION", error.Code);
            }
        }
    }

    /// <summary>宿主核验的 created 目标可在无连接观察时写入真实临时工程, 不刷新当前连接观察.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedConfigurationWritesWithoutCurrentConnectionRevision(bool sharedCurrent)
    {
        Directory.CreateDirectory(root);
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", null, null, null, null, null), project, project, null, default);
        var file = Path.Combine(project, composition.EntryFile);
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(file, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler();
        using var configuration = new ModelConfigurationBridge(reader, workspace, compiler);
        configuration.BindCreated(new("plan", "published", true, true, false, file, snapshot.BaselineId, [], []));
        using var edits = new ModelEditService(reader, workspace);
        var journal = new ExecutionJournal(Path.Combine(root, "receipts"));
        List<JsonElement> messages = [];
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)), "test",
            projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>(file),
            connections: new TestLocalFileConnection(reader).Service(), configuration: configuration, mcpJournal: journal);
        if (sharedCurrent)
            await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        List<long> observations = [];
        var context = Assert.IsType<ModelConfigurationContext>(await session.InvokeMcpAsync("configuration.context", Json(new { target = "created" }), default,
            observeConnectionRevision: observations.Add));
        var request = JsonSerializer.SerializeToElement(new
        {
            context.TargetId,
            context.Project.BaselineId,
            Section = "measures",
            Request = new ModelConfigurationRequest(Measures: [new("RevisionFixture", "value", "Uncategorized", true, "", "")], MeasureConfigurationRevision: context.MeasureConfigurationRevision)
        }, JsonSerializerOptions.Web);
        var saved = Assert.IsType<ModelConfigurationContext>(await session.InvokeMcpAsync("configuration.apply", request, default,
            observeConnectionRevision: observations.Add));
        Assert.Empty(observations);
        Assert.Contains((await reader.ReadAsync(file, default)).Model.Measures, measure => measure.Name == "RevisionFixture");
        Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "configuration.apply").Record.State);
        foreach (var method in new[] { "configuration.complete", "configuration.discard" })
        {
            var recovery = session.InvokeMcpAsync(method, Json(new { targetId = saved.TargetId, baselineId = saved.Project.BaselineId }), default,
                observeConnectionRevision: observations.Add);
            // 已保存工程没有待保存内容, complete 保留此业务拒绝; discard 继续回读独立目标.
            if (method == "configuration.complete")
                Assert.Equal("NO_PENDING_CHANGES", (await Assert.ThrowsAsync<AppException>(() => recovery)).Code);
            else Assert.Equal(saved.TargetId, Assert.IsType<ModelConfigurationContext>(await recovery).TargetId);
        }
        Assert.Empty(observations);
        if (sharedCurrent) Assert.Contains(messages, message => message.TryGetProperty("workspaceChanged", out var change) && change.GetProperty("revision").GetInt64() == 2);
        else Assert.DoesNotContain(messages, message => message.TryGetProperty("workspaceChanged", out _));
    }

    /// <summary>未知或伪造 created 句柄不能豁免当前连接代际校验.</summary>
    [Theory]
    [InlineData("configuration.apply")]
    [InlineData("configuration.complete")]
    [InlineData("configuration.discard")]
    public async Task UnknownConfigurationTargetCannotBypassConnectionRevision(string method)
    {
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test");
        var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync(method,
            Json(new { targetId = "created-forged", baselineId = "baseline" }), default));
        Assert.Equal("STALE_CONNECTION", error.Code);
    }

    /// <summary>读取失败不能推进观察, 即使该方法通常可以采用当前目标.</summary>
    [Fact]
    public async Task FailedInspectDoesNotObserveConnection()
    {
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test", projects: new(new ModelTarget(root, false)));
        List<long> observations = [];
        var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("project.inspect", Json(new { }), default,
            observeConnectionRevision: observations.Add));
        Assert.Equal("PROJECT_NOT_SELECTED", error.Code);
        Assert.Empty(observations);
    }

    /// <summary>完成事件中的并发重连仍受回执准入保护; 原结果保留自身代际, 明确后续重连才能推进.</summary>
    [Fact]
    public async Task ModelResultKeepsItsRevisionWhenReconnectPrecedesReceiptRelease()
    {
        var target = new ModelTarget(root, false);
        using var edits = new ModelEditService(target, target);
        BridgeSession? activeSession = null;
        Task? reconnect = null;
        JsonElement? reconnectResponse = null;
        void Send(string text)
        {
            var message = JsonSerializer.Deserialize<JsonElement>(text);
            if (message.TryGetProperty("id", out var id) && id.GetString() == "reconnect") reconnectResponse = message;
            if (message.TryGetProperty("workspaceChanged", out var change) && change.GetProperty("revision").GetInt64() == 2)
                reconnect = activeSession!.ReceiveAsync(OriginPolicy.Production,
                    JsonSerializer.Serialize(new { protocolVersion = 1, id = "reconnect", method = "project.open", @params = new { }, connectionRevision = 2 }));
        }
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), Send, "test",
            projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>("fixture.pbip"),
            connections: new TestLocalFileConnection(target).Service(), mcpJournal: new(root));
        activeSession = session;
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        List<long> observations = [];
        await session.InvokeMcpAsync("model.edit",
            Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" }), default, 1, observations.Add);
        await Assert.IsType<Task>(reconnect, exactMatch: false);
        Assert.Equal("USER_DATA_BUSY", reconnectResponse!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal([2L], observations);
        await session.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id = "retry-after-completion", method = "project.open", @params = new { }, connectionRevision = 2 }));
        await session.InvokeMcpAsync("project.inspect", Json(new { }), default, observeConnectionRevision: observations.Add);
        Assert.Equal([2L, 3L], observations);
    }

    /// <summary>隐藏宿主需要独立人工交互时立即拒绝, 不发送无法完成的弹窗或伪造确认.</summary>
    [Fact]
    public async Task BackgroundDialogsFailImmediatelyWithoutEmittingUi()
    {
        using var dialogs = new UiDialogBroker(_ => throw new InvalidOperationException("Hidden dialog emitted"), () => false);
        var error = await Assert.ThrowsAsync<AppException>(() => dialogs.ShowAsync(new("confirm", "title"), default));
        Assert.Equal("MCP_INTERACTION_REQUIRED", error.Code);
    }

    /// <summary>已通过 MCP 写入口的模型请求直接签发精确授权, 不依赖 Vue 弹窗或额外确认回调.</summary>
    [Fact]
    public async Task ModelWriteCompletesWithoutInteractiveAuthorization()
    {
        var target = new ModelTarget(root, false);
        using var edits = new ModelEditService(target, target);
        var journal = new ExecutionJournal(root);
        List<JsonElement> messages = [];
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)), "test", projects: new(edits), edits: edits,
            selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(), mcpJournal: journal);
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        var result = Assert.IsType<ProjectSnapshot>(await session.InvokeMcpAsync("model.edit",
            Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" }), default, 1));
        Assert.Equal("after", result.Model.Tables[0].Description);
        Assert.Equal(1, target.Writes);
        Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "model.edit").Record.State);
        Assert.DoesNotContain(messages, message => message.TryGetProperty("uiDialog", out _));
    }

    /// <summary>第三方不能伪造弹窗回答、取消其他请求、清空数据或输入凭据.</summary>
    [Theory]
    [InlineData("ui.dialogRespond")]
    [InlineData("request.cancel")]
    [InlineData("app.resetUserData")]
    [InlineData("learning.update")]
    [InlineData("ai.settings")]
    public async Task ControlChannelsAreNeverCallable(string method)
    {
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => throw new InvalidOperationException("Unexpected UI response"), "test");
        Assert.Equal("MCP_METHOD_NOT_ALLOWED", (await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync(method, Json(new { }), default))).Code);
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>配置读取仅提供允许修改的接入命令, 不改变调用方权限或产生网页响应与落盘写入.</summary>
    [Fact]
    public async Task ReadOnlyCallsReturnActualDataWithoutWebResponseOrJournal()
    {
        List<string> webResponses = [];
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), webResponses.Add, "test");
        var result = Json(await session.InvokeMcpAsync("mcp.configuration", Json(new { }), default) ?? throw new InvalidOperationException());
        Assert.False(result.GetProperty("isSimulation").GetBoolean());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "pbi-utils.exe"), result.GetProperty("executablePath").GetString());
        Assert.False(result.TryGetProperty("readOnly", out _));
        var launch = result.GetProperty("readWrite");
        Assert.Equal(result.GetProperty("executablePath").GetString(), launch.GetProperty("command").GetString());
        Assert.Equal(["mcp", "serve", "--allow-write"], launch.GetProperty("args").EnumerateArray().Select(value => value.GetString()));
        Assert.Empty(webResponses);
        Assert.Equal(0, session.PendingCount);
        Assert.False(Directory.Exists(root));
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("doctor", Json(new { extra = true }), default))).Code);
    }

    /// <summary>精确设置直接写入且无对话框依赖, 完成账本先于结果返回.</summary>
    [Fact]
    public async Task SettingsWriteCompletesWithoutUiAndPersistsReceipt()
    {
        var settings = new LocalUiSettingsStore(root);
        var journal = new ExecutionJournal(root);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => throw new InvalidOperationException("Unexpected IPC response on web"), "test",
            uiSettings: new(settings), mcpJournal: journal);
        Assert.Equal("zh-cn", settings.Read().Locale);
        await session.InvokeMcpAsync("ui.saveSettings", Json(new { locale = "en-us" }), default);
        Assert.Equal("en-us", settings.Read().Locale);
        var record = Assert.Single(journal.List()).Record;
        Assert.Equal("completed", record.State); Assert.True(record.CommitStarted); Assert.Equal(0, session.PendingCount);
    }

    /// <summary>已取消的请求不进入直接写入, 既有设置与回执保持不变.</summary>
    [Fact]
    public async Task PrecancelledSettingsWriteLeavesNoSideEffects()
    {
        var settings = new LocalUiSettingsStore(root);
        var journal = new ExecutionJournal(root);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => throw new InvalidOperationException("Unexpected UI"), "test", uiSettings: new(settings), mcpJournal: journal);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeMcpAsync("ui.saveSettings", Json(new { locale = "en-us" }), cancellation.Token));
        Assert.Equal("zh-cn", settings.Read().Locale); Assert.Empty(journal.List()); Assert.Equal(0, session.PendingCount);
    }

    /// <summary>现有协议大小边界不能被本机入口绕过, 超限在任何确认和执行之前失败.</summary>
    [Fact]
    public async Task OversizedRequestIsRejectedByTheSameEnvelopeBudget()
    {
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => throw new InvalidOperationException(), "test");
        var error = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("dax.format", Json(new { value = new string('x', 70000), style = "long" }), default));
        Assert.Equal("MESSAGE_TOO_LARGE", error.Code);
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>MCP 模型编辑必须经过真实前后差异确认, 拒绝或过期均零提交, 未知提交跨调用阻止重放.</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("stale")]
    [InlineData("unknown")]
    public async Task ModelWritesUseExactAuthorizationAndDurableCommitBoundary(string scenario)
    {
        var target = new ModelTarget(root, scenario == "unknown");
        using var edits = new ModelEditService(target, target);
        var journal = new ExecutionJournal(root);
        var confirmations = 0;
        List<JsonElement> messages = [];
        var policy = new ToolWritePolicy((proposal, _) =>
        {
            confirmations++;
            var change = Assert.Single(proposal.Changes);
            Assert.Equal("before", change.Before);
            Assert.Equal("after", change.After);
            Assert.Equal(0, target.Writes);
            if (scenario == "stale") target.Snapshot = target.Snapshot with { BaselineId = "changed" };
            return Task.FromResult(scenario != "deny");
        });
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), text => messages.Add(JsonSerializer.Deserialize<JsonElement>(text)), "test", projects: new(edits), edits: edits,
            selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(), toolWrites: policy, mcpJournal: journal);
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        var request = Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" });
        if (scenario == "approve")
        {
            var result = Assert.IsType<ProjectSnapshot>(await session.InvokeMcpAsync("model.edit", request, default, 1));
            Assert.Equal("after", result.Model.Tables[0].Description);
            Assert.Equal("completed", Assert.Single(journal.List(), item => item.Record.Method == "model.edit").Record.State);
            var change = messages.Last().GetProperty("workspaceChanged");
            Assert.Equal(2, change.GetProperty("revision").GetInt64());
            Assert.Equal("after", change.GetProperty("project").GetProperty("model").GetProperty("tables")[0].GetProperty("description").GetString());
        }
        else
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("model.edit", request, default, 1));
            Assert.Equal(scenario == "deny" ? "CANCELLED" : scenario == "stale" ? "WRITE_AUTHORIZATION_INVALID" : "EXECUTION_FAILED", failure.Code);
            var editReceipt = Assert.Single(journal.List(), item => item.Record.Method == "model.edit").Record;
            Assert.Equal(scenario == "unknown" ? "needs_verification" : "not_committed", editReceipt.State);
            Assert.Equal(scenario == "unknown", editReceipt.CommitStarted);
            if (scenario == "unknown")
                Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => session.InvokeMcpAsync("model.edit", request, default, 1))).Code);
        }
        Assert.Equal(1, confirmations);
        Assert.Equal(scenario is "approve" or "unknown" ? 1 : 0, target.Writes);
    }

    /// <summary>外部切换即使使用相同名称和路径仍增加代际, 旧网页修改不能落到新的当前目标.</summary>
    [Fact]
    public async Task ExternalReconnectRejectsOldGuiDraftsUntilNewRevisionIsExplicitlyAdopted()
    {
        var target = new ModelTarget(root, false);
        using var edits = new ModelEditService(target, target);
        ConcurrentQueue<JsonElement> messages = new();
        void Send(string text) => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text));
        using var dialogs = new UiDialogBroker(Send);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), Send, "test", projects: new(edits), edits: edits,
            selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(), dialogs: dialogs, mcpJournal: new(root));
        Task Call(string id, string method, object parameters, long revision) => session.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters, connectionRevision = revision }));
        JsonElement Response(string id) => Assert.Single(messages, item => item.TryGetProperty("id", out var value) && value.GetString() == id);
        await Call("initial", "project.open", new { }, 0);
        Assert.Equal(1, Response("initial").GetProperty("connectionRevision").GetInt64());
        List<long> observations = [];
        var reconnect = session.InvokeMcpAsync("project.open", Json(new { }), default, observeConnectionRevision: observations.Add);
        await reconnect;
        Assert.DoesNotContain(messages, item => item.TryGetProperty("uiDialog", out _));
        Assert.Equal([2L], observations);
        var change = messages.Last().GetProperty("workspaceChanged");
        Assert.Equal(2, change.GetProperty("revision").GetInt64());
        Assert.Equal("workspace", change.GetProperty("project").GetProperty("projectId").GetString());
        foreach (var method in new[] { "model.edit", "model.save", "model.discard", "configuration.apply", "configuration.complete", "project.disconnect" })
        {
            await Call(method, method, new { }, 1);
            Assert.Equal("STALE_CONNECTION", Response(method).GetProperty("error").GetProperty("code").GetString());
        }
        await Call("fresh", "project.inspect", new { }, 2);
        Assert.True(Response("fresh").TryGetProperty("result", out _));
        Assert.Equal(2, Response("fresh").GetProperty("connectionRevision").GetInt64());
        Assert.Equal(0, target.Writes);
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>最终账本写失败不能吞掉真实保存快照, 保留 commit_started 并拒绝下一次写入.</summary>
    [Fact]
    public async Task ReceiptFailurePreservesActualModelResultAndUnknownCommitBarrier()
    {
        var target = new ModelTarget(root, false) { FailReceipt = true };
        using var edits = new ModelEditService(target, target);
        var journal = new ExecutionJournal(root);
        var logger = new CapturingLogger();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test", projects: new(edits), edits: edits,
            selectProject: _ => Task.FromResult<string?>("fixture.pbip"), connections: new TestLocalFileConnection(target).Service(),
            toolWrites: new((_, _) => Task.FromResult(true)), mcpJournal: journal, logger: logger);
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
        try
        {
            List<long> observations = [];
            var result = Assert.IsType<McpDesktopResult>(await session.InvokeMcpAsync("model.edit", Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "after", table = "", pageId = "" }), default, 1, observations.Add));
            Assert.Empty(observations);
            Assert.True(result.IsError);
            Assert.True(result.NeedsVerification);
            Assert.Equal("RECEIPT_SAVE_FAILED", result.ErrorCode);
            var snapshot = Assert.IsType<ProjectSnapshot>(result.Value);
            Assert.Equal("after", snapshot.Model.Tables[0].Description);
            Assert.Equal("saved", snapshot.BaselineId);
            Assert.Equal("commit_started", Assert.Single(journal.List(), item => item.Record.Method == "model.edit").Record.State);
            Assert.Equal("EXECUTION_NEEDS_VERIFICATION", Assert.Throws<AppException>(journal.RequireResetSafe).Code);
            var terminal = Assert.Single(logger.Events, item => item.GetValueOrDefault("Operation") as string == "Bridge.model.edit"
                && item.GetValueOrDefault("Outcome") as string is not (null or "Started"));
            Assert.Equal("Failed", terminal["Outcome"]);
            Assert.Equal("RECEIPT_SAVE_FAILED", terminal["ErrorCode"]);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "mcp", "executions"), "*.json")) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    /// <summary>只模拟已登录账户, 退出在真实宿主提交回调后计数, 不访问云端.</summary>
    private sealed class SignOutCloud : IPowerBiServiceClient
    {
        public PowerBiAccount? Account { get; private set; } = new("fixture@example.com");
        public int SignOuts { get; private set; }
        public Func<Task>? BeforeCommit { get; init; }
        /// <summary>本夹具不提供环境发现.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => throw new NotSupportedException();
        /// <summary>本夹具已经登录, 不提供登录.</summary>
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) => throw new NotSupportedException();
        /// <summary>本夹具不提供目录访问.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => throw new NotSupportedException();
        /// <summary>本夹具不解析模型令牌.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token) => throw new NotSupportedException();
        /// <summary>遵守取消和提交回调后清理假账户, 记录真实调用次数.</summary>
        public async Task SignOutAsync(CancellationToken token, Action? committing = null)
        {
            if (BeforeCommit is not null) await BeforeCommit();
            token.ThrowIfCancellationRequested();
            committing?.Invoke();
            SignOuts++;
            Account = null;
        }
    }

    /// <summary>捕获真实结构化桥接终态, 不由测试推导或过滤产品的成功/失败分类.</summary>
    private sealed class CapturingLogger : ILogger<BridgeSession>
    {
        internal ConcurrentQueue<Dictionary<string, object?>> Events { get; } = new();
        /// <summary>本测试只核事件字段, 不持有日志作用域资源.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>接收所有真实等级以检查唯一终态.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>直接保留结构化日志字段, 不记录或合成业务正文.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values) Events.Enqueue(values.ToDictionary());
        }
    }

    /// <summary>只模拟模型适配边界, 使用真实编辑服务、授权策略、桥接和本机回执存储.</summary>
    private sealed class ModelTarget(string root, bool failAfterCommit) : IProjectReader, IModelEditWorkspace
    {
        private readonly string directory = root;
        private readonly bool fails = failAfterCommit;
        public ProjectSnapshot Snapshot { get; set; } = new("fixture.pbip", "workspace", "baseline", "TMDL", "PBIR", "1", null,
            new("Model", 1606, "en-US", [new("Sales", "before", false, [], [])], [], [], 0, 0), [], [], [], new("pbip", "PBIP"));
        public int Writes { get; private set; }
        public bool FailReceipt { get; init; }
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(Snapshot);
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new Draft(this, baseline));

        private sealed class Draft(ModelTarget owner, ProjectSnapshot baseline) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = baseline;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            {
                Snapshot = Snapshot with { HasPendingChanges = true, Model = Snapshot.Model with { Tables = [Snapshot.Model.Tables[0] with { Description = request.Value }] } };
                return Task.CompletedTask;
            }
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                committing?.Invoke();
                // GUI连接现在也有可靠回执; 精确找出本次模型写的活跃提交记录, 不放宽为任意历史记录.
                var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(owner.directory, "mcp", "executions"), "*.json"), path =>
                {
                    using var candidate = JsonDocument.Parse(File.ReadAllText(path));
                    return candidate.RootElement.GetProperty("method").GetString() == "model.edit"
                        && candidate.RootElement.GetProperty("state").GetString() == "commit_started";
                });
                using var record = JsonDocument.Parse(File.ReadAllText(file));
                Assert.Equal("commit_started", record.RootElement.GetProperty("state").GetString());
                Assert.Equal(Path.GetFileNameWithoutExtension(file), record.RootElement.GetProperty("executionId").GetString());
                owner.Writes++;
                if (owner.fails) throw new IOException("Controlled unknown commit");
                owner.Snapshot = Snapshot with { HasPendingChanges = false, BaselineId = "saved", SaveResult = new(true, true, false, "saved") };
                if (owner.FailReceipt) File.SetAttributes(file, FileAttributes.ReadOnly);
                return Task.FromResult(owner.Snapshot);
            }
            public void Dispose() { }
        }
    }

    /// <summary>测试只拥有自己的临时设置目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
