// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LoggingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实 Serilog 文件、级别持久化、业务关联、异常脱敏和日志桥接边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Diagnostics;
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

public sealed class LoggingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-logging-" + Guid.NewGuid().ToString("N"));

    private JsonElement[] Events() => [.. Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef")
        .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))];

    /// <summary>被过滤的终态仍完成裁决, 后续开启日志或释放范围不能补发取消事件.</summary>
    /// <param name="failed">是否以异常终结被过滤的操作.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledTerminalRemainsFinalAfterLevelChanges(bool failed)
    {
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            runtime.Save("Fatal", default);
            var logger = runtime.Factory.CreateLogger("FilteredTerminal");
            var operation = BusinessLog.Begin(logger, "Test.Filtered");
            if (failed) operation.Fail(new InvalidOperationException("private-failure"));
            else operation.Complete();
            runtime.Save("Verbose", default);
            operation.Dispose();
            using var visible = BusinessLog.Begin(logger, "Test.Visible");
            visible.Complete();
        }
        var events = Events();
        Assert.DoesNotContain(events, item => item.TryGetProperty("Operation", out var value) && value.GetString() == "Test.Filtered");
        Assert.Equal(2, events.Count(item => item.TryGetProperty("Operation", out var value) && value.GetString() == "Test.Visible"));
        Assert.DoesNotContain("private-failure", string.Join('\n', events.Select(item => item.GetRawText())));
    }

    [Fact]
    public async Task SwitchIsImmediatePersistedAndInvalidOrCancelledSavePreservesState()
    {
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            var service = new LoggingService(runtime, runtime.Factory.CreateLogger<LoggingService>());
            var logger = runtime.Factory.CreateLogger("LevelTest");
            Assert.Equal("Information", service.Read().Level);
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("HiddenBeforeSwitch");
            service.Save("Debug", CancellationToken.None);
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("VisibleAfterSwitch");
            service.Save("Warning", CancellationToken.None);
            await new PowerBiService(new BridgeTestReader(), TimeProvider.System, runtime.Factory.CreateLogger<PowerBiService>()).ExecuteAsync();
            Assert.Throws<AppException>(() => service.Save("1", CancellationToken.None));
            Assert.Throws<OperationCanceledException>(() => service.Save("Verbose", new CancellationToken(true)));
            using (UserDataStore.Lock(Path.Combine(root, "logging-settings.lock")))
                Assert.Throws<AppException>(() => service.Save("Error", CancellationToken.None));
            Assert.Equal("Warning", service.Read().Level);
        }
        using var restarted = new LoggingRuntime(root, "test");
        Assert.Equal("Warning", restarted.Read().Level);
        var content = string.Join('\n', Events().Select(e => e.GetRawText()));
        Assert.Contains("VisibleAfterSwitch", content);
        Assert.DoesNotContain("HiddenBeforeSwitch", content);
        Assert.DoesNotContain("PowerBi.Execute", content);
        Assert.DoesNotContain("private-name", content);
    }

    [Fact]
    public void CorruptConfigurationUsesVisibleFallbackAndKeepsOriginal()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "logging-settings.json");
        File.WriteAllText(path, "{broken");
        using var runtime = new LoggingRuntime(root, "test");
        Assert.Equal("Information", runtime.Read().Level);
        Assert.NotNull(runtime.Read().Warning);
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task NestedOperationsAndConcurrentRequestsHaveIndependentCorrelation()
    {
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            var logger = runtime.Factory.CreateLogger("CorrelationTest");
            var doctor = new DoctorService(new RuntimeEnvironment(), runtime.Factory.CreateLogger<DoctorService>());
            async Task Request()
            {
                using var operation = BusinessLog.Begin(logger, "Test.Request", root: true);
                await Task.Yield();
                _ = doctor.Execute();
                operation.Complete();
            }
            await Task.WhenAll(Request(), Request());
        }
        var events = Events();
        var requests = events.Where(e => e.GetProperty("Operation").GetString() == "Test.Request" && e.GetProperty("Outcome").GetString() == "Started").ToArray();
        Assert.Equal(2, requests.Select(e => e.GetProperty("RequestId").GetString()).Distinct().Count());
        foreach (var request in requests)
        {
            var correlated = events.Where(e => e.GetProperty("RequestId").GetString() == request.GetProperty("RequestId").GetString()).ToArray();
            Assert.Equal(4, correlated.Length);
            var nested = correlated.Single(e => e.GetProperty("Operation").GetString() == "Doctor.Execute" && e.GetProperty("Outcome").GetString() == "Started");
            Assert.Equal(request.GetProperty("OperationId").GetString(), nested.GetProperty("ParentOperationId").GetString());
            Assert.Single(correlated, e => e.GetProperty("Operation").GetString() == "Doctor.Execute" && e.GetProperty("Outcome").GetString() == "Completed");
        }
    }

    [Fact]
    public async Task FailureContainsSafeTypesAndCancellationKeepsOriginalException()
    {
        var error = new InvalidOperationException("Bearer SECRET_TOKEN password=secret C:\\private\\model.tmdl", new IOException("sk-private-key"));
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            var logger = runtime.Factory.CreateLogger("FailureTest");
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => BusinessLog.Run<int>(logger, "Test.Failure", () => throw error)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BusinessLog.RunAsync(logger, "Test.Cancel", () => Task.FromCanceled(new CancellationToken(true))));
        }
        var events = Events();
        var failure = events.Single(e => e.GetProperty("Outcome").GetString() == "Failed");
        Assert.Contains("IOException", failure.GetProperty("ErrorTypes").GetString());
        Assert.Contains("0x", failure.GetProperty("HResults").GetString());
        var content = string.Join('\n', events.Select(e => e.GetRawText()));
        foreach (var secret in new[] { "SECRET_TOKEN", "password=secret", "private", "sk-private-key" }) Assert.DoesNotContain(secret, content);
        Assert.Equal("Cancelled", events.Last().GetProperty("Outcome").GetString());
        Assert.False(events.Last().TryGetProperty("@l", out _));
    }

    [Fact]
    public async Task BridgeDirectoryIsFixedAndUntrustedInputIsNotLogged()
    {
        ConcurrentQueue<JsonElement> replies = new();
        var opens = 0;
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
                text => replies.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", logging: new(runtime),
                openLogDirectory: _ => { opens++; return Task.CompletedTask; }, logger: runtime.Factory.CreateLogger<BridgeSession>());
            async Task Send(string method, object parameters, string source = OriginPolicy.Production) => await session.ReceiveAsync(source,
                JsonSerializer.Serialize(new { protocolVersion = 1, id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
            await Send("logging.openDirectory", new { path = "SECRET_INJECTED_PATH" });
            Assert.Equal(0, opens);
            Assert.Equal("VALIDATION_ERROR", replies.Last().GetProperty("error").GetProperty("code").GetString());
            await Send("logging.save", new { level = "Verbose", extra = true });
            Assert.Equal("Information", runtime.Read().Level);
            await Send("logging.save", new { level = "Debug" });
            Assert.Equal("Debug", runtime.Read().Level);
            await Send("logging.openDirectory", new { });
            Assert.Equal(1, opens);
            await Send("SECRET_METHOD", new { });
            var previous = replies.Count;
            await Send("logging.openDirectory", new { }, "https://evil.test");
            Assert.Equal(previous, replies.Count);
            Assert.Equal(1, opens);
            Assert.Equal(0, session.PendingCount);
        }
        var content = string.Join('\n', Events().Select(e => e.GetRawText()));
        Assert.DoesNotContain("SECRET_INJECTED_PATH", content);
        Assert.DoesNotContain("SECRET_METHOD", content);
    }

    [Fact]
    public async Task SharedWritersProduceCompleteJsonAndFlushOnDispose()
    {
        using (var first = new LoggingRuntime(root, "test"))
        using (var second = new LoggingRuntime(root, "test"))
        {
            var a = first.Factory.CreateLogger("First");
            var b = second.Factory.CreateLogger("Second");
            await Task.WhenAll(Task.Run(() => { for (var n = 0; n < 100; n++) if (a.IsEnabled(LogLevel.Information)) a.LogInformation("Writer {Writer} sequence {Sequence}", "A", n); }),
                Task.Run(() => { for (var n = 0; n < 100; n++) if (b.IsEnabled(LogLevel.Information)) b.LogInformation("Writer {Writer} sequence {Sequence}", "B", n); }));
        }
        Assert.Equal(200, Events().Length);
    }

    /// <summary>关闭排空取消业务及异步资源释放, 各操作记录唯一终态, 不假定独立日志的全局结束顺序.</summary>
    [Fact]
    public async Task ShutdownWaitsForCancelledBusinessAndRecordsItsFinalState()
    {
        ConcurrentQueue<JsonElement> replies = new();
        var spy = new BridgeTestReader { Wait = true };
        using (var runtime = new LoggingRuntime(root, "test"))
        {
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
                text => replies.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", logger: runtime.Factory.CreateLogger<BridgeSession>(),
                powerBi: new(spy, TimeProvider.System, runtime.Factory.CreateLogger<PowerBiService>()));
            var request = session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "closing", method = "powerbi.instances", @params = new { } }));
            await spy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await request;
            Assert.Equal(0, session.PendingCount);
            Assert.Empty(replies);
            Assert.True(spy.Cancelled);
        }
        var events = Events();
        var business = events.Where(item => item.TryGetProperty("Operation", out var operation) && operation.GetString() == "Bridge.powerbi.instances").ToArray();
        Assert.Equal(2, business.Length);
        Assert.Equal("Started", business[0].GetProperty("Outcome").GetString());
        Assert.Equal("Cancelled", business[1].GetProperty("Outcome").GetString());
        Assert.Equal(business[0].GetProperty("OperationId").GetString(), business[1].GetProperty("OperationId").GetString());
        var release = Assert.Single(events, item => item.TryGetProperty("Operation", out var operation) && operation.GetString() == "Bridge.Release"
            && item.GetProperty("Outcome").GetString() != "Started");
        Assert.Equal("Completed", release.GetProperty("Outcome").GetString());
    }

    [Fact]
    public void RollingRetentionRemovesOldFilesAndKeepsCurrentEvents()
    {
        var folder = Path.Combine(root, "logs");
        Directory.CreateDirectory(folder);
        for (var n = 1; n <= 35; n++)
        {
            var date = DateTime.UtcNow.Date.AddDays(-20 - n);
            var file = Path.Combine(folder, "test-" + date.ToString("yyyyMMdd") + ".clef");
            File.WriteAllText(file, "old");
            File.SetLastWriteTimeUtc(file, date);
        }
        using (var runtime = new LoggingRuntime(root, "test")) runtime.Factory.CreateLogger("Retention").LogInformation("Current event retained");
        Assert.Single(Directory.EnumerateFiles(folder, "*.clef"));
        Assert.Single(Events());
    }

    public void Dispose()
    {
        UserDataStore.Check(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    /// <summary>校验必须在工具执行前完成, 此读取器不允许触及工程.</summary>
    private sealed class UnusedProjectReader : IProjectReader
    {
        /// <summary>若未知批准字段未被拒绝, 则立即暴露越过校验边界的行为.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken token)
            => throw new InvalidOperationException("工具校验失败不能读取工程");
    }
}
