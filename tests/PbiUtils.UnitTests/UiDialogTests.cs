// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UiDialogTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证宿主前端弹窗的一次性身份, 严格字段边界, 取消排空及桥接日志保密.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
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

/// <summary>执行真实 broker 和 bridge, 不把协议测试视为 Windows 窗口验收.</summary>
public sealed class UiDialogTests
{
    /// <summary>只有宿主产生的当前身份可以回答, 回复不回传密钥且不能重复执行.</summary>
    [Fact]
    public async Task RepliesAreBoundToOneUnpredictableDialogAndDoNotEchoSecrets()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        var pending = dialogs.ShowAsync(new("confirm", "input.title", Actions: ["save"], Fields: ["secret"]), default);
        var id = CurrentId(output);
        Assert.Matches("^[0-9a-f]{64}$", id);
        Assert.False(dialogs.TryRespond(new string('0', 64), "save", Json("{\"secret\":\"private-value\"}")));
        Assert.False(pending.IsCompleted);
        Assert.True(dialogs.TryRespond(id, "save", Json("{\"secret\":\"private-value\"}")));
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("private-value", reply.Values["secret"]);
        Assert.DoesNotContain("private-value", reply.ToString());
        Assert.False(dialogs.TryRespond(id, "save", Json("{}")));
        Assert.Single(output, value => value.TryGetProperty("uiDialogClosed", out _));
        Assert.DoesNotContain("private-value", string.Join('\n', output));
        var next = dialogs.ShowAsync(new("confirm", "confirm.title"), default);
        var nextId = CurrentId(output);
        Assert.NotEqual(id, nextId);
        Assert.False(dialogs.TryRespond(id, "confirm", Json("{}")));
        Assert.True(dialogs.TryRespond(nextId, "cancel", Json("{}")));
        Assert.Equal("cancel", (await next).Action);
    }

    /// <summary>未知操作和字段、重复键、大小写别名及错误值类型都不会终结原弹窗.</summary>
    [Theory]
    [InlineData("unknown", "{}")]
    [InlineData("confirm", "{\"secret\":\"one\",\"secret\":\"two\"}")]
    [InlineData("confirm", "{\"Secret\":\"value\"}")]
    [InlineData("confirm", "{\"path\":\"unexpected\"}")]
    [InlineData("confirm", "{\"secret\":true}")]
    [InlineData("confirm", "{\"secret\":null}")]
    [InlineData("confirm", "[]")]
    [InlineData("cancel", "{\"secret\":\"unused\"}")]
    public async Task InvalidRepliesKeepTheOriginalPrompt(string action, string values)
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        var pending = dialogs.ShowAsync(new("confirm", "title", Fields: ["secret"]), default);
        var error = Assert.Throws<AppException>(() => dialogs.TryRespond(CurrentId(output), action, Json(values)));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.False(pending.IsCompleted);
        Assert.True(dialogs.TryRespond(CurrentId(output), "cancel", Json("{}")));
        Assert.Equal("cancel", (await pending).Action);
    }

    /// <summary>允许值长度正好达到 8192 字符, 超长值保留原提示供用户修正.</summary>
    [Fact]
    public async Task StringLengthBoundaryIsEnforcedWithoutTruncation()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        var pending = dialogs.ShowAsync(new("confirm", "title", Fields: ["secret"]), default);
        var oversized = JsonSerializer.SerializeToElement(new { secret = new string('x', 8193) });
        Assert.Throws<AppException>(() => dialogs.TryRespond(CurrentId(output), "confirm", oversized));
        Assert.True(dialogs.TryRespond(CurrentId(output), "confirm", JsonSerializer.SerializeToElement(new { secret = new string('x', 8192) })));
        Assert.Equal(8192, (await pending).Values["secret"].Length);
    }

    /// <summary>取消后可立即打开新弹窗, 旧令牌、旧回答和旧异步完成都不能关闭新弹窗.</summary>
    [Fact]
    public async Task CancellationClearsTheSlotBeforeCompletingTheOldWaiter()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        using var firstToken = new CancellationTokenSource();
        var first = dialogs.ShowAsync(new("confirm", "first"), firstToken.Token);
        var firstId = CurrentId(output);
        var busy = await Assert.ThrowsAsync<AppException>(() => dialogs.ShowAsync(new("confirm", "busy"), default));
        Assert.Equal("BUSY", busy.Code);
        Assert.True(dialogs.CancelActive());
        var second = dialogs.ShowAsync(new("confirm", "second"), default);
        var secondId = CurrentId(output);
        firstToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        Assert.False(dialogs.TryRespond(firstId, "confirm", Json("{}")));
        Assert.True(dialogs.TryRespond(secondId, "confirm", Json("{}")));
        Assert.Equal("confirm", (await second).Action);
        Assert.False(dialogs.CancelActive());
    }

    /// <summary>原请求超时和宿主释放均排空等待, 没有重复关闭事件或可重放回答.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisposeCloseOnlyTheirPrompt(bool dispose)
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        using var cancellation = new CancellationTokenSource();
        var pending = dialogs.ShowAsync(new("confirm", "title"), cancellation.Token);
        var id = CurrentId(output);
        if (dispose) dialogs.Dispose(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(dialogs.TryRespond(id, "confirm", Json("{}")));
        Assert.False(dialogs.CancelActive());
        Assert.Single(output, value => value.TryGetProperty("uiDialogClosed", out _));
        if (dispose) await Assert.ThrowsAsync<ObjectDisposedException>(() => dialogs.ShowAsync(new("confirm", "title"), default));
    }

    /// <summary>原请求正占有业务门闩等待弹窗时, 独立回复仍能完成且输入不会进入日志或响应.</summary>
    [Fact]
    public async Task IndependentBridgeReplyCompletesWaitingOperationWithoutLoggingInputs()
    {
        ConcurrentQueue<JsonElement> output = new();
        var log = new CapturingLogger();
        var clock = new RecordingClock();
        using var dialogs = Broker(output);
        string? actualSecret = null;
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(Json(value)), "test", logger: log, dialogs: dialogs, requestTimeProvider: clock,
            projects: new(new NoRead()), connections: new TestLocalFileConnection(new NoRead()).Service(),
            selectProject: async token =>
            {
                var reply = await dialogs.ShowAsync(new("confirm", "title", Actions: ["save"], Fields: ["secret"]), token);
                actualSecret = reply.Values.GetValueOrDefault("secret");
                return null;
            });
        var operation = Call(session, "operation", "project.open", new { });
        Assert.Equal(TimeSpan.FromSeconds(180), clock.Durations.First());
        Assert.False(operation.IsCompleted);
        var id = CurrentId(output);
        await Call(session, "forged", "ui.dialogRespond", new { dialogId = id, action = "save", values = new { secret = "private-secret" } }, "https://evil.invalid");
        Assert.False(operation.IsCompleted);
        await Call(session, "reply", "ui.dialogRespond", new { dialogId = id, action = "save", values = new { secret = "private-secret" } });
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("private-secret", actualSecret);
        Assert.True(Response(output, "reply").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("CANCELLED", Response(output, "operation").GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("private-secret", string.Join('\n', output));
        Assert.DoesNotContain("private-secret", string.Join('\n', log.Messages));
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>桥接真实计时器取消传给弹窗, 原请求只返回超时且迟到回答不能恢复输入提交.</summary>
    [Fact]
    public async Task BridgeTimeoutClosesPromptAndRejectsLateResponse()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(Json(value)), "test", timeout: TimeSpan.FromMilliseconds(50), dialogs: dialogs,
            projects: new(new NoRead()), connections: new TestLocalFileConnection(new NoRead()).Service(),
            selectProject: async token => { await dialogs.ShowAsync(new("confirm", "title"), token); return null; });
        var operation = Call(session, "operation", "project.open", new { });
        var id = CurrentId(output);
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("TIMEOUT", Response(output, "operation").GetProperty("error").GetProperty("code").GetString());
        Assert.Single(output, value => value.TryGetProperty("uiDialogClosed", out _));
        Assert.False(dialogs.TryRespond(id, "confirm", Json("{}")));
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>会话关闭取消原弹窗, 关闭失败提示仍可回答, 其他业务方法保持关闭.</summary>
    [Fact]
    public async Task ShutdownDrainsPromptButStillAllowsHostCloseNotice()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(Json(value)), "test", dialogs: dialogs,
            projects: new(new NoRead()), connections: new TestLocalFileConnection(new NoRead()).Service(),
            selectProject: async token => { await dialogs.ShowAsync(new("confirm", "title"), token); return null; });
        var operation = Call(session, "operation", "project.open", new { });
        var originalId = CurrentId(output);
        await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await operation;
        Assert.Equal("Disposed", session.Lifecycle);
        Assert.False(dialogs.TryRespond(originalId, "confirm", Json("{}")));
        var notice = dialogs.ShowAsync(new("notice", "closeFailure"), default);
        var noticeId = CurrentId(output);
        await Call(session, "closed-reply", "ui.dialogRespond", new { dialogId = noticeId, action = "confirm", values = new { } });
        Assert.Equal("confirm", (await notice.WaitAsync(TimeSpan.FromSeconds(3))).Action);
        Assert.True(Response(output, "closed-reply").GetProperty("result").GetProperty("accepted").GetBoolean());
        await Call(session, "closed-business", "doctor", new { });
        Assert.DoesNotContain(output, value => value.TryGetProperty("id", out var id) && id.GetString() == "closed-business");
    }

    /// <summary>选择器夹具始终返回空值, 不得读取真实工程.</summary>
    private sealed class NoRead : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => throw new InvalidOperationException("Unexpected model read");
    }

    /// <summary>严格桥接外壳拒绝重复与额外参数, 不消耗有效的一次性回答.</summary>
    [Fact]
    public async Task BridgeRejectsMalformedReplyShellBeforeBroker()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var dialogs = Broker(output);
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(Json(value)), "test", dialogs: dialogs);
        var pending = dialogs.ShowAsync(new("confirm", "title"), default);
        var id = CurrentId(output);
        foreach (var parameters in new[]
        {
            JsonSerializer.Serialize(new { dialogId = id, action = "confirm", values = new { }, extra = true }),
            "{\"dialogId\":\"" + id + "\",\"action\":\"confirm\",\"action\":\"cancel\",\"values\":{}}",
            JsonSerializer.Serialize(new { dialogId = id, action = "confirm", values = "private-invalid-value" })
        })
        {
            await session.ReceiveAsync(OriginPolicy.Production,
                "{\"protocolVersion\":1,\"id\":\"invalid\",\"method\":\"ui.dialogRespond\",\"params\":" + parameters + "}");
            Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
            Assert.False(pending.IsCompleted);
        }
        Assert.True(dialogs.TryRespond(id, "cancel", Json("{}")));
        await pending;
        Assert.DoesNotContain("private-invalid-value", string.Join('\n', output));
    }

    /// <summary>创建仅内存收集消息的通道, 不访问真实凭据或用户文件.</summary>
    private static UiDialogBroker Broker(ConcurrentQueue<JsonElement> output) => new(value => output.Enqueue(Json(value)));

    /// <summary>解析独立 JSON 值, 保留重复属性供实际解析器拒绝.</summary>
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    /// <summary>取得最近宿主弹窗身份, 不从网页推断或自造身份.</summary>
    private static string CurrentId(ConcurrentQueue<JsonElement> output) => output.Last(value => value.TryGetProperty("uiDialog", out _)).GetProperty("uiDialog").GetProperty("id").GetString()!;

    /// <summary>按请求身份读取对应响应, 避免和异步原请求终态混淆.</summary>
    private static JsonElement Response(ConcurrentQueue<JsonElement> output, string requestId) => output.Single(value => value.TryGetProperty("id", out var id) && id.GetString() == requestId);

    /// <summary>经过真实来源校验和 JSON 消息边界调用桥接.</summary>
    private static Task Call(BridgeSession session, string id, string method, object parameters, string source = OriginPolicy.Production) =>
        session.ReceiveAsync(source, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));

    /// <summary>记录宿主实际分配的阶段时长, 仍交给真实系统计时器, 不改写被测分发逻辑.</summary>
    private sealed class RecordingClock : TimeProvider
    {
        public ConcurrentQueue<TimeSpan> Durations { get; } = new();
        /// <summary>捕获阶段时长并保持标准计时器取消和释放语义.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Durations.Enqueue(dueTime);
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>收集实际业务日志的格式化正文和 scope, 以检验敏感输入不进入任何日志参数.</summary>
    private sealed class CapturingLogger : ILogger<BridgeSession>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        /// <summary>日志测试启用全部级别.</summary>
        public bool IsEnabled(LogLevel level) => true;
        /// <summary>保留 scope 的实际格式化内容, 不设置全局日志设施.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Messages.Enqueue(state.ToString() ?? "");
            return null;
        }
        /// <summary>使用生产日志格式化委托收集正文, 失败时也不读取异常原文.</summary>
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
