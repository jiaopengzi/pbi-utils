// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ChangeBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 可控门闩覆盖模型保存前后取消竞态, 不依赖计时猜测.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>保存临界点是取消契约的一部分, 不能先写模型再响应已取消.</summary>
public sealed class ChangeBridgeTests
{
    /// <summary>提交前取消保留草稿; 提交后拒绝取消并返回保存快照.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAndAfterCommitBarrier(bool commitFirst)
    {
        var workspace = new Workspace(commitFirst); var reader = new Reader();
        using var edits = new ModelEditService(reader, workspace);
        ConcurrentQueue<JsonElement> output = new();
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            text => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", projects: new(edits),
            selectProject: _ => Task.FromResult<string?>("source.pbip"), connections: new TestLocalFileConnection(reader).Service(), edits: edits);
        Task Call(string id, string method, object parameters) => bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters, connectionRevision = id == "open" ? 0 : 1 }));
        await Call("open", "project.open", new { });
        var apply = Call("apply", "model.edit", new { operation = "table.set", objectName = "Sales", property = "description", value = "test", table = "", pageId = "" });
        await workspace.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Call("cancel", "request.cancel", new { requestId = "apply" });
        Assert.Equal(!commitFirst, output.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
        workspace.Release.TrySetResult();
        await apply.WaitAsync(TimeSpan.FromSeconds(3));
        var final = output.Single(m => m.TryGetProperty("id", out var id) && id.GetString() == "apply");
        if (commitFirst) Assert.False(final.GetProperty("result").GetProperty("hasPendingChanges").GetBoolean());
        else Assert.Equal("CANCELLED", final.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(commitFirst, workspace.Published);
        Assert.Equal(!commitFirst, bridge.HasPendingChanges);
        Assert.Equal(0, bridge.PendingCount);
    }
    /// <summary>提交中关闭只能停止接收, 连接必须等模型保存结束后再断开, 连续关闭只释放一次.</summary>
    [Fact]
    public async Task ShutdownDrainsCommittedSaveBeforeDisconnecting()
    {
        var workspace = new Workspace(true); var reader = new Reader();
        using var edits = new ModelEditService(reader, workspace);
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
            projects: new(edits), selectProject: _ => Task.FromResult<string?>("source.pbip"), connections: new(reader, localFiles: new TestLocalFileConnection(reader)), edits: edits);
        Task Call(string id, string method, object parameters) => bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters, connectionRevision = id == "open" ? 0 : 1 }));
        await Call("open", "project.open", new { });
        var save = Call("save", "model.edit", new { operation = "table.set", objectName = "Sales", property = "description", value = "fixture", table = "", pageId = "" });
        await workspace.Entered.Task;
        var close = bridge.ShutdownAsync(); var again = bridge.ShutdownAsync();
        Assert.False(close.IsCompleted); Assert.Equal("Draining", bridge.Lifecycle); Assert.Equal(0, reader.Disconnections);
        workspace.Release.SetResult(); await Task.WhenAll(save, close, again);
        Assert.True(workspace.Published); Assert.Equal(1, reader.Disconnections); Assert.Equal("Disposed", bridge.Lifecycle); Assert.Equal(0, bridge.PendingCount);
    }
    private sealed class Reader : IConnectedModelReader
    {
        public int Disconnections { get; private set; }
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token) => Task.FromResult("source.pbip");
        public string RegisterRemote(RemoteModelAddress address) => throw new InvalidOperationException();
        public void Forget(string id) { }
        public void Disconnect() => Disconnections++;
        /// <summary>固定快照仅用于取消竞争, 不访问文件.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(new ProjectSnapshot(path, "workspace", "baseline", "TMSL", "PBIR", "1.0", "4.0", new("m", 1606, "en-US", [], [], [], 0, 0), [], [], []));
    }
    private sealed class Workspace(bool commitFirst) : IModelEditWorkspace
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Published { get; private set; }
        /// <summary>创建固定内存草稿, 没有实际源文件.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot snapshot, CancellationToken token) => Task.FromResult<IModelDraft>(new Draft(snapshot, this, commitFirst));
        private sealed class Draft(ProjectSnapshot initial, Workspace owner, bool commitsFirst) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = initial;
            /// <summary>只标记草稿, 验证桥接暂存行为.</summary>
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token) { Snapshot = Snapshot with { HasPendingChanges = true }; return Task.CompletedTask; }
            /// <summary>固定门闩控制提交前后边界, 取消只在提交前有效.</summary>
            public async Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                if (commitsFirst) committing?.Invoke();
                owner.Entered.TrySetResult();
                await owner.Release.Task;
                if (!commitsFirst) { token.ThrowIfCancellationRequested(); committing?.Invoke(); }
                owner.Published = true;
                return Snapshot with { HasPendingChanges = false };
            }
            /// <summary>测试草稿没有外部资源.</summary>
            public void Dispose() { }
        }
    }
}
