// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LocalModelFileTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享文件连接的 Desktop 注册、读取后身份复核和失败取消清理.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>以受控适配器验证共享业务边界, 不启动 Desktop 或读写用户模型.</summary>
public sealed class LocalModelFileTests
{
    /// <summary>历史提交与选择位于共享门闩内; 旧模型读取中切换不会先写历史, 历史失败也保留原模型.</summary>
    [Fact]
    public async Task SelectionRejectsBusyBeforeCommittingHistoryAndPreservesCurrentOnHistoryFailure()
    {
        var reader = new SelectionReader();
        using var workspace = new ChangeWorkspace();
        using var edits = new ModelEditService(reader, workspace);
        var previous = await reader.ReadAsync("previous.pbip", default); edits.Select(previous);
        reader.Block = true;
        var read = edits.ReadAsync(previous.ProjectPath, default);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var records = 0;
        Assert.Equal("BUSY", Assert.Throws<AppException>(() => edits.Select(previous with { ProjectPath = "next.pbix" }, () => records++)).Code);
        Assert.Equal(0, records);
        reader.Release.SetResult(); await read;
        Assert.Throws<IOException>(() => edits.Select(previous with { ProjectPath = "next.pbix" }, () => throw new IOException("test-history-failure")));
        Assert.Equal(previous, await edits.ReadAsync(previous.ProjectPath, default));
    }

    /// <summary>文件入口必须经过平台打开、PID 注册、权威模型读取和最终文件复核.</summary>
    [Theory]
    [InlineData("report.pbip")]
    [InlineData("report.pbix")]
    public async Task FileConnectUsesTheReadyInstanceAndVerifiesAfterReading(string path)
    {
        var reader = new Reader(); var opener = new Opener(reader);
        var snapshot = await new ModelConnectionService(reader, localFiles: opener).FileAsync(path, default);
        Assert.Equal(path, opener.Opened);
        Assert.Equal(456, reader.ProcessId);
        Assert.True(opener.Verified);
        Assert.Equal("desktop", snapshot.Connection!.Kind);
        Assert.Contains(snapshot.ProjectPath, reader.Registered);
    }

