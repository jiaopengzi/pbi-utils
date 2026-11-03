// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldSessionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过实际桥接会话验证脚手架提交取消, 进度及关闭排空.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅用门闩适配器控制提交先后, 不以时间等待猜测是否提交.</summary>
public sealed class ScaffoldSessionTests
{
    /// <summary>提交前接受取消, 提交后拒绝取消; 两种关闭均等待业务排空.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndShutdownDrainScaffold(bool committed)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var adapter = new Adapter(committed);
        ConcurrentQueue<JsonElement> messages = new();
        var scaffold = new ScaffoldBridge(new Catalog(), new(adapter), root, _ => Task.FromResult<string?>(root));
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            text => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", scaffold: scaffold);
        try
        {
            await Call(session, "base", "scaffold.updateDraft", new
            {
                section = "base",
                value = new
                { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "output", locale = "zh-cn" }
            });
            var draft = messages.Last().GetProperty("result");
            var identity = new { draftId = draft.GetProperty("draftId").GetString(), revision = draft.GetProperty("revision").GetInt32() };
            await Call(session, "choose", "scaffold.chooseOutput", new { });
            await Call(session, "preview", "scaffold.preview", identity);
            Assert.Contains(messages, message => message.TryGetProperty("event", out var value)
                && value.GetProperty("requestId").GetString() == "preview" && value.GetProperty("type").GetString() == "scaffoldProgress"
                && value.GetProperty("payload").GetProperty("percent").GetInt32() == 99);
            var creation = Call(session, "create", "scaffold.create", new { identity.draftId, identity.revision, digest = new string('c', 64) });
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(committed, messages.Any(message => message.TryGetProperty("requestPhase", out var value)
                && value.GetProperty("requestId").GetString() == "create" && value.GetProperty("phase").GetString() == "committing"));
            await Call(session, "cancel", "request.cancel", new { requestId = "create" });
            Assert.Equal(!committed, messages.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
            var shutdown = session.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            Assert.False(adapter.Disposed);
            adapter.Release.TrySetResult();
            await Task.WhenAll(creation, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(committed ? 1 : 0, adapter.Writes);
            Assert.True(adapter.Disposed);
            Assert.Equal(0, session.PendingCount);
            Assert.Equal("Disposed", session.Lifecycle);
        }
        finally
        {
            adapter.Release.TrySetResult();
            await session.ShutdownAsync();
            Directory.Delete(root);
        }
    }

    /// <summary>发送真实 JSON 协议消息.</summary>
    private static Task Call(BridgeSession session, string id, string method, object parameters) =>
        session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));

    /// <summary>保持项目业务和资源释放可观察.</summary>
    private sealed class Adapter(bool committed) : IScaffoldWorkspace
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new(99, "scaffold.previewReady"));
            return Task.FromResult(new ScaffoldPlan("id", new string('c', 64), DateTimeOffset.UtcNow.AddMinutes(15), output, request, "output.pbip", [], [], [], 1606));
        }
        public async Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress,
            CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null)
        {
            if (beforeCommit is not null) await beforeCommit(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (committed) committing?.Invoke();
            Entered.TrySetResult();
            await Release.Task;
            if (!committed) cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            return new("id", "published", true, true, false, Path.Combine(output, "output.pbip"), "baseline", [], []);
        }
        public Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken) => Task.FromResult(!committed || Writes == 0);
        public void Dispose() => Disposed = true;
    }

    /// <summary>本组仅验证会话协议, 模板磁盘行为由独立测试覆盖.</summary>
    private sealed class Catalog : IPbipTemplateCatalog
    {
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PbipTemplateInfo>>([]);
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
