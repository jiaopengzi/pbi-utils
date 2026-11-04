// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpRemoteConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 MCP 远端直连仅用当前 Windows 身份, 严格拒绝秘密并保留取消、只读与可靠回执.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Execution;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class McpRemoteConnectionTests
{
    /// <summary>秘密、表单身份和任意额外字段在创建回执或调用模型适配器之前拒绝.</summary>
    [Theory]
    [InlineData("password")]
    [InlineData("values")]
    [InlineData("formId")]
    [InlineData("userName")]
    [InlineData("authenticationMode")]
    [InlineData("remember")]
    public async Task ExternalFieldsNeverReachUiOrJournal(string field)
    {
        await using var fixture = new Fixture();
        var input = new Dictionary<string, string> { ["server"] = "fixture-server", ["database"] = "fixture-model", [field] = "secret-sentinel" };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("project.connectRemote", Json(input), default));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.Empty(fixture.Messages); Assert.Empty(fixture.Journal.List()); Assert.Equal(0, fixture.Reader.Registrations);
    }

    /// <summary>缺失、类型错误、连接串注入及需组织认证的 XMLA 地址均不进入读取或回执.</summary>
    [Theory]
    [InlineData("{}", "VALIDATION_ERROR")]
    [InlineData("{\"server\":null,\"database\":\"db\"}", "VALIDATION_ERROR")]
    [InlineData("{\"server\":\"host;Password=secret\",\"database\":\"db\"}", "INVALID_SERVER")]
    [InlineData("{\"server\":\"host\",\"database\":\" \"}", "INVALID_SERVER")]
    [InlineData("{\"server\":\"powerbi://api.powerbi.com/v1.0/myorg/test\",\"database\":\"db\"}", "INVALID_AUTHENTICATION_MODE")]
    public async Task InvalidTargetsFailBeforeRegistration(string input, string code)
    {
        await using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("project.connectRemote", JsonSerializer.Deserialize<JsonElement>(input), default));
        Assert.Equal(code, error.Code); Assert.Empty(fixture.Journal.List()); Assert.Empty(fixture.Messages); Assert.Equal(0, fixture.Reader.Registrations);
    }

    /// <summary>真实共享连接通道直接发布只读目标, 无表单、秘密或凭据存储, 回执在成功返回前完成.</summary>
    [Fact]
    public async Task CurrentIdentityConnectionPublishesReadOnlyWorkspaceWithoutUi()
    {
        await using var fixture = new Fixture();
        List<long> revisions = [];
        var result = Assert.IsType<ProjectSnapshot>(await fixture.Session.InvokeMcpAsync("project.connectRemote", Parameters(), default, observeConnectionRevision: revisions.Add));
        Assert.Equal("remote-workspace", result.ProjectId); Assert.Equal("ssas-xmla", result.Connection!.Kind);
        Assert.Equal("fixture-server", fixture.Reader.Address!.Server); Assert.Equal("fixture-model", fixture.Reader.Address.Database);
        Assert.Null(fixture.Reader.Address.Credential); Assert.Equal(1, fixture.Reader.ReadCount); Assert.Equal([1L], revisions);
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("uiDialog", out _));
        var record = Assert.Single(fixture.Journal.List()).Record;
        Assert.Equal("completed", record.State); Assert.True(record.CommitStarted);
        Assert.Empty(new LocalRemoteConnectionStore(fixture.Root).Load());
        var readOnly = await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("model.edit",
            Json(new { operation = "table.set", objectName = "Sales", property = "description", value = "changed", table = "" }), default, 1));
        Assert.Equal("READ_ONLY_MODEL", readOnly.Code);
    }

    /// <summary>前置连接失败不发布目标, 撤销临时注册并可靠记录未提交失败.</summary>
    [Fact]
    public async Task ConnectionFailureDoesNotPublishOrLeaveRegistration()
    {
        await using var fixture = new Fixture(); fixture.Reader.Fail = true;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("project.connectRemote", Parameters(), default));
        Assert.Equal("TEST_REMOTE_READ_FAILED", error.Code); Assert.Equal(1, fixture.Reader.Forgets);
        Assert.Equal("not_committed", Assert.Single(fixture.Journal.List()).Record.State);
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("workspaceChanged", out _));
        Assert.Equal("PROJECT_NOT_SELECTED", (await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("project.inspect", Json(new { }), default))).Code);
    }

    /// <summary>客户端取消只在 SDK 边界生效, 原读取排空后才返回, 不把迟到结果发布成新目标.</summary>
    [Fact]
    public async Task CancellationDrainsConnectionBeforeFinishingReceipt()
    {
        await using var fixture = new Fixture(); fixture.Reader.Block = true;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Session.InvokeMcpAsync("project.connectRemote", Parameters(), cancellation.Token);
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync(); await fixture.Reader.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted); fixture.Reader.Release.TrySetResult();
        Assert.Equal("CANCELLED", (await Assert.ThrowsAsync<AppException>(() => pending)).Code);
        Assert.Equal("not_committed", Assert.Single(fixture.Journal.List()).Record.State); Assert.Equal(1, fixture.Reader.Forgets);
        Assert.DoesNotContain(fixture.Messages, item => item.TryGetProperty("workspaceChanged", out _)); Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>当前连接持有可靠回执租约时另一次写请求明确拒绝, 不建立第二个目标.</summary>
    [Fact]
    public async Task ConcurrentConnectionCannotBypassReceiptLease()
    {
        await using var fixture = new Fixture(); fixture.Reader.Block = true;
        var pending = fixture.Session.InvokeMcpAsync("project.connectRemote", Parameters(), default);
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Session.InvokeMcpAsync("project.connectRemote", Parameters(), default));
        Assert.Equal("USER_DATA_BUSY", error.Code); Assert.Equal(1, fixture.Reader.Registrations);
        fixture.Reader.Release.TrySetResult(); await pending;
        Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>按实际 MCP 输入结构序列化合成参数, 不读取用户数据.</summary>
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    /// <summary>返回无秘密的固定 SSAS 测试目标.</summary>
    private static JsonElement Parameters() => Json(new { server = "fixture-server", database = "fixture-model" });

    /// <summary>隔离真实存储、回执与桥接, 仅网络读取使用受控适配器, 不提供对话框或远端表单.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pbi-mcp-remote-" + Guid.NewGuid().ToString("N"));
        internal ConcurrentQueue<JsonElement> Messages { get; } = new();
        internal Reader Reader { get; } = new();
        internal ExecutionJournal Journal { get; }
        internal ModelEditService Edits { get; }
        internal BridgeSession Session { get; }
        /// <summary>组合真实业务与隔离回执, 不提供本地表单或对话框.</summary>
        internal Fixture()
        {
            Journal = new(Root); Edits = new(Reader, Reader);
            Session = new(new(false, null), new(new RuntimeEnvironment()), text => Messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test",
                projects: new(Edits), edits: Edits, connections: new(Reader), mcpJournal: Journal);
        }
        /// <summary>排空受控读取和宿主后只删除本测试创建的目录.</summary>
        public async ValueTask DisposeAsync()
        {
            Reader.Release.TrySetResult(); await Session.ShutdownAsync(); Edits.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    /// <summary>只控制只读 SDK 读取与取消排空, 不访问真实引擎或用户凭据.</summary>
    private sealed class Reader : IConnectedModelReader, IModelEditWorkspace
    {
        internal bool Block { get; set; }
        internal bool Fail { get; set; }
        internal int ReadCount { get; private set; }
        internal int Registrations { get; private set; }
        internal int Forgets { get; private set; }
        internal RemoteModelAddress? Address { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>该夹具只验证远端, 本机注册意外调用直接失败.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token) => throw new NotSupportedException();
        /// <summary>记录实际传入身份与地址, 不建立网络连接.</summary>
        public string RegisterRemote(RemoteModelAddress address) { Registrations++; Address = address; return "model://remote-fixture"; }
        /// <summary>记录失败或取消后真实业务撤销注册.</summary>
        public void Forget(string id) => Forgets++;
        /// <summary>测试没有长生命周期网络连接可释放.</summary>
        public void Disconnect() { }
        /// <summary>远端模型必须在创建任何写草稿前被拒绝.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot snapshot, CancellationToken token) => throw new InvalidOperationException("Remote write must be rejected before creating a draft");
        /// <summary>返回合成只读快照, 可门控同步 SDK 完成时间并在边界传播取消与故障.</summary>
        public async Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
        {
            ReadCount++; using var registration = token.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult();
            if (Block) await Release.Task;
            token.ThrowIfCancellationRequested();
            if (Fail) throw new AppException("TEST_REMOTE_READ_FAILED", "测试远端读取失败", 4);
            return new(path, "remote-workspace", "remote-baseline", "TMSL", "PBIR", "1", null,
                new("Remote", 1606, "en-US", [], [], [], 0, 0), [], [], [], new("ssas-xmla", "SSAS"));
        }
    }
}
