// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteConnectionsFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为 MCP 连接正向测试提供独立用户目录、真实存储与受控外部适配边界.

using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Execution;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>每个测试独占临时目录, 真实桥接和存储不连接用户模型、账号或外部进程.</summary>
internal sealed class McpWriteConnectionsFixture : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pbi-mcp-write-connections-" + Guid.NewGuid().ToString("N"));
    internal string ProjectFile { get; }
    internal ConcurrentQueue<JsonElement> Messages { get; } = new();
    internal McpWriteConnectionsReader Reader { get; } = new();
    internal McpWriteConnectionsCloud Cloud { get; } = new();
    internal LocalRecentProjects Recent { get; }
    internal LocalPowerBiLoginStore Logins { get; }
    internal LocalPowerBiRecentModelStore ServiceRecent { get; }
    internal LocalRemoteConnectionStore Remote { get; }
    internal LocalUiSettingsStore Settings { get; }
    internal LoggingRuntime Logging { get; }
    internal ExecutionJournal Journal { get; }
    internal UiDialogBroker Dialogs { get; }
    internal BridgeSession Session { get; }
    internal int FileSelections { get; private set; }
    internal List<string> OpenedLogDirectories { get; } = [];
    internal int LearningOpens { get; private set; }

    /// <summary>所有持久化依赖显式绑定新临时根, 浏览器与选择器只记录调用.</summary>
    internal McpWriteConnectionsFixture()
    {
        Directory.CreateDirectory(Root);
        ProjectFile = Path.Combine(Root, "fixture.pbip");
        File.WriteAllText(ProjectFile, "isolated-file-picker-sentinel");
        Recent = new(Root);
        Logins = new(Root);
        ServiceRecent = new(Root);
        Remote = new(Root);
        Settings = new(Root);
        Logging = new(Root, "test");
        Journal = new(Root);
        Dialogs = new(Send);
        Session = new(new(false, null), new(new RuntimeEnvironment()), Send, "test",
            projects: new(Reader), connections: new(Reader, localFiles: Reader),
            selectProject: token =>
            {
                token.ThrowIfCancellationRequested();
                FileSelections++;
                return Task.FromResult<string?>(ProjectFile);
            },
            recentProjects: Recent, remoteForm: new(Remote),
            cloudConnection: new(Cloud, loginStore: Logins, recentModelStore: ServiceRecent),
            uiSettings: new(Settings), logging: new(Logging),
            openLogDirectory: _ =>
            {
                Assert.True(Directory.Exists(Logging.DirectoryPath));
                RequireCommitStarted("logging.openDirectory");
                OpenedLogDirectories.Add(Logging.DirectoryPath);
                return Task.CompletedTask;
            },
            openLearningSite: _ =>
            {
                RequireCommitStarted("learning.open");
                LearningOpens++;
                return Task.CompletedTask;
            },
            dialogs: Dialogs, mcpJournal: Journal);
    }

    /// <summary>只把真实宿主消息解析为可断言数据, 不合成产品回复.</summary>
    private void Send(string message) => Messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message));

    /// <summary>序列化测试输入, 保持与真实 MCP 参数相同的 JSON 字段.</summary>
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, WebJson);

    /// <summary>调用正式 MCP 分发, 连接代际由测试显式指定.</summary>
    internal Task<object?> Invoke(string method, object parameters, long? revision = null) =>
        Session.InvokeMcpAsync(method, Json(parameters), default, revision);

    /// <summary>先核对调用前状态, 直接执行精确请求并验证无通用弹窗与真实持久回执.</summary>
    internal async Task<object?> Write(string method, object parameters, Action beforeWrite, long? revision = null)
    {
        var oldPromptCount = Messages.Count(item => item.TryGetProperty("uiDialog", out _));
        beforeWrite();
        var result = await Invoke(method, parameters, revision).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(oldPromptCount, Messages.Count(item => item.TryGetProperty("uiDialog", out _)));
        AssertReceipt(method, parameters);
        return result;
    }

    /// <summary>通过重新创建的账本读取确定提交, 参数摘要绑定本次方法和原始输入.</summary>
    internal void AssertReceipt(string method, object parameters)
    {
        var record = Assert.Single(new ExecutionJournal(Root).List(), item => item.Record.Method == method).Record;
        Assert.Equal("completed", record.State);
        Assert.True(record.CommitStarted);
        Assert.Null(record.ErrorCode);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + Json(parameters).GetRawText()));
        Assert.Equal(Convert.ToHexStringLower(digest), record.RequestDigest);
        Assert.Equal(0, Session.PendingCount);
    }

    /// <summary>外部适配器边界直接读取落盘账本, 验证副作用之前已经可靠记录 commit_started.</summary>
    internal void RequireCommitStarted(string method)
    {
        var records = Directory.EnumerateFiles(Path.Combine(Root, "mcp", "executions"), "*.json")
            .Select(file => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file)))
            .Where(record => record.GetProperty("method").GetString() == method).ToArray();
        Assert.Equal("commit_started", Assert.Single(records).GetProperty("state").GetString());
    }

    /// <summary>仅可信本地网页通道提交表单, MCP 远端直连不复用此表单通道.</summary>
    internal async Task<JsonElement> LocalCall(string method, object parameters)
    {
        var id = Guid.NewGuid().ToString("N");
        await Session.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));
        var response = Assert.Single(Messages, item => item.TryGetProperty("id", out var value) && value.GetString() == id);
        Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
        return response.GetProperty("result");
    }

    /// <summary>先排空宿主及日志句柄, 只清理本夹具创建的唯一临时目录.</summary>
    public async ValueTask DisposeAsync()
    {
        await Session.ShutdownAsync();
        Dialogs.Dispose();
        Logging.Dispose();
        Directory.Delete(Root, true);
    }
}
