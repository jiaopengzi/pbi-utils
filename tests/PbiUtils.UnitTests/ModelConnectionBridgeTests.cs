// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConnectionBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证单模型切换、失败回退、取消门闩和服务退出的实际桥接分发.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Connections;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用实际 BridgeSession 与共享连接服务, 假适配器不进行网络请求.</summary>
public sealed class ModelConnectionBridgeTests
{
    /// <summary>本地文件填写与 Desktop 打开各有独立预算, 旧选择阶段回调不能取消新阶段.</summary>
    [Fact]
    public async Task FileSelectionAndDesktopOpeningHaveIndependentDeadlines()
    {
        var clock = new ManualClock();
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(clock: clock, selectFile: token => selected.Task.WaitAsync(token));
        fixture.Reader.Block = true;
        var request = fixture.Call("project.open", new { }, "file");
        var selectionTimer = clock.Timers.Last();
        clock.Advance(TimeSpan.FromSeconds(170));
        Assert.Equal(1, fixture.Bridge.PendingCount);
        selected.SetResult("file.pbix");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("connecting", fixture.Last.GetProperty("requestPhase").GetProperty("phase").GetString());
        selectionTimer.FireStale(); clock.Advance(TimeSpan.FromSeconds(170));
        Assert.False(fixture.Reader.Token.IsCancellationRequested);
        fixture.Reader.Release.SetResult(); await request;
        Assert.Equal("desktop", fixture.Last.GetProperty("result").GetProperty("connection").GetProperty("kind").GetString());
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    /// <summary>文件选择取消返回独立取消结果, 保留当前模型并且不增加新注册.</summary>
    [Fact]
    public async Task CancelledFileSelectionPreservesCurrentModel()
    {
        using var fixture = new Fixture(selectFile: _ => Task.FromResult<string?>(null));
        await fixture.Call("project.connectLocal", new { processId = "100" });
        var previous = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        await fixture.Call("project.open", new { });
        Assert.Equal("CANCELLED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        await fixture.Call("project.inspect", new { });
        Assert.Equal(previous, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets);
    }

    /// <summary>平铺填写不保留在途请求或模型门闩, 点击连接才开始完整30秒预算并释放临时凭据.</summary>
    [Fact]
    public async Task InlineRemoteInputDoesNotStartRequestDeadlineOrHoldModelGate()
    {
        var clock = new ManualClock();
        using var fixture = new Fixture(clock: clock);
        var parameters = await fixture.RemoteParametersAsync(windows: true);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        clock.Advance(TimeSpan.FromSeconds(170));
        await fixture.Call("project.connectLocal", new { processId = "100" });
        Assert.True(fixture.Last.TryGetProperty("result", out _));
        fixture.Reader.Block = true;
        var request = fixture.Call("project.connectRemote", parameters, "remote");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("connecting", fixture.Last.GetProperty("requestPhase").GetProperty("phase").GetString());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(fixture.Reader.Token.IsCancellationRequested);
        fixture.Reader.Release.SetResult(); await request;
        Assert.True(fixture.Last.TryGetProperty("result", out _));
        Assert.NotNull(fixture.Reader.LastCredential);
        Assert.Throws<ObjectDisposedException>(() => fixture.Reader.LastCredential.Use((_, _) => true));
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    /// <summary>真实连接30秒超时与主动取消均等待读取排空, 阻止抢占并保留先前当前模型.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteDeadlineAndCancellationDrainBeforePreservingPreviousModel(bool userCancellation)
    {
        var clock = new ManualClock();
        using var fixture = new Fixture(clock: clock);
        await fixture.Call("project.connectLocal", new { processId = "100" });
        var previous = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        var parameters = await fixture.RemoteParametersAsync();
        fixture.Reader.Block = true;
        var request = fixture.Call("project.connectRemote", parameters, "remote");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (userCancellation)
        {
            await fixture.Call("request.cancel", new { requestId = "remote" });
            Assert.True(fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean());
        }
        else clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(fixture.Reader.Token.IsCancellationRequested);
        Assert.False(request.IsCompleted); Assert.Equal(1, fixture.Bridge.PendingCount);
        await fixture.Call("project.connectLocal", new { processId = "200" });
        Assert.Equal("BUSY", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        fixture.Reader.Release.TrySetResult(); await request;
        Assert.Equal(userCancellation ? "CANCELLED" : "TIMEOUT", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        fixture.Reader.Block = false;
        await fixture.Call("project.inspect", new { });
        Assert.Equal(previous, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets);
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    /// <summary>同源重新导航也撤销旧表单, 旧 ID 不得连接; 连接失败同样不替换先前模型.</summary>
    [Fact]
    public async Task DocumentReloadAndFailedRemoteConnectionPreserveCurrentWorkspace()
    {
        using var fixture = new Fixture();
        await fixture.Call("project.connectLocal", new { processId = "100" });
        var previous = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        var oldParameters = await fixture.RemoteParametersAsync(); fixture.Bridge.InvalidateDocument();
        await fixture.Call("project.connectRemote", oldParameters);
        Assert.Equal("REMOTE_FORM_EXPIRED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(fixture.Reader.Targets);
        var currentParameters = await fixture.RemoteParametersAsync(); fixture.Reader.Fail = true;
        await fixture.Call("project.connectRemote", currentParameters);
        Assert.Equal("TEST_READ_FAILED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        fixture.Reader.Fail = false; await fixture.Call("project.inspect", new { });
        Assert.Equal(previous, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets); Assert.Equal(0, fixture.Bridge.PendingCount);
        await fixture.Call("project.connectRemote", currentParameters);
        Assert.Equal("REMOTE_FORM_EXPIRED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>关闭停止新表单读取并撤销未提交身份, 已进入读取的请求继续排空后释放.</summary>
    [Fact]
    public async Task ShutdownRevokesFormAndRejectsNewContextUntilRemoteReadDrains()
    {
        using var fixture = new Fixture(); var parameters = await fixture.RemoteParametersAsync();
        fixture.Reader.Block = true;
        var request = fixture.Call("project.connectRemote", parameters, "remote");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var shutdown = fixture.Bridge.ShutdownAsync();
        Assert.False(shutdown.IsCompleted); Assert.True(fixture.Reader.Token.IsCancellationRequested);
        var count = fixture.MessageCount;
        await fixture.Call("remote.form", new { });
        Assert.Equal(count, fixture.MessageCount); Assert.False(shutdown.IsCompleted);
        fixture.Reader.Release.TrySetResult(); await Task.WhenAll(request, shutdown);
        Assert.Equal("Disposed", fixture.Bridge.Lifecycle); Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Empty(fixture.Reader.Targets);
    }

    /// <summary>实际桥接拒绝输入时只发稳定错误, 日志和返回均不包含新密码、目标或账号.</summary>
    [Fact]
    public async Task RemoteInputAndFailureLogsDoNotExposeSubmittedSecrets()
    {
        using var fixture = new Fixture(); await fixture.Call("remote.form", new { });
        var formId = fixture.Last.GetProperty("result").GetProperty("formId").GetString();
        await fixture.Call("project.connectRemote", new
        {
            formId,
            values = new { server = "powerbi://api.powerbi.com/v1.0/myorg/private-target", database = "private-model", authenticationMode = "windows", userName = "server\\private-user", password = "private-password-sentinel", remember = true, rememberPassword = true, recentId = "" }
        });
        Assert.Equal("INVALID_AUTHENTICATION_MODE", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        var logged = string.Join('\n', fixture.Log.Messages) + fixture.Last.GetRawText();
        foreach (var value in new[] { "private-target", "private-model", "private-user", "private-password-sentinel" }) Assert.DoesNotContain(value, logged);
        Assert.Empty(fixture.Reader.Targets);
    }

    /// <summary>登录缓存提交与取消同样原子裁决, 不能返回已取消但留下新账号.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignInCommitBarrierPreventsCancelledAccountMutation(bool commitFirst)
    {
        var cloud = new GatedCloud(commitFirst);
        using var fixture = new Fixture(cloud);
        var login = fixture.Call("powerbi.serviceSignIn", new { email = "test@example.com", environmentName = "GlobalCloud", remember = true }, "login");
        await cloud.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("request.cancel", new { requestId = "login" });
        Assert.Equal(!commitFirst, fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean());
        cloud.Release.TrySetResult(); await login;
        if (commitFirst) Assert.NotNull(fixture.Last.GetProperty("result").GetProperty("account").GetProperty("username").GetString());
        else Assert.Equal("CANCELLED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(commitFirst, cloud.Account is not null);
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    /// <summary>初次邮箱登录按实际阶段通知检测后认证, 仅通知固定阶段且不包含邮箱或令牌.</summary>
    [Fact]
    public async Task ServiceSignInPublishesDiscoveryThenAuthenticationPhases()
    {
        var cloud = new GatedCloud(false); using var fixture = new Fixture(cloud);
        var login = fixture.Call("powerbi.serviceSignIn", new { email = "test@example.com", environmentName = "", remember = false });
        await cloud.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["discoveringEnvironment", "authenticating"], fixture.Phases);
        cloud.Release.SetResult(); await login;
        Assert.NotNull(fixture.Last.GetProperty("result").GetProperty("account").GetProperty("username").GetString());
    }

    /// <summary>只有完整读取及发布成功后的 Service 连接进入最近记录, 失败和提交前取消均保留空历史.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ServiceRecentRecordsOnlyPublishedSuccessfulConnections(string outcome)
    {
        var store = new RecentStore(); using var fixture = new Fixture(recentModelStore: store);
        await fixture.Call("powerbi.serviceModels", new { });
        fixture.Reader.Fail = outcome == "failure"; fixture.Reader.Block = outcome == "cancel";
        var connection = fixture.Call("project.connectService", new { modelId = "service-model" }, "connect");
        if (outcome == "cancel")
        {
            await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Call("request.cancel", new { requestId = "connect" });
            Assert.True(fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean()); fixture.Reader.Release.SetResult();
        }
        await connection;
        if (outcome == "success")
        {
            Assert.Equal(("actual-account", "GlobalCloud", "workspace", "stable-model"), Assert.Single(store.Saved));
            Assert.True(fixture.Last.TryGetProperty("result", out _));
        }
        else { Assert.Empty(store.Saved); Assert.True(fixture.Last.TryGetProperty("error", out _)); }
    }

    /// <summary>最近模型桥接读取仅在登录后提供当前目录交集, 不接受网页账号或权限标识.</summary>
    [Fact]
    public async Task ServiceRecentBridgeUsesCurrentDirectoryAndRequiresLogin()
    {
        using var fixture = new Fixture(recentModelStore: new RecentStore());
        await fixture.Call("powerbi.serviceRecentModels", new { });
        var row = Assert.Single(fixture.Last.GetProperty("result").GetProperty("items").EnumerateArray());
        Assert.Equal("service-model", row.GetProperty("modelId").GetString()); Assert.Equal("Current name", row.GetProperty("name").GetString());
        await fixture.Call("powerbi.serviceSignOut", new { });
        await fixture.Call("powerbi.serviceRecentModels", new { });
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
    }
    /// <summary>切换成功才淘汰旧目标, 失败保持原当前模型, 退出 Service 同步断开.</summary>
    [Fact]
    public async Task SwitchingKeepsOneModelAndFailurePreservesPrevious()
    {
        using var fixture = new Fixture();
        await fixture.Call("project.open", new { });
        await fixture.Call("project.connectService", new { modelId = "service-model" });
        var servicePath = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        Assert.Single(fixture.Reader.Targets);
        fixture.Reader.Fail = true;
        await fixture.Call("project.connectLocal", new { processId = "100" });
        Assert.Equal("TEST_READ_FAILED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        fixture.Reader.Fail = false;
        await fixture.Call("project.inspect", new { });
        Assert.Equal(servicePath, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets);
        await fixture.Call("powerbi.serviceSignOut", new { });
        Assert.Empty(fixture.Reader.Targets);
        await fixture.Call("project.inspect", new { });
        Assert.Equal("PROJECT_NOT_SELECTED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }
    /// <summary>退出云账号不影响 PBIP 或 Desktop 当前模型.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignOutPreservesOtherSources(bool desktop)
    {
        using var fixture = new Fixture();
        await fixture.Call(desktop ? "project.connectLocal" : "project.open", desktop ? new { processId = "100" } : (object)new { });
        var path = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        await fixture.Call("powerbi.serviceSignOut", new { });
        await fixture.Call("project.inspect", new { });
        Assert.Equal(path, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
    }
    /// <summary>并发切换被拒绝; 接受取消后没有迟到快照, 新注册被清理.</summary>
    [Fact]
    public async Task CancellationAndConcurrentSwitchPreserveCurrentModel()
    {
        using var fixture = new Fixture();
        await fixture.Call("project.connectLocal", new { processId = "100" });
        var path = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        fixture.Reader.Block = true;
        var connect = fixture.Call("project.connectService", new { modelId = "service-model" }, "pending");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("project.open", new { });
        Assert.Equal("BUSY", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        await fixture.Call("request.cancel", new { requestId = "pending" });
        Assert.True(fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean());
        fixture.Reader.Release.TrySetResult();
        await connect;
        Assert.Equal("CANCELLED", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        fixture.Reader.Block = false;
        await fixture.Call("project.inspect", new { });
        Assert.Equal(path, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets);
        await fixture.Call("project.open", new { });
        Assert.Single(fixture.Reader.Targets);
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }
    /// <summary>服务桥接拒绝网页令牌和连接地址; 未知字段不进入外部适配器.</summary>
    [Theory]
    [InlineData("powerbi.serviceSignIn")]
    [InlineData("powerbi.serviceDeleteLogin")]
    [InlineData("powerbi.serviceModels")]
    [InlineData("powerbi.serviceRecentModels")]
    [InlineData("powerbi.serviceDeleteRecentModel")]
    [InlineData("project.deleteRecent")]
    [InlineData("powerbi.serviceSignOut")]
    [InlineData("project.connectService")]
    [InlineData("project.connectRemote")]
    public async Task RejectsExtraCredentialParameters(string method)
    {
        using var fixture = new Fixture();
        await fixture.Call(method, new { modelId = "service-model", accessToken = "never-accepted" });
        Assert.Equal("VALIDATION_ERROR", fixture.Last.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("never-accepted", fixture.Last.ToString());
        Assert.Empty(fixture.Reader.Targets);
    }

    /// <summary>实际桥接只删除指定最近记录, 保留真实文件、当前模型和其他账号历史, 重复删除返回 false.</summary>
    [Fact]
    public async Task DeletesBoundRecentRecordsWithoutDisconnectingCurrentModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-recent-delete-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var model = Path.Combine(root, "private.pbix"); File.WriteAllText(model, "model-file");
            var local = new LocalRecentProjects(root); local.Record(model); var localId = Assert.Single(local.Load()).Id;
            var cloudStore = new LocalPowerBiRecentModelStore(root);
            await cloudStore.SaveAsync("actual-account", "GlobalCloud", "workspace", "stable-model", default);
            await cloudStore.SaveAsync("another-account", "GlobalCloud", "workspace", "stable-model", default);
            var cloudId = Assert.Single(await cloudStore.LoadAsync("actual-account", "GlobalCloud", default)).Id;
            using var fixture = new Fixture(recentModelStore: cloudStore, recentProjects: local);
            await fixture.Call("project.connectLocal", new { processId = "100" });
            var current = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
            foreach (var (method, id) in new[] { ("project.deleteRecent", localId), ("powerbi.serviceDeleteRecentModel", cloudId) })
            {
                await fixture.Call(method, new { id }); Assert.True(fixture.Last.GetProperty("result").GetProperty("deleted").GetBoolean());
                await fixture.Call(method, new { id }); Assert.False(fixture.Last.GetProperty("result").GetProperty("deleted").GetBoolean());
                await fixture.Call(method, new { id, path = model }); Assert.Equal("VALIDATION_ERROR", fixture.Last.GetProperty("error").GetProperty("code").GetString());
                await fixture.Call(method, new { id = "private.pbix" }); Assert.Equal("VALIDATION_ERROR", fixture.Last.GetProperty("error").GetProperty("code").GetString());
            }
            Assert.Empty(local.Load()); Assert.Empty(await cloudStore.LoadAsync("actual-account", "GlobalCloud", default));
            Assert.Single(await cloudStore.LoadAsync("another-account", "GlobalCloud", default)); Assert.Equal("model-file", File.ReadAllText(model));
            await fixture.Call("project.inspect", new { }); Assert.Equal(current, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
            Assert.Single(fixture.Reader.Targets); Assert.Equal(0, fixture.Bridge.PendingCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>每个测试隔离桥接结果与模型状态.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ConcurrentQueue<JsonElement> messages = new();
        public Reader Reader { get; } = new();
        public BridgeSession Bridge { get; }
        public CapturingLogger Log { get; } = new();
        public int MessageCount => messages.Count;
        public JsonElement Last => messages.Last();
        public string[] Phases => [.. messages.Where(message => message.TryGetProperty("requestPhase", out _))
            .Select(message => message.GetProperty("requestPhase").GetProperty("phase").GetString()!)];
        /// <summary>使用真实共享服务创建单窗口桥接.</summary>
        public Fixture(IPowerBiServiceClient? cloud = null, TimeProvider? clock = null, Func<CancellationToken, Task<string?>>? selectFile = null,
            IPowerBiRecentModelStore? recentModelStore = null, LocalRecentProjects? recentProjects = null)
        {
            var remoteForm = new RemoteConnectionForm(new(Path.Combine(Path.GetTempPath(), "pbi-remote-bridge-" + Guid.NewGuid().ToString("N"))));
            Bridge = new(new(false, null), new(new RuntimeEnvironment()), text => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test",
                projects: new(Reader), connections: new(Reader, localFiles: new TestLocalFileConnection(Reader)), selectProject: selectFile ?? (_ => Task.FromResult<string?>("file.pbip")), cloudConnection: new(cloud ?? new Cloud(), recentModelStore: recentModelStore), remoteForm: remoteForm, requestTimeProvider: clock, recentProjects: recentProjects, logger: Log);
        }
        /// <summary>构造与真实网页相同的结构化请求.</summary>
        public Task Call(string method, object parameters, string? id = null) => Bridge.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                id = id ?? Guid.NewGuid().ToString("N"),
                method,
                @params = parameters,
                connectionRevision = messages.Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).DefaultIfEmpty().Max()
            }));
        /// <summary>读取实际宿主表单身份并构造无保存副作用的完整合成输入.</summary>
        public async Task<object> RemoteParametersAsync(bool windows = false)
        {
            await Call("remote.form", new { });
            return new
            {
                formId = Last.GetProperty("result").GetProperty("formId").GetString(),
                values = new { server = "server", database = "catalog", authenticationMode = windows ? "windows" : "current", userName = windows ? "server\\test-user" : "", password = windows ? "fake-password" : "", remember = false, rememberPassword = false, recentId = "" }
            };
        }
        /// <summary>释放本组桥接状态.</summary>
        public void Dispose() => Bridge.Dispose();
    }
    /// <summary>捕获真实业务日志格式与异常, 不由测试自行脱敏来掩盖泄露.</summary>
    private sealed class CapturingLogger : ILogger<BridgeSession>
    {
        public List<string> Messages { get; } = [];
        /// <summary>作用域在本测试中不需持有资源.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>包含所有级别以覆盖永久调试事件.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保留格式化内容和实际异常, 供秘密哨兵断言.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
    private sealed class Reader : IConnectedModelReader
    {
        public Dictionary<string, string> Targets { get; } = [];
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public CancellationToken Token { get; private set; }
        public RemoteWindowsCredential? LastCredential { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>模拟系统发现后的本机注册.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token) => Task.FromResult(Register("desktop"));
        /// <summary>模拟原生服务器授权.</summary>
        public string RegisterRemote(RemoteModelAddress address) { LastCredential = address.Credential; return Register("ssas-xmla"); }
        /// <summary>模拟当前账号发布的模型句柄注册.</summary>
        public string RegisterService(string modelId) => Register("powerbi-service");
        /// <summary>分配不含地址的新注册.</summary>
        private string Register(string source) { var id = "model://" + Guid.NewGuid().ToString("N"); Targets[id] = source; return id; }
        /// <summary>失败或取消清理尚未生效的注册.</summary>
        public void Forget(string id) => Targets.Remove(id);
        /// <summary>成功切换后保留唯一注册.</summary>
        public void Retain(string? id) { foreach (var key in Targets.Keys.Where(k => k != id).ToArray()) Targets.Remove(key); }
        /// <summary>断开当前窗口所有注册.</summary>
        public void Disconnect() => Targets.Clear();
        /// <summary>门闩模拟读取中取消, 不依赖任意延迟.</summary>
        public async Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
        {
            Token = token;
            if (Block) { Entered.TrySetResult(); await Release.Task; }
            token.ThrowIfCancellationRequested();
            if (Fail) throw new AppException("TEST_READ_FAILED", "模拟读取失败");
            var source = Targets.GetValueOrDefault(path, "pbip");
            return new(path, path, "baseline", source, "report", "1", null, new("Model", 1600, "en-US", [], [], [], 0, 0), [], [], [], new(source, source));
        }
    }
    /// <summary>仅用于请求时限测试的手动时钟, 不依赖实际睡眠或外部服务.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];
        private TimeSpan now;
        /// <summary>创建可由测试推进的单次计时器.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, now + dueTime); Timers.Add(timer); return timer;
        }
        /// <summary>推进当前时刻并调用到期计时器.</summary>
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in Timers.Where(t => !t.Disposed && t.Due <= now).ToArray()) timer.FireStale();
        }
    }
    /// <summary>允许显式模拟已排队的旧阶段回调, 验证生产代码的阶段竞争保护.</summary>
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        /// <summary>执行回调, 包含模拟已释放但先前已排队的旧回调.</summary>
        public void FireStale() { Disposed = true; callback(state); }
        /// <summary>生产代码使用新阶段计时器, 测试不支持改动旧计时器.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        /// <summary>取消未触发的计时器.</summary>
        public void Dispose() => Disposed = true;
        /// <summary>异步释放测试计时器.</summary>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Cloud : IPowerBiServiceClient
    {
        public PowerBiAccount? Account { get; private set; } = new("test@example.com");
        public string? AccountId => Account is null ? null : "actual-account";
        /// <summary>返回固定假账号.</summary>
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) { committing?.Invoke(); return Task.FromResult(Account!); }
        /// <summary>返回固定环境, 不调用真实服务.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiEnvironment>>([new("GlobalCloud", "全球版")]);
        /// <summary>返回固定假目录.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiServiceModel>>([new("service-model", "Current name", "Current workspace", null, "workspace", "stable-model")]);
        /// <summary>桥接测试不能直接请求后端令牌.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string id, CancellationToken token) => throw new InvalidOperationException();
        /// <summary>模拟只清理本应用账号.</summary>
        public Task SignOutAsync(CancellationToken token, Action? committing = null) { committing?.Invoke(); Account = null; return Task.CompletedTask; }
    }
    private sealed class RecentStore : IPowerBiRecentModelStore
    {
        public List<(string, string, string, string)> Saved { get; } = [];
        /// <summary>只返回稳定目录标识, 展示名称应由当前目录重新提供.</summary>
        public Task<IReadOnlyList<StoredPowerBiServiceModel>> LoadAsync(string accountId, string environmentName, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StoredPowerBiServiceModel>>([new("recent-id", "workspace", "stable-model", DateTimeOffset.UtcNow)]);
        /// <summary>记录实际宿主调用, 不触及真实用户文件.</summary>
        public Task SaveAsync(string accountId, string environmentName, string workspaceId, string modelId, CancellationToken token)
        { Saved.Add((accountId, environmentName, workspaceId, modelId)); return Task.CompletedTask; }
        /// <summary>此假存储不提供删除数据, 实际删除由独立文件存储测试覆盖.</summary>
        public Task<bool> DeleteAsync(string accountId, string environmentName, string id, CancellationToken token, Action? committing = null) => Task.FromResult(false);
    }
    private sealed class GatedCloud(bool commitFirst) : IPowerBiServiceClient
    {
        public PowerBiAccount? Account { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>门闩控制缓存提交前后的取消时机.</summary>
        public async Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null)
        {
            if (commitFirst) committing?.Invoke();
            Entered.TrySetResult(); await Release.Task;
            token.ThrowIfCancellationRequested();
            if (!commitFirst) committing?.Invoke();
            Account = new("test@example.com"); return Account;
        }
        /// <summary>返回固定环境, 不调用真实服务.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiEnvironment>>([new("GlobalCloud", "全球版")]);
        /// <summary>登录竞争测试不读取外部目录.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiServiceModel>>([]);
        /// <summary>登录竞争测试不获取外部令牌.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string id, CancellationToken token) => throw new InvalidOperationException();
        /// <summary>清理假账号.</summary>
        public Task SignOutAsync(CancellationToken token, Action? committing = null) { committing?.Invoke(); Account = null; return Task.CompletedTask; }
    }
}
