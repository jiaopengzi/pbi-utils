// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldDesktopConnectionFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 新建 Desktop 连接回归的独占文件, 模型适配器与手动时钟, 所有数据仅存在测试目录.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

namespace PbiUtils.UnitTests;

/// <summary>真实会话和 FileAsync 共用同一模型读写服务, 只有平台与生成适配器使用可观察替身.</summary>
internal sealed class ScaffoldDesktopConnectionFixture : IAsyncDisposable, ILocalModelFileOpener
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-created-connection-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<JsonElement> messages = new();
    private readonly ScaffoldBridge scaffold;
    private readonly ModelConnectionService connections;
    public DesktopReader Reader { get; } = new();
    public ManualClock Clock { get; } = new();
    public BridgeSession Session { get; }
    public ModelEditService Edits { get; }
    public LocalRecentProjects Recent { get; }
    public string Entry { get; }
    public string CreationIdentity { get; private set; } = "";
    public string? ForwardedIdentity { get; private set; }
    public string? OpenedFile { get; private set; }
    public int FileVerifications { get; private set; }
    public JsonElement Last => messages.Last(message => message.TryGetProperty("id", out _));
    public string? Error => Last.GetProperty("error").GetProperty("code").GetString();

    /// <summary>创建专属测试路径与真实业务组合, 不使用真实 Desktop、用户设置或网络.</summary>
    public ScaffoldDesktopConnectionFixture()
    {
        Directory.CreateDirectory(root);
        Entry = Path.Combine(root, "created", "created.pbip");
        Recent = new(Path.Combine(root, "recent"));
        connections = new(Reader, localFiles: this);
        Edits = new(Reader, new UnusedWorkspace());
        scaffold = new(new Catalog(), new(new Workspace(Entry)), root, _ => Task.FromResult<string?>(root),
            openDesktop: ConnectCreatedAsync);
        Session = new(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test",
            projects: new(Edits), connections: connections, edits: Edits, recentProjects: Recent,
            scaffold: scaffold, requestTimeProvider: Clock);
    }

    /// <summary>创建结构可核验的 PBIP 回执并经真实脚手架草稿流程绑定入口身份.</summary>
    public async Task CreateAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        await File.WriteAllTextAsync(Entry, "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"Project.Report\"}}]}");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Entry)!, "Project.Report"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Entry)!, "Project.Report", "definition.pbir"), "{}");
        CreationIdentity = ProjectFileIdentity.Read(Entry);
        await ScaffoldCall("scaffold.chooseOutput", new { });
        var draft = await ScaffoldCall("scaffold.updateDraft", new
        {
            section = "base",
            value = new { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "created", locale = "zh-cn", gitInitialize = false }
        });
        var draftId = draft.GetProperty("draftId").GetString();
        var revision = draft.GetProperty("revision").GetInt32();
        var plan = await ScaffoldCall("scaffold.preview", new { draftId, revision });
        await ScaffoldCall("scaffold.create", new { draftId, revision, digest = plan.GetProperty("digest").GetString() });
    }

    /// <summary>通过实际桥接发布旧连接, 返回后续失败和取消必须保留的不透明模型句柄.</summary>
    public async Task<string> ConnectPreviousAsync()
    {
        await Call("project.connectLocal", new { processId = "100" });
        return Last.GetProperty("result").GetProperty("projectPath").GetString()!;
    }

    /// <summary>发送真实结构化桥接请求, 参数没有磁盘路径或模型授权.</summary>
    public Task Call(string method, object parameters, string? id = null) => Session.ReceiveAsync(OriginPolicy.Production,
        JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id = id ?? Guid.NewGuid().ToString("N"),
            method,
            @params = parameters,
            connectionRevision = messages.Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).DefaultIfEmpty().Max()
        }));

    /// <summary>读取指定请求实际发出的安全阶段, 用于验证连接等待尚未跨越发布边界.</summary>
    /// <param name="id">需要读取阶段通知的完整请求身份.</param>
    /// <returns>按接收顺序物化的阶段数组; 没有匹配通知时返回空数组.</returns>
    public string[] Phases(string id) => [.. messages.Where(message => message.TryGetProperty("requestPhase", out var phase)
        && phase.GetProperty("requestId").GetString() == id).Select(message => message.GetProperty("requestPhase").GetProperty("phase").GetString()!)];

    /// <summary>让发布最近记录明确失败, 保留损坏原文而不模拟成功写入.</summary>
    public void CorruptRecent()
    {
        Directory.CreateDirectory(Path.Combine(root, "recent"));
        File.WriteAllText(Path.Combine(root, "recent", "recent-projects.json"), "{");
    }

    /// <summary>打开受限创建入口前后核验原身份, 中间复用实际共享文件连接用例.</summary>
    private async Task<ProjectSnapshot> ConnectCreatedAsync(string path, string identity, CancellationToken token)
    {
        ForwardedIdentity = identity;
        if (ProjectFileIdentity.Read(path) != identity) throw new AppException("CREATED_PROJECT_NOT_AVAILABLE", "创建入口已变化", 3);
        var project = await connections.FileAsync(path, token);
        if (ProjectFileIdentity.Read(path) != identity) throw new AppException("CREATED_PROJECT_NOT_AVAILABLE", "创建入口已变化", 3);
        return project;
    }

    /// <summary>只记下共享用例交出的文件入口, 返回与旧连接不同的模拟平台目标.</summary>
    public Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); OpenedFile = path;
        return Task.FromResult(new LocalModelFileTarget(path, 200, DateTime.UnixEpoch));
    }

    /// <summary>观察 FileAsync 的连接后复核步骤, 取消时不产生成功回执.</summary>
    public Task VerifyAsync(LocalModelFileTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); FileVerifications++; return Task.CompletedTask;
    }

    /// <summary>执行真实脚手架分发以准备回执, 不借助字段反射或任意目标注入.</summary>
    private async Task<JsonElement> ScaffoldCall(string method, object parameters) => JsonSerializer.SerializeToElement(
        await scaffold.HandleAsync(method, JsonSerializer.SerializeToElement(parameters, JsonSerializerOptions.Web), null, CancellationToken.None), JsonSerializerOptions.Web);

    /// <summary>构造与磁盘入口分离的 Desktop 权威模型快照.</summary>
    public static ProjectSnapshot Snapshot(string path) => new(path, path, "baseline", "TMDL", "PBIR", "4.2", null,
        new("Model", 1702, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));

    /// <summary>先释放门闩并排空会话, 再释放共享编辑器和独占测试目录.</summary>
    public async ValueTask DisposeAsync()
    {
        Reader.Release.TrySetResult();
        await Session.ShutdownAsync(); Edits.Dispose();
        Directory.Delete(root, true);
    }

    /// <summary>平台注册身份与读取边界替身, 所有句柄只在当前测试实例有效.</summary>
    internal sealed class DesktopReader : IConnectedModelReader
    {
        public Dictionary<string, int> Targets { get; } = [];
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>每轮文件和旧连接分配不同句柄, 不以磁盘路径作为在线模型身份.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token)
        {
            var id = "model://" + Guid.NewGuid().ToString("N"); Targets[id] = processId; return Task.FromResult(id);
        }
        /// <summary>测试不提供远程连接能力.</summary>
        public string RegisterRemote(RemoteModelAddress address) => throw new NotSupportedException();
        /// <summary>失败或取消只撤销新注册.</summary>
        public void Forget(string id) => Targets.Remove(id);
        /// <summary>成功发布后只保留本轮模型句柄.</summary>
        public void Retain(string? id) { foreach (var key in Targets.Keys.Where(key => key != id).ToArray()) Targets.Remove(key); }
        /// <summary>会话关闭清理自身全部注册.</summary>
        public void Disconnect() => Targets.Clear();
        /// <summary>可观察的读取门闩在释放后检查取消, 不用延迟猜测竞态.</summary>
        public async Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            if (Block) { Entered.TrySetResult(); await Release.Task; }
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new AppException("TEST_READ_FAILED", "模型读取失败", 4);
            return Snapshot(projectPath);
        }
    }

    /// <summary>本组只验证切换, 不允许意外建立真实或虚假的编辑草稿.</summary>
    private sealed class UnusedWorkspace : IModelEditWorkspace
    {
        /// <summary>意外调用编辑能力立即失败.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new NotSupportedException();
    }

    /// <summary>生成适配器只返回对应测试磁盘入口, 核验与发布由真实桥接执行.</summary>
    private sealed class Workspace(string entry) : IScaffoldWorkspace
    {
        /// <summary>返回绑定草稿的预览身份.</summary>
        public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ScaffoldPlan("plan", new string('c', 64), DateTimeOffset.UtcNow.AddMinutes(10), output, request, "created.pbip", [], [], [], 1702));
        /// <summary>模拟已核验创建回执, 不修改当前模型.</summary>
        public async Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null)
        {
            if (beforeCommit is not null) await beforeCommit(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            committing?.Invoke(); return new ScaffoldResult("plan", "published", true, true, false, entry, "baseline", [], []);
        }
        /// <summary>此预览替身无外部资源.</summary>
        public Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken) => Task.FromResult(true);
        /// <summary>此创建替身无外部资源.</summary>
        public void Dispose() { }
    }

    /// <summary>测试不读取实际模板库.</summary>
    private sealed class Catalog : IPbipTemplateCatalog
    {
        /// <summary>模板列表不参与模型连接验证.</summary>
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PbipTemplateInfo>>([]);
        /// <summary>意外读取模板立即失败.</summary>
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>以手动时间验证完整 180 秒等待预算, 不创建实际等待计时器.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private TimeSpan now;
        private readonly List<ManualTimer> timers = [];
        /// <summary>记录生产请求实际指定的阶段截止时间.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, now + dueTime); timers.Add(timer); return timer;
        }
        /// <summary>只触发尚未释放且实际到期的单次回调.</summary>
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray()) timer.Fire();
        }
    }

    /// <summary>请求完成或阶段替换后不会再触发的单次测试计时器.</summary>
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        /// <summary>模拟平台触发到期回调.</summary>
        public void Fire() { Disposed = true; callback(state); }
        /// <summary>生产改阶段创建新计时器, 不允许修改旧定时器掩盖边界.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        /// <summary>取消后续触发.</summary>
        public void Dispose() => Disposed = true;
        /// <summary>释放单次计时器.</summary>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
