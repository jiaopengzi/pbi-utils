// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证实际桥接协议, 来源策略, 取消竞争与请求资源清理.

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

/// <summary>直接运行实际桌面协议源代码, 不冒充 Windows UI 测试.</summary>
public sealed class BridgeTests
{
    /// <summary>和 TypeScript 共享样例, 验证实际 C# 白名单响应字段.</summary>
    [Fact]
    public async Task SharedContractFixtures()
    {
        var fixtures = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "bridge-contracts.json")));
        ConcurrentQueue<JsonElement> output = new();
        var reader = new ContractModel();
        using var edits = new ModelEditService(reader, reader);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", projects: new(edits),
            selectProject: _ => Task.FromResult<string?>("contract.pbip"), connections: new TestLocalFileConnection(reader).Service(), edits: edits);
        foreach (var fixture in fixtures.EnumerateArray())
        {
            var request = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                id = fixture.GetProperty("id").GetString(),
                method = fixture.GetProperty("method").GetString(),
                @params = fixture.GetProperty("params"),
                connectionRevision = output.Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).DefaultIfEmpty().Max()
            });
            await session.ReceiveAsync(OriginPolicy.Production, request);
            var response = output.Last();
            Assert.Equal(fixture.GetProperty("id").GetString(), response.GetProperty("id").GetString());
            if (fixture.TryGetProperty("error", out var expectedError)) AssertSubset(expectedError, response.GetProperty("error"));
            else if (fixture.TryGetProperty("result", out var expectedResult)) AssertSubset(expectedResult, response.GetProperty("result"));
            else Assert.True(response.GetProperty("result").TryGetProperty(fixture.GetProperty("expectedField").GetString()!, out _));
        }
    }

    /// <summary>固定字段金样与实际 dispatcher 返回值比较, 时钟和运行身份仍由宿主生成.</summary>
    private static void AssertSubset(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (var field in expected.EnumerateObject()) AssertSubset(field.Value, actual.GetProperty(field.Name));
        else Assert.True(JsonElement.DeepEquals(expected, actual), $"契约值不一致: {expected} / {actual}");
    }

    private sealed class ContractModel : IProjectReader, IModelEditWorkspace
    {
        private ProjectSnapshot snapshot = new("contract.pbip", "contract", "before", "TMDL", "PBIR", "4.2", "4.0", new("contract", 1606, "en-US", [new("Sales", "before", false, [], [])], [], [], 0, 0), [], [], []);
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(snapshot);
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new ContractDraft(baseline, this));
        private sealed class ContractDraft(ProjectSnapshot initial, ContractModel owner) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = initial;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token)
            { Snapshot = Snapshot with { HasPendingChanges = true }; return Task.CompletedTask; }
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            { committing?.Invoke(); owner.snapshot = Snapshot with { BaselineId = "after", HasPendingChanges = false, SaveResult = new(true, true, true, "文件已保存") }; return Task.FromResult(owner.snapshot); }
            public void Dispose() { }
        }
    }

    /// <summary>创建带线程安全消息收集器的独立会话.</summary>
    private static BridgeSession Session(IPowerBiReader reader, ConcurrentQueue<JsonElement> messages, TimeSpan? timeout = null) =>
        new(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", timeout, powerBi: new(reader, TimeProvider.System));

    /// <summary>建立标准请求文档.</summary>
    private static string Request(string id, string method, object? parameters = null) =>
        JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters ?? new { } });

    /// <summary>恶意主机、端口和 Release 开发地址被拒绝.</summary>
    [Fact]
    public void ExactOriginPolicy()
    {
        var policy = new OriginPolicy(false, null);
        Assert.True(policy.Allows(OriginPolicy.Production + "/index.html"));
        foreach (var origin in new[] { "https://app.pbi-utils.local.evil/", "https://app.pbi-utils.local:444/", "http://app.pbi-utils.local", "https://user@app.pbi-utils.local/", OriginPolicy.Development }) Assert.False(policy.Allows(origin));
        Assert.Throws<InvalidOperationException>(() => new OriginPolicy(false, OriginPolicy.Development));
        Assert.Throws<InvalidOperationException>(() => new OriginPolicy(true, "http://127.0.0.1:5174"));
        Assert.True(new OriginPolicy(true, OriginPolicy.Development).Allows(OriginPolicy.Development));
    }

    /// <summary>畸形、未知协议方法、参数和超大消息均拒绝.</summary>
    [Fact]
    public async Task RejectsMalformedMessagesAndSpoofedOrigin()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var session = Session(new BridgeTestReader(), output);
        await session.ReceiveAsync("https://evil.test", Request("x", "doctor"));
        Assert.Empty(output);
        var cases = new[] { "{", "[]", Request("x", "shell"), Request("x", "powerbi.instances", new { name = 42 }), Request("x", "doctor", new { secret = true }), Request("x", "doctor").Replace("\"protocolVersion\":1", "\"protocolVersion\":2"), new string('x', 65537) };
        foreach (var item in cases)
        {
            await session.ReceiveAsync(OriginPolicy.Production, item);
            Assert.True(output.TryDequeue(out var response));
            Assert.True(response.TryGetProperty("error", out _));
            Assert.Equal(0, session.PendingCount);
        }
    }

    /// <summary>取消被受理后原请求只能返回 CANCELLED, 重复取消不新增终态.</summary>
    [Fact]
    public async Task CancelAcknowledgementAndSingleTerminal()
    {
        var spy = new BridgeTestReader { Wait = true };
        ConcurrentQueue<JsonElement> output = new();
        using var session = Session(spy, output);
        var original = session.ReceiveAsync(OriginPolicy.Production, Request("scan", "powerbi.instances"));
        await spy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.ReceiveAsync(OriginPolicy.Production, Request("scan", "doctor"));
        Assert.Contains(output, x => x.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "DUPLICATE_ID");
        await session.ReceiveAsync(OriginPolicy.Production, Request("cancel", "request.cancel", new { requestId = "scan" }));
        await original.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(output, x => x.TryGetProperty("result", out var result) && result.TryGetProperty("accepted", out var accepted) && accepted.GetBoolean());
        Assert.True(spy.Cancelled);
        Assert.Single(output, x => x.TryGetProperty("error", out var e) && e.GetProperty("code").GetString() == "CANCELLED");
        await session.ReceiveAsync(OriginPolicy.Production, Request("late", "request.cancel", new { requestId = "scan" }));
        Assert.False(output.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>超时与关闭传播到服务, 完成后清理在途资源.</summary>
    [Fact]
    public async Task TimeoutAndDisposeReleaseRequests()
    {
        var spy = new BridgeTestReader { Wait = true };
        ConcurrentQueue<JsonElement> output = new();
        using var session = Session(spy, output, TimeSpan.FromMilliseconds(50));
        await session.ReceiveAsync(OriginPolicy.Production, Request("a", "powerbi.instances")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("TIMEOUT", output.Last().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, session.PendingCount);
        Assert.True(spy.Cancelled);
        var closingReader = new BridgeTestReader { Wait = true };
        using var closing = Session(closingReader, output);
        var run = closing.ReceiveAsync(OriginPolicy.Production, Request("b", "powerbi.instances"));
        await closingReader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        closing.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, closing.PendingCount);
        Assert.True(closingReader.Cancelled);
    }

    /// <summary>批准移除的旧演示方法返回未知方法, 不再保留隐藏执行入口.</summary>
    /// <param name="method">已移除的桥接方法.</param>
    [Theory]
    [InlineData("hello")]
    [InlineData("agent.hello")]
    public async Task RemovedDemoMethodsAreRejected(string method)
    {
        ConcurrentQueue<JsonElement> output = new();
        using var session = Session(new BridgeTestReader(), output);
        await session.ReceiveAsync(OriginPolicy.Production, Request("removed", method));
        Assert.Equal("UNKNOWN_METHOD", Assert.Single(output).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>模型保存设置已移除, 读取和更新入口均明确拒绝, 不保留隐藏设置能力.</summary>
    /// <param name="method">已移除的模型保存设置方法.</param>
    [Theory]
    [InlineData("model.saveSettings")]
    [InlineData("model.updateSaveSettings")]
    public async Task RemovedModelSaveSettingsMethodsAreRejected(string method)
    {
        ConcurrentQueue<JsonElement> output = new();
        using var session = Session(new BridgeTestReader(), output);
        object parameters = method == "model.updateSaveSettings" ? new { confirmBeforeSave = false } : new { };
        await session.ReceiveAsync(OriginPolicy.Production, Request("removed-model-settings", method, parameters));
        Assert.Equal("UNKNOWN_METHOD", Assert.Single(output).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, session.PendingCount);
    }
}
