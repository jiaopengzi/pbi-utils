// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BridgeResourceShutdownTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证桥接关闭排空配置拥有的异步草稿, 前序故障不跳过其他资源且不重复释放借用对象.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using System.Reflection;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>保留真实配置编译和编辑服务, 仅在文件保存与最终释放边界注入可控故障.</summary>
public sealed class BridgeResourceShutdownTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>临时预览的清理失败仍由配置持有; 成功、校验失败或取消的首因不被清理异常覆盖.</summary>
    /// <param name="outcome">预览主体完成、主动取消或命中实际归属校验失败.</param>
    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("invalid")]
    public async Task FailedPreviewCleanupStaysOwnedAndPreservesPrimaryOutcome(string outcome)
    {
        await using var fixture = new Fixture(draftDisposalFailure: new IOException("preview cleanup failure"));
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancelled") fixture.Workspace.AfterPreview = cancellation.Cancel;
        var preview = fixture.PreviewAsync(outcome == "invalid", cancellation.Token);
        var draft = await fixture.Workspace.Created.Task.WaitAsync(Deadline);
        await draft.DisposalEntered.Task.WaitAsync(Deadline);
        draft.Release.TrySetResult();
        if (outcome == "completed") await Assert.ThrowsAsync<IOException>(() => preview);
        else
        {
            Exception primary = outcome == "cancelled" ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview)
                : await Assert.ThrowsAsync<AppException>(() => preview);
            if (primary is AppException app) Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", app.Code);
            Assert.Equal(true, primary.Data["ModelDraftCleanupFailed"]);
        }
        await fixture.Bridge.ShutdownAsync().WaitAsync(Deadline);
        Assert.Equal("Disposed", fixture.Bridge.Lifecycle);
        Assert.Equal(2, draft.DisposeCalls);
    }

    /// <summary>当前或已被新建回执替换的 owned service 均由关闭任务排空, 未完成前不能宣布 Disposed.</summary>
    /// <param name="retired">是否先撤销已创建句柄, 使旧服务进入等待释放集合.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForOwnedConfigurationDraftIncludingRetiredOwner(bool retired)
    {
        await using var fixture = new Fixture();
        await fixture.PrepareConfigurationAsync();
        var draft = fixture.Workspace.Draft!;
        if (retired) fixture.Configuration.BindCreated(new("failed", "verification_pending", true, false, false, fixture.Entry, null, [], []));

        var closing = fixture.Bridge.ShutdownAsync();
        await draft.DisposalEntered.Task.WaitAsync(Deadline);
        Assert.False(closing.IsCompleted);
        Assert.Equal("Draining", fixture.Bridge.Lifecycle);
        draft.Release.TrySetResult();
        await closing.WaitAsync(Deadline);
        Assert.Equal("Disposed", fixture.Bridge.Lifecycle);
        Assert.Equal(1, draft.DisposeCalls);
        await fixture.Bridge.ShutdownAsync().WaitAsync(Deadline);
        Assert.Equal(1, draft.DisposeCalls);
    }

    /// <summary>前序断开故障不能跳过配置排空, 配置随后失败也仍释放工作区和预览门闩并保留首因.</summary>
    [Fact]
    public async Task EarlierDisconnectFailureStillDrainsConfigurationAndReleasesRemainingResources()
    {
        var disconnect = new IOException("first disconnect failure");
        await using var fixture = new Fixture(disconnectFailure: disconnect, draftDisposalFailure: new IOException("later draft failure"));
        await fixture.PrepareConfigurationAsync();
        var closing = fixture.Bridge.ShutdownAsync();
        await fixture.Workspace.Draft!.DisposalEntered.Task.WaitAsync(Deadline);
        Assert.False(closing.IsCompleted);
        Assert.Equal("Draining", fixture.Bridge.Lifecycle);
        fixture.Workspace.Draft.Release.TrySetResult();

        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => closing.WaitAsync(Deadline))).Code);
        Assert.Equal("ReleaseFailed", fixture.Bridge.Lifecycle);
        Assert.Same(disconnect, Field<Exception>(fixture.Bridge, "shutdownFailure"));
        Assert.Equal(1, fixture.OwnedWorkspace.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => Field<SemaphoreSlim>(fixture.Bridge, "calendarPreviewGate").Wait(0));
        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ShutdownAsync())).Code);
        Assert.Equal(2, fixture.Workspace.Draft.DisposeCalls);
        Assert.Equal(1, fixture.OwnedWorkspace.DisposeCalls);
    }

    /// <summary>脚手架最早失败时, 关闭仍须等待已经开始的主模型清理, 不能直接返回前序异常.</summary>
    [Fact]
    public async Task FailedScaffoldShutdownStillWaitsForMainModelCleanup()
    {
        await using var fixture = new Fixture(failScaffold: true);
        await fixture.PrepareMainDraftAsync();
        var closing = fixture.Bridge.ShutdownAsync();
        await fixture.Workspace.Draft!.DisposalEntered.Task.WaitAsync(Deadline);
        Assert.False(closing.IsCompleted);
        Assert.Equal("Draining", fixture.Bridge.Lifecycle);
        fixture.Workspace.Draft.Release.TrySetResult();
        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => closing.WaitAsync(Deadline))).Code);
        Assert.Equal(1, fixture.OwnedWorkspace.DisposeCalls);
        Assert.Equal("ReleaseFailed", fixture.Bridge.Lifecycle);
        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ShutdownAsync())).Code);
        Assert.Equal(1, fixture.Workspace.Draft.DisposeCalls);
        Assert.Equal(1, fixture.ScaffoldWorkspace!.DisposeCalls);
    }

    /// <summary>工作区首次清理失败后, 显式再次关闭仅重试未确认资源, 已成功断开不会重放.</summary>
    [Fact]
    public async Task ExplicitShutdownRetryOnlyRepeatsFailedWorkspace()
    {
        await using var fixture = new Fixture(failWorkspaceOnce: true);
        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ShutdownAsync())).Code);
        Assert.Equal("ReleaseFailed", fixture.Bridge.Lifecycle);
        await fixture.Bridge.ShutdownAsync().WaitAsync(Deadline);
        Assert.Equal("Disposed", fixture.Bridge.Lifecycle);
        Assert.Equal(2, fixture.OwnedWorkspace.DisposeCalls);
        Assert.Equal(1, fixture.Connection.DisconnectCalls);
    }

    /// <summary>配置 owned 草稿释放失败后保留原 owner, 下次显式关闭可重试而不重复已成功的工作区释放.</summary>
    [Fact]
    public async Task ExplicitShutdownRetryRetainsFailedConfigurationOwner()
    {
        await using var fixture = new Fixture(draftDisposalFailure: new IOException("first cleanup failure"));
        await fixture.PrepareConfigurationAsync();
        fixture.Workspace.Draft!.Release.TrySetResult();
        Assert.Equal("SHUTDOWN_FAILED", (await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ShutdownAsync())).Code);
        await fixture.Bridge.ShutdownAsync().WaitAsync(Deadline);
        Assert.Equal("Disposed", fixture.Bridge.Lifecycle);
        Assert.Equal(2, fixture.Workspace.Draft.DisposeCalls);
        Assert.Equal(1, fixture.OwnedWorkspace.DisposeCalls);
        Assert.Equal(1, fixture.Connection.DisconnectCalls);
    }

    /// <summary>配置仅借用当前编辑服务; 配置自身结束不得提前关闭主模型, 主 owner 随后关闭一次.</summary>
    [Fact]
    public async Task ConfigurationDoesNotDisposeBorrowedCurrentEditService()
    {
        await using var fixture = new Fixture();
        await fixture.PrepareConfigurationAsync(sharedCurrent: true);
        await fixture.Configuration.DisposeAsync();
        Assert.False(fixture.Workspace.Draft!.DisposalEntered.Task.IsCompleted);
        var closing = fixture.Bridge.ShutdownAsync();
        await fixture.Workspace.Draft.DisposalEntered.Task.WaitAsync(Deadline);
        fixture.Workspace.Draft.Release.TrySetResult();
        await closing.WaitAsync(Deadline);
        Assert.Equal(1, fixture.Workspace.Draft.DisposeCalls);
    }

    /// <summary>同步断开和工作区清理不在调用者的 UI 同步上下文中执行.</summary>
    [Fact]
    public async Task DisposeQueuesSynchronousResourceCleanupOffCallerContext()
    {
        await using var fixture = new Fixture();
        var callerContext = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(callerContext); fixture.Bridge.Dispose(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await fixture.Bridge.ShutdownAsync().WaitAsync(Deadline);
        Assert.NotSame(callerContext, fixture.Connection.DisconnectContext);
        Assert.NotSame(callerContext, fixture.OwnedWorkspace.DisposeContext);
        Assert.Equal(1, fixture.OwnedWorkspace.DisposeCalls);
    }

    /// <summary>仅观察无法从关闭后的公开请求访问的资源终态, 不向产品添加测试注入入口.</summary>
    private static T Field<T>(BridgeSession bridge, string name) => (T)typeof(BridgeSession)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;

    /// <summary>所有磁盘内容都在本夹具 Guid 目录, 真实编译器冻结配置但保存边界固定失败.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-resource-shutdown-" + Guid.NewGuid().ToString("N"));
        private readonly ProjectReader reader = new();
        private readonly ChangeWorkspace fileWorkspace;
        private readonly ModelConfigurationCompiler compiler = new();
        public string Entry { get; }
        public BlockingWorkspace Workspace { get; }
        public TrackingResource OwnedWorkspace { get; } = new();
        public ControlledConnection Connection { get; }
        public ModelEditService MainEdits { get; }
        public ModelConfigurationBridge Configuration { get; }
        public BridgeSession Bridge { get; }
        public FailingScaffoldWorkspace? ScaffoldWorkspace { get; }

        /// <summary>建立最小本地文件工程, 不打开 Desktop 或修改任何用户模型.</summary>
        public Fixture(Exception? disconnectFailure = null, Exception? draftDisposalFailure = null, bool failScaffold = false, bool failWorkspaceOnce = false)
        {
            Entry = PbipFixture.Create(Path.Combine(root, "project"));
            fileWorkspace = new(Path.Combine(root, "user"));
            Workspace = new(fileWorkspace, draftDisposalFailure);
            MainEdits = new(reader, Workspace);
            Configuration = new(reader, Workspace, compiler);
            Connection = new(disconnectFailure);
            OwnedWorkspace.FailOnce = failWorkspaceOnce;
            ScaffoldWorkspace = failScaffold ? new() : null;
            var scaffold = ScaffoldWorkspace is not null ? new ScaffoldBridge(new ScaffoldTemplateCatalog(root), new ScaffoldService(ScaffoldWorkspace),
                root, _ => Task.FromResult<string?>(null)) : null;
            Bridge = new(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test", ownedWorkspace: OwnedWorkspace,
                connections: new(Connection), edits: MainEdits, configuration: Configuration, scaffold: scaffold);
        }

        /// <summary>通过真实配置公开路径保留提交前失败的草稿, 按参数分别归配置或当前编辑服务拥有.</summary>
        public async Task PrepareConfigurationAsync(bool sharedCurrent = false)
        {
            var snapshot = await reader.ReadAsync(Entry, default);
            MainEdits.Select(snapshot);
            Configuration.BindCreated(new("plan", "published", true, true, false, Entry, snapshot.BaselineId, [], []));
            var context = await Configuration.ContextAsync("created", sharedCurrent ? snapshot : null, default);
            var input = JsonSerializer.SerializeToElement(new
            {
                targetId = context.TargetId,
                baselineId = snapshot.BaselineId,
                section = "measures",
                request = new ModelConfigurationRequest(Measures: [new("ShutdownFixture", "value", "Uncategorized", true, "", "")],
                    MeasureConfigurationRevision: context.MeasureConfigurationRevision)
            }, Json);

            await Assert.ThrowsAsync<IOException>(() => Configuration.ApplyAsync(input, sharedCurrent ? snapshot : null,
                sharedCurrent ? MainEdits : null, default));
            Assert.True(Configuration.HasPendingChanges);
        }

        /// <summary>建立独立已创建目标并执行真实只读预览, 可选择实际未归属表触发主体校验失败.</summary>
        public async Task<ScaffoldCalendarProgram> PreviewAsync(bool invalid, CancellationToken token)
        {
            var snapshot = await reader.ReadAsync(Entry, token);
            Configuration.BindCreated(new("plan", "published", true, true, false, Entry, snapshot.BaselineId, [], []));
            var context = await Configuration.ContextAsync("created", null, token);
            return await Configuration.PreviewCalendarAsync(context.TargetId, snapshot.BaselineId,
                new("simple", 2024, 2024, invalid ? "销售" : "PreviewDates", Fields: ["Dates"]), "zh-cn", null, token);
        }

        /// <summary>主编辑服务使用相同的真实文件草稿, 关闭前不进入保存.</summary>
        public async Task PrepareMainDraftAsync()
        {
            MainEdits.Select(await reader.ReadAsync(Entry, default));
            await MainEdits.StageAsync(new("table.set", "销售", "description", "pending"), default);
        }

        /// <summary>断言失败也先打开清理门闩, 观察最终任务后再移除本夹具目录.</summary>
        public async ValueTask DisposeAsync()
        {
            Workspace.Draft?.Release.TrySetResult();
            try { await Bridge.ShutdownAsync().WaitAsync(Deadline); }
            catch (Exception error) when (error is AppException { Code: "SHUTDOWN_FAILED" } or IOException) { }
            if (Workspace.Draft is { } draft) { await draft.DisposeAsync(); await draft.DisposalFinished.Task.WaitAsync(Deadline); }
            compiler.Dispose(); fileWorkspace.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>保留真实文件草稿的校验和状态, 只将保存失败与异步释放延迟注入到外部资源边界.</summary>
    private sealed class BlockingWorkspace(IModelEditWorkspace inner, Exception? failure) : IModelEditWorkspace
    {
        public BlockingDraft? Draft { get; private set; }
        public Action? AfterPreview { get; set; }
        public TaskCompletionSource<BlockingDraft> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>在真实草稿创建完成后发布受控边界, 不模拟模型校验结果.</summary>
        public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            Draft = new(await inner.CreateDraftAsync(baseline, token), failure, () => AfterPreview?.Invoke());
            Created.TrySetResult(Draft);
            return Draft;
        }
    }

    /// <summary>等待显式门闩才释放真实草稿; 释放故障用于证明 owner 必须观察异步任务.</summary>
    private sealed class BlockingDraft(IModelDraft inner, Exception? failure, Action afterPreview) : IModelDraft, ICalendarProgramDraft
    {
        private Task? disposal;
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProjectSnapshot Snapshot => inner.Snapshot;
        /// <summary>原样转发真实文件草稿的模型校验与暂存, 不替代任何业务规则.</summary>
        public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => inner.ApplyAsync(request, token);
        /// <summary>在提交前固定失败以保留待关闭草稿, 不调用发布或提交回调.</summary>
        public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => throw new IOException("isolated save failure");
        /// <summary>转发真实只读编译, 完成后允许测试在同一令牌上明确发出取消.</summary>
        public async Task<ScaffoldCalendarProgram> PreviewCalendarAsync(ScaffoldCalendarOptions options, string locale, CancellationToken token)
        {
            var program = await ((ICalendarProgramDraft)inner).PreviewCalendarAsync(options, locale, token);
            afterPreview();
            return program;
        }
        /// <summary>同次释放共享任务, 后续显式调用仅重试首次注入的未确认清理.</summary>
        public void Dispose() { if (disposal is null || disposal.IsFaulted) disposal = CloseAsync(); }
        /// <summary>让真实 owner 等待释放门闩与失败结果, 不同步阻塞调用者.</summary>
        public ValueTask DisposeAsync() { Dispose(); return new(disposal!); }

        /// <summary>无论注入故障与否都真正释放夹具模型并发布完成事实, 不隐藏 owner 的异常.</summary>
        private async Task CloseAsync()
        {
            DisposeCalls++; DisposalEntered.TrySetResult();
            try { await Release.Task; if (failure is not null && DisposeCalls == 1) throw failure; await inner.DisposeAsync(); }
            finally { DisposalFinished.TrySetResult(); }
        }
    }

    /// <summary>记录最后独立拥有者是否被尝试释放及其调用上下文.</summary>
    private sealed class TrackingResource : IDisposable
    {
        public int DisposeCalls { get; private set; }
        public bool FailOnce { get; set; }
        public SynchronizationContext? DisposeContext { get; private set; }
        /// <summary>记录真实拥有者的清理调用, 可选首次失败供显式关闭重试验证.</summary>
        public void Dispose() { DisposeCalls++; DisposeContext = SynchronizationContext.Current; if (FailOnce && DisposeCalls == 1) throw new IOException("first workspace cleanup failure"); }
    }

    /// <summary>只在断开边界注入故障, 不建立真实在线连接.</summary>
    private sealed class ControlledConnection(Exception? failure) : IConnectedModelReader
    {
        public SynchronizationContext? DisconnectContext { get; private set; }
        public int DisconnectCalls { get; private set; }
        /// <summary>本夹具不注册真实 Desktop, 误入注册路径立即失败.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token) => throw new NotSupportedException();
        /// <summary>本夹具不允许远端注册.</summary>
        public string RegisterRemote(RemoteModelAddress address) => throw new NotSupportedException();
        /// <summary>本夹具只观察断开, 读取在线模型说明调用路径有误.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        /// <summary>没有真实注册目标可撤销, 误调用按不支持处理.</summary>
        public void Forget(string id) => throw new NotSupportedException();
        /// <summary>观察断开上下文, 可选持续失败用于证明其他清理不会被跳过.</summary>
        public void Disconnect() { DisconnectCalls++; DisconnectContext = SynchronizationContext.Current; if (failure is not null) throw failure; }
    }

    /// <summary>脚手架没有活动业务, 只在最终 owned workspace 清理时抛出首个异常.</summary>
    private sealed class FailingScaffoldWorkspace : IScaffoldWorkspace
    {
        public int DisposeCalls { get; private set; }
        /// <summary>关闭测试不应进入脚手架生成预览.</summary>
        public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        /// <summary>关闭不得重放脚手架创建业务或提交回调.</summary>
        public Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken token, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null) => throw new NotSupportedException();
        /// <summary>没有真实 staging, 正常回答受控废弃边界.</summary>
        public Task<bool> DiscardAsync(string digest, CancellationToken token) => Task.FromResult(true);
        /// <summary>固定失败并计数, 后续桥接关闭不能重新发起这次脚手架终态.</summary>
        public void Dispose() { DisposeCalls++; throw new IOException("isolated scaffold cleanup failure"); }
    }
}
