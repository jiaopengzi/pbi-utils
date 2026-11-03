// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SharedWriteAdmissionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证独立GUI、核心MCP与CLI业务入口共享写准入和未知提交保护, 仅使用临时存储.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Settings;
using PbiUtils.Infrastructure.Execution;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Commands;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>跨入口共享真实账本, 存储替身只注入提交后的故障或确定性门闩.</summary>
public sealed class SharedWriteAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-shared-write-" + Guid.NewGuid().ToString("N"));
    private static readonly LocalUdfDefinition Definition = new(1, "Local.Admission", "1.0.0", "() => 1", "", []);

    /// <summary>GUI真实保存后回读失败留下待核验, 独立GUI、模型MCP、核心MCP与CLI均不能继续写入.</summary>
    [Fact]
    public async Task GuiUnknownCommitBlocksAllIndependentWriters()
    {
        var output = new ConcurrentQueue<JsonElement>();
        using var gui = CreateGui(new FaultingSettingsStore(root), output);
        var response = await GuiCall(gui, output, "ui.saveSettings", new { locale = "en-us" });
        Assert.Equal("EXECUTION_FAILED", Error(response));
        Assert.Equal("en-us", new LocalUiSettingsStore(root).Read().Locale);
        var pending = Assert.Single(new ExecutionJournal(root).List()).Record;
        Assert.Equal("ui.saveSettings", pending.Method);
        Assert.Equal("needs_verification", pending.State);
        Assert.True(pending.CommitStarted);
        await AssertAllWritersBlockedAsync("EXECUTION_NEEDS_VERIFICATION");
    }

    /// <summary>核心文件提交成功但终态丢失时保留commit_started, 新连接不能按本地运行时状态跳过持久账本.</summary>
    [Fact]
    public async Task InterruptedCoreReceiptBlocksAllIndependentWriters()
    {
        var journal = new ExecutionJournal(root);
        // 模拟宿主恰在真实文件提交之后终止; 不伪造已完成或回读失败的业务结果.
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "udf_local_create", Json(new { definition = Definition }), "local-files"))
            await new LocalUdfStore(root).CreateAsync(Definition, default, execution.CommitStarted);
        var pending = Assert.Single(journal.List()).Record;
        Assert.Equal("commit_started", pending.State);
        Assert.True(pending.CommitStarted);
        Assert.Single(await new LocalUdfStore(root).ListAsync(default));
        await AssertAllWritersBlockedAsync("EXECUTION_NEEDS_VERIFICATION");
    }

    /// <summary>CLI已记录提交而后续提交通知失败必须保守待核验, 不因另一个入口未见该异常而放行.</summary>
    [Fact]
    public async Task CliUnknownCommitBlocksAllIndependentWriters()
    {
        var arguments = await CliCreateArgumentsAsync();
        await Assert.ThrowsAsync<IOException>(() => CliBusinessCommands.RunAsync(arguments, root, NullLoggerFactory.Instance,
            default, committing: () => throw new IOException("isolated-commit-notification"), workingDirectory: root));
        var pending = Assert.Single(new ExecutionJournal(root).List()).Record;
        Assert.Equal("udf_local_create", pending.Method);
        Assert.Equal("needs_verification", pending.State);
        Assert.True(pending.CommitStarted);
        Assert.Empty(await new LocalUdfStore(root).ListAsync(default));
        await AssertAllWritersBlockedAsync("EXECUTION_NEEDS_VERIFICATION");
    }

    /// <summary>一个GUI持有共享写准入时其他连接可读取但不能写, 真实完成后后续GUI保存合并最新字段.</summary>
    [Fact]
    public async Task ActiveGuiWriteBlocksOtherContextsUntilItsActualCompletion()
    {
        using var store = new GatedSettingsStore(root);
        var firstOutput = new ConcurrentQueue<JsonElement>();
        var secondOutput = new ConcurrentQueue<JsonElement>();
        using var first = CreateGui(store, firstOutput);
        using var second = CreateGui(new LocalUiSettingsStore(root), secondOutput);
        var pending = Task.Run(() => GuiCall(first, firstOutput, "ui.saveSettings", new { locale = "en-us" }));
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = await GuiCall(second, secondOutput, "ui.settings", new { });
            Assert.Equal("zh-cn", read.GetProperty("result").GetProperty("locale").GetString());
            await AssertAllWritersBlockedAsync("USER_DATA_BUSY");
            Assert.False(pending.IsCompleted);
        }
        finally { store.Release.Set(); }
        Assert.False((await pending).TryGetProperty("error", out _));
        var next = await GuiCall(second, secondOutput, "ui.saveSettings", new { showRecentFiles = false });
        Assert.False(next.TryGetProperty("error", out _));
        Assert.Equal(new UiSettings("en-us", false), new LocalUiSettingsStore(root).Read());
        var records = new ExecutionJournal(root).List();
        Assert.Equal(2, records.Count);
        Assert.All(records, item =>
        {
            Assert.Equal("ui.saveSettings", item.Record.Method);
            Assert.Equal("completed", item.Record.State);
            Assert.True(item.Record.CommitStarted);
        });
    }

    /// <summary>使用全部真实分发入口验证写入拒绝、只读可用和持久业务状态不变, 不仅检查底层锁.</summary>
    /// <param name="code">活跃写入或未知提交对应的精确拒绝码.</param>
    private async Task AssertAllWritersBlockedAsync(string code)
    {
        var before = new LocalUiSettingsStore(root).Read();
        var localBefore = await new LocalUdfStore(root).ListAsync(default);
        var output = new ConcurrentQueue<JsonElement>();
        using var gui = CreateGui(new LocalUiSettingsStore(root), output);
        Assert.Equal(code, Error(await GuiCall(gui, output, "ui.saveSettings", new { daxFormatStyle = "short" })));
        var read = await GuiCall(gui, output, "ui.settings", new { });
        Assert.False(read.TryGetProperty("error", out _));
        using var desktopMcp = CreateGui(new LocalUiSettingsStore(root), new());
        var desktopError = await Assert.ThrowsAsync<AppException>(() => desktopMcp.InvokeMcpAsync("ui.saveSettings", Json(new { locale = "en-us" }), default));
        Assert.Equal(code, desktopError.Code);
        await using var coreMcp = new McpBusinessRuntime(true, root, NullLoggerFactory.Instance);
        var coreResult = await coreMcp.CallAsync(new CallToolRequestParams
        {
            Name = "udf_local_create",
            Arguments = Json(new { definition = Definition }).Deserialize<Dictionary<string, JsonElement>>()
        }, default);
        Assert.True(coreResult.IsError);
        Assert.Equal(code, Error(coreResult.StructuredContent!.Value));
        var arguments = await CliCreateArgumentsAsync();
        var cliError = await Assert.ThrowsAsync<AppException>(() => CliBusinessCommands.RunAsync(
            arguments, root, NullLoggerFactory.Instance, default, workingDirectory: root));
        Assert.Equal(code, cliError.Code);
        Assert.Equal(before, new LocalUiSettingsStore(root).Read());
        Assert.Equal(localBefore, await new LocalUdfStore(root).ListAsync(default));
        if (code == "EXECUTION_NEEDS_VERIFICATION")
        {
            var journal = new ExecutionJournal(root);
            Assert.Single(journal.List());
            Assert.Equal(code, Assert.Throws<AppException>(journal.RequireResetSafe).Code);
        }
    }

    /// <summary>每次创建独立桥接会话, 仅共享真实用户根与账本, 不共享请求或模型上下文.</summary>
    private BridgeSession CreateGui(IUiSettingsStore store, ConcurrentQueue<JsonElement> output)
        => new(new(false, null), new(new RuntimeEnvironment()), text => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)),
            "test", uiSettings: new(store), mcpJournal: new(root));

    /// <summary>生成真实CLI输入文件; 命令读取使用其生产严格JSON契约, 不是模拟结果.</summary>
    private async Task<string[]> CliCreateArgumentsAsync()
    {
        Directory.CreateDirectory(root);
        var request = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(Definition, JsonSerializerOptions.Web));
        return ["udf", "local", "create", "--request", request];
    }

    /// <summary>使用唯一请求身份收取真实GUI信封, 并发事件不能冒充本请求响应.</summary>
    private static async Task<JsonElement> GuiCall(BridgeSession session, ConcurrentQueue<JsonElement> output, string method, object parameters)
    {
        var id = Guid.NewGuid().ToString("N");
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));
        return Assert.Single(output, item => item.TryGetProperty("id", out var value) && value.GetString() == id);
    }

    /// <summary>读取真实错误信封, 缺错误字段直接使测试失败.</summary>
    private static string? Error(JsonElement response) => response.GetProperty("error").GetProperty("code").GetString();

    /// <summary>保持产品Web命名策略生成测试请求.</summary>
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>真实偏好已保存之后注入未知失败, 确认产品不会误判为未提交.</summary>
    private sealed class FaultingSettingsStore(string root) : IUiSettingsStore
    {
        public UiSettings Read() => new LocalUiSettingsStore(root).Read();

        /// <summary>正常执行真实存储及提交回调, 随后模拟权威结果丢失.</summary>
        public UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null)
        {
            new LocalUiSettingsStore(root).Save(patch, token, committing);
            throw new IOException("isolated-post-commit-failure");
        }
    }

    /// <summary>在真实保存之前持有业务门闩, 不用定时延迟猜测写入竞争.</summary>
    private sealed class GatedSettingsStore(string root) : IUiSettingsStore, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public UiSettings Read() => new LocalUiSettingsStore(root).Read();

        /// <summary>等待测试明确放行后执行真实提交, 超时失败而不留下后台等待线程.</summary>
        public UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null)
        {
            Entered.SetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
            return new LocalUiSettingsStore(root).Save(patch, token, committing);
        }

        /// <summary>仅释放当前测试拥有的同步句柄.</summary>
        public void Dispose() => Release.Dispose();
    }

    /// <summary>所有连接释放后仅清理本次随机临时根, 不读取用户目录或模型.</summary>
    public void Dispose()
    {
        UserDataStore.Check(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