    /// <summary>窗口在读取期间切换或接受取消时必须撤销新注册, 保留先前当前注册.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileIdentityFailureOrCancellationDoesNotRetainNewTarget(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader(); reader.Registered.Add("model://previous");
        var opener = new Opener(reader) { VerifyAction = () => { if (cancel) cancellation.Cancel(); else throw new AppException("DESKTOP_FILE_CHANGED", "测试窗口已切换"); } };
        var task = new ModelConnectionService(reader, localFiles: opener).FileAsync("report.pbix", cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Equal("DESKTOP_FILE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => task)).Code);
        Assert.Equal(["model://previous"], reader.Registered);
    }

    /// <summary>Desktop 读取失败同样撤销本次目标, 没有权威快照则不进行最终文件复核.</summary>
    [Fact]
    public async Task ReadFailureCleansRegistration()
    {
        var reader = new Reader { Fail = true }; var opener = new Opener(reader);
        await Assert.ThrowsAsync<AppException>(() => new ModelConnectionService(reader, localFiles: opener).FileAsync("report.pbip", default));
        Assert.Empty(reader.Registered); Assert.False(opener.Verified);
    }

    /// <summary>父窗口和文件不变但引擎在最终文件复核期间变化时, 不发布已经失效的模型目标.</summary>
    [Fact]
    public async Task EngineChangeDuringFileVerificationCleansTheReadTarget()
    {
        var reader = new Reader(); reader.Registered.Add("model://previous");
        var opener = new Opener(reader) { VerifyAction = () => reader.EngineChanged = true };
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", (await Assert.ThrowsAsync<AppException>(() => new ModelConnectionService(reader, localFiles: opener).FileAsync("report.pbix", default))).Code);
        Assert.Equal(2, reader.Verifications);
        Assert.Equal(["model://previous"], reader.Registered);
    }

    /// <summary>已打开实例快速连接同样复核读取后的引擎身份, 失败撤销注册.</summary>
    [Fact]
    public async Task QuickConnectionVerifiesItsEngineAfterReading()
    {
        var reader = new Reader { EngineChanged = true };
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", (await Assert.ThrowsAsync<AppException>(() => new ModelConnectionService(reader).LocalAsync(456, default))).Code);
        Assert.True(reader.Read); Assert.Empty(reader.Registered);
    }

    /// <summary>缺少平台文件打开适配器时明确失败, 不退回离线 PBIP 读取.</summary>
    [Fact]
    public async Task MissingDesktopAdapterDoesNotReadFiles()
    {
        var reader = new Reader();
        Assert.Equal("DESKTOP_OPEN_UNSUPPORTED", (await Assert.ThrowsAsync<AppException>(() => new ModelConnectionService(reader).FileAsync("report.pbip", default))).Code);
        Assert.False(reader.Read); Assert.Empty(reader.Registered);
    }

    /// <summary>假打开器保留阶段顺序, 允许测试复核失败和取消.</summary>
    private sealed class Opener(Reader reader) : ILocalModelFileOpener
    {
        public string? Opened { get; private set; }
        public bool Verified { get; private set; }
        public Action? VerifyAction { get; init; }
        /// <summary>返回已绑定的假实例, 不启动任何进程.</summary>
        public Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); Opened = path; return Task.FromResult(new LocalModelFileTarget(path, 456, DateTime.UnixEpoch)); }
        /// <summary>断言读取先于复核, 并施加可控身份变化或取消.</summary>
        public Task VerifyAsync(LocalModelFileTarget target, CancellationToken token) { Assert.True(reader.Read); Verified = true; VerifyAction?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    /// <summary>注册和读取完全驻留内存, 只观察共享服务的清理行为.</summary>
    private sealed class Reader : IConnectedModelReader
    {
        public HashSet<string> Registered { get; } = [];
        public int ProcessId { get; private set; }
        public bool Read { get; private set; }
        public bool Fail { get; init; }
        public bool EngineChanged { get; set; }
        public int Verifications { get; private set; }
        /// <summary>创建与文件打开器返回 PID 对应的新注册.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token) { ProcessId = processId; var id = "model://new"; Registered.Add(id); return Task.FromResult(id); }
        /// <summary>模拟已注册引擎重启, 不把父窗口仍存活当成连接成功.</summary>
        public Task VerifyLocalAsync(string id, CancellationToken token) { Verifications++; token.ThrowIfCancellationRequested(); if (EngineChanged) throw new AppException("POWER_BI_INSTANCE_CLOSED", "测试引擎已重启"); return Task.CompletedTask; }
        /// <summary>模拟模型读取或权威读取失败.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) { Read = true; if (Fail) throw new AppException("TEST_READ_FAILED", "测试读取失败"); return Task.FromResult(new ProjectSnapshot(projectPath, "id", "baseline", "Desktop", "unavailable", "1", null, new("report", 1600, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"))); }
        /// <summary>本测试不提供远端连接.</summary>
        public string RegisterRemote(RemoteModelAddress address) => throw new NotSupportedException();
        /// <summary>精确撤销失败的新注册.</summary>
        public void Forget(string id) => Registered.Remove(id);
        /// <summary>清理内存目标.</summary>
        public void Disconnect() => Registered.Clear();
    }

    /// <summary>门闩仅阻塞模型读取, 验证选择与宿主历史写入的真正并发边界.</summary>
    private sealed class SelectionReader : IProjectReader
    {
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>读取有界内存快照, 不接触磁盘或实际 Desktop.</summary>
        public async Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken)
        {
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return new(projectPath, "id", "baseline", "Desktop", "unavailable", "1", null, new("report", 1600, "en-US", [], [], [], 0, 0), [], [], []);
        }
    }
}
