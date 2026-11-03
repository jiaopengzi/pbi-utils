// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ExternalToolConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证外部工具启动参数、认证上下文、一次消费及失败取消后的连接保留, 不访问真实模型.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ExternalToolConnectionTests
{
    /// <summary>保留无参启动, 严格接受回环端口与数据库, 顺序不影响准确目标.</summary>
    [Theory]
    [InlineData("localhost:50432")]
    [InlineData("LOCALHOST:50432")]
    [InlineData("127.0.0.1:50432")]
    [InlineData("[::1]:50432")]
    public void ParsesOnlyPairedLocalArguments(string server)
    {
        Assert.Null(ExternalToolLaunchTarget.Parse([]));
        Assert.Equal(new(50432, "model database"), ExternalToolLaunchTarget.Parse(["--server", server, "--database", "model database"]));
        Assert.Equal(new(50432, "model database"), ExternalToolLaunchTarget.Parse(["--database", "model database", "--server", server]));
    }

    /// <summary>远端、别名、连接字符串和非数字端口全部在发现或连接前拒绝.</summary>
    [Theory]
    [InlineData("remote.example:50432")]
    [InlineData("localhost.evil:50432")]
    [InlineData("http://localhost:50432")]
    [InlineData("127.1:50432")]
    [InlineData("0.0.0.0:50432")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:+50432")]
    [InlineData("localhost:50432;Password=x")]
    public void RejectsUntrustedServers(string server) => Assert.Equal("EXTERNAL_TOOL_ARGUMENTS_INVALID",
        Assert.Throws<AppException>(() => ExternalToolLaunchTarget.Parse(["--server", server, "--database", "model"])).Code);

    /// <summary>重复、缺失、未知及越界参数不能作为部分有效目标启动.</summary>
    [Fact]
    public void RejectsIncompleteDuplicateUnknownOrInvalidDatabaseArguments()
    {
        string[][] cases = [["--server", "localhost:1"], ["--server", "localhost:1", "--server", "localhost:1"],
            ["--database", "a", "--database", "b"], ["--server", "localhost:1", "--allow-write", "true"],
            ["--server", "localhost:1", "--database", ""], ["--server", "localhost:1", "--database", "a\nb"],
            ["--server", "localhost:1", "--database", new string('x', 257)], ["--server", "localhost:1", "--database"]];
        foreach (var args in cases) Assert.Equal("EXTERNAL_TOOL_ARGUMENTS_INVALID", Assert.Throws<AppException>(() => ExternalToolLaunchTarget.Parse(args)).Code);
    }

    /// <summary>只有 GUI 握手可带启动目标, 远端调用的 kind 或写标记不能模拟该入口.</summary>
    [Fact]
    public void HostOptionsBindLaunchTargetToGui()
    {
        var target = new ExternalToolLaunchTarget(50432, "model");
        BusinessHostIdentity.ValidateOptions(new("gui", true, ExternalToolTarget: target));
        foreach (var kind in new[] { "mcp", "cli" })
            Assert.Equal("BUSINESS_HOST_CLIENT_INVALID", Assert.Throws<AppException>(() => BusinessHostIdentity.ValidateOptions(new(kind, true, ExternalToolTarget: target))).Code);
        Assert.Throws<AppException>(() => BusinessHostIdentity.ValidateOptions(new("gui", true, ExternalToolTarget: target with { Port = 0 })));
    }

    /// <summary>网页只能消费启动时绑定的目标, 首次成功后代际更新且任何重放被拒绝.</summary>
    [Fact]
    public async Task StartupContextConnectsOnceAndRejectsInjectedParameters()
    {
        using var fixture = new Fixture();
        await fixture.Call("app.info");
        Assert.True(fixture.Last.GetProperty("result").GetProperty("hasExternalToolTarget").GetBoolean());
        await fixture.Call("project.connectExternalTool", new { server = "remote" });
        Assert.Equal("VALIDATION_ERROR", fixture.Error);
        Assert.Equal(0, fixture.Reader.Registrations);
        await fixture.Call("project.connectExternalTool");
        Assert.Equal("desktop", fixture.Last.GetProperty("result").GetProperty("connection").GetProperty("kind").GetString());
        Assert.Equal(new(50432, "model"), fixture.Reader.ObservedTarget);
        Assert.Equal(1, fixture.Reader.Verifications);
        Assert.Equal(1, fixture.Last.GetProperty("connectionRevision").GetInt64());
        await fixture.Call("app.info");
        Assert.False(fixture.Last.GetProperty("result").GetProperty("hasExternalToolTarget").GetBoolean());
        await fixture.Call("project.connectExternalTool");
        Assert.Equal("EXTERNAL_TOOL_TARGET_UNAVAILABLE", fixture.Error);
        Assert.Equal(1, fixture.Reader.Registrations);
    }

    /// <summary>读取或最终复核失败均清理新注册并保留原模型; 失败启动意图不可重试重放.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLaunchPreservesPreviousModel(bool failVerification)
    {
        using var fixture = new Fixture();
        await fixture.Call("project.connectLocal", new { processId = "100" });
        var original = fixture.Last.GetProperty("result").GetProperty("projectPath").GetString();
        fixture.Reader.Failure = failVerification ? "verify" : "read";
        await fixture.Call("project.connectExternalTool");
        Assert.Equal(failVerification ? "POWER_BI_INSTANCE_CLOSED" : "EXTERNAL_TOOL_DATABASE_MISMATCH", fixture.Error);
        fixture.Reader.Failure = null;
        await fixture.Call("project.inspect");
        Assert.Equal(original, fixture.Last.GetProperty("result").GetProperty("projectPath").GetString());
        Assert.Single(fixture.Reader.Targets);
        await fixture.Call("project.connectExternalTool");
        Assert.Equal("EXTERNAL_TOOL_TARGET_UNAVAILABLE", fixture.Error);
    }

    /// <summary>取消等原读取排空后清理注册, 不发布迟到模型且拒绝并发切换.</summary>
    [Fact]
    public async Task CancelledLaunchDrainsAndDoesNotReplay()
    {
        using var fixture = new Fixture();
        fixture.Reader.Block = true;
        var pending = fixture.Call("project.connectExternalTool", id: "launch");
        await fixture.Reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("request.cancel", new { requestId = "launch" });
        Assert.True(fixture.Last.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.False(pending.IsCompleted);
        await fixture.Call("project.connectLocal", new { processId = "100" });
        Assert.Equal("BUSY", fixture.Error);
        fixture.Reader.Release.SetResult();
        await pending;
        Assert.Equal("CANCELLED", fixture.Error);
        Assert.Empty(fixture.Reader.Targets);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        await fixture.Call("project.connectExternalTool");
        Assert.Equal("EXTERNAL_TOOL_TARGET_UNAVAILABLE", fixture.Error);
    }

    /// <summary>夹具只创建内存模型和实际 BridgeSession, 无进程、文件或网络副作用.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ConcurrentQueue<JsonElement> messages = new();
        internal Reader Reader { get; } = new();
        internal BridgeSession Bridge { get; }
        internal JsonElement Last => messages.Last();
        internal string? Error => Last.GetProperty("error").GetProperty("code").GetString();
        internal Fixture() => Bridge = new(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", projects: new(Reader), connections: new(Reader),
            externalToolTarget: new(50432, "model"));
        /// <summary>请求代际采用此前真实响应, 与前端类型化桥接相同.</summary>
        internal Task Call(string method, object? parameters = null, string? id = null) => Bridge.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                id = id ?? Guid.NewGuid().ToString("N"),
                method,
                @params = parameters ?? new { },
                connectionRevision = messages.Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).LastOrDefault()
            }));
        /// <summary>测试结束排空并释放本夹具.</summary>
        public void Dispose() => Bridge.Dispose();
    }

    /// <summary>共享连接用例的受控平台边界, 只模拟本机注册与取消, 远端误调用立即失败.</summary>
    private sealed class Reader : IConnectedModelReader
    {
        internal HashSet<string> Targets { get; } = [];
        internal ExternalToolLaunchTarget? ObservedTarget { get; private set; }
        internal int Registrations { get; private set; }
        internal int Verifications { get; private set; }
        internal string? Failure { get; set; }
        internal bool Block { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>记录传入的唯一启动目标.</summary>
        public Task<string> RegisterExternalToolAsync(ExternalToolLaunchTarget target, CancellationToken token)
        { ObservedTarget = target; Registrations++; return RegisterLocalAsync(100, token); }
        /// <summary>分配没有外部资源的假模型句柄.</summary>
        public Task<string> RegisterLocalAsync(int processId, CancellationToken token)
        { var path = "model://" + Guid.NewGuid().ToString("N"); Targets.Add(path); return Task.FromResult(path); }
        /// <summary>复核失败用于模拟读取期间原引擎已经退出.</summary>
        public Task VerifyLocalAsync(string id, CancellationToken token)
        { Verifications++; if (Failure == "verify") throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭"); return Task.CompletedTask; }
        /// <summary>实际取消只在测试显式释放同步 SDK 边界后结束.</summary>
        public async Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
        {
            if (Block) { Entered.TrySetResult(); await Release.Task; }
            token.ThrowIfCancellationRequested();
            if (Failure == "read") throw new AppException("EXTERNAL_TOOL_DATABASE_MISMATCH", "数据库不匹配");
            return new(path, path, "baseline", "desktop", "report", "1", null, new("Model", 1600, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        }
        /// <summary>任何外部工具路径误用远端服务都使测试失败.</summary>
        public string RegisterRemote(RemoteModelAddress address) => throw new InvalidOperationException();
        /// <summary>清理失败或取消注册.</summary>
        public void Forget(string id) => Targets.Remove(id);
        /// <summary>成功连接仅保留新模型.</summary>
        public void Retain(string? id) => Targets.RemoveWhere(target => target != id);
        /// <summary>断开只影响夹具自身.</summary>
        public void Disconnect() => Targets.Clear();
    }
}
