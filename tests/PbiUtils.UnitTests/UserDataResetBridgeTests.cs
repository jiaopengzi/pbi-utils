// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UserDataResetBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证宿主双确认、一次安排、迟到回复、取消与并发冻结, 不执行任何真实用户目录重置.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>测试只调用真实桥接与 UiDialogBroker, 重置回调仅计数, 不构造生产目录清理器.</summary>
public sealed class UserDataResetBridgeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>两次确认使用不同随机身份; 旧回复与并发业务不能越过当前确认, 全部确认后只安排一次关闭.</summary>
    [Fact]
    public async Task RequiresTwoHostConfirmationsAndSchedulesExactlyOnce()
    {
        await using var fixture = new Fixture();
        var resetting = fixture.CallAsync("reset", "app.resetUserData", new { });
        var first = await fixture.NextDialogAsync();
        Assert.Equal("hostDialog.resetDataTitle", first.GetProperty("titleKey").GetString());
        Assert.Equal(0, fixture.Scheduled);
        Assert.False((await fixture.ReplyAsync("wrong", "unknown", "confirm")).GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("BUSY", (await fixture.ResponseAsync("busy", "doctor", new { })).GetProperty("error").GetProperty("code").GetString());
        await fixture.ReplyAsync("confirm-first", first.GetProperty("id").GetString()!, "confirm");
        var second = await fixture.NextDialogAsync();
        Assert.Equal("hostDialog.resetDataFinalTitle", second.GetProperty("titleKey").GetString());
        Assert.NotEqual(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal(0, fixture.Scheduled);
        Assert.False((await fixture.ReplyAsync("replay", first.GetProperty("id").GetString()!, "confirm")).GetProperty("result").GetProperty("accepted").GetBoolean());
        await fixture.ReplyAsync("confirm-final", second.GetProperty("id").GetString()!, "confirm");
        await resetting.WaitAsync(Deadline);
        Assert.True(fixture.Response("reset").GetProperty("result").GetProperty("scheduled").GetBoolean());
        Assert.Equal(1, fixture.Scheduled);
        Assert.Equal(2, fixture.Checks);
        Assert.Equal("BUSY", (await fixture.ResponseAsync("again", "app.resetUserData", new { })).GetProperty("error").GetProperty("code").GetString());
        fixture.Bridge.CancelUserDataReset();
        Assert.True((await fixture.ResponseAsync("restored", "doctor", new { })).TryGetProperty("result", out _));
    }

    /// <summary>任一次取消均不安排重置, 并解除原请求冻结使用户可继续使用应用.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellingEitherConfirmationDoesNotScheduleOrKeepBusinessFrozen(int at)
    {
        await using var fixture = new Fixture();
        var resetting = fixture.CallAsync("reset", "app.resetUserData", new { });
        for (var index = 1; index <= at; index++)
        {
            var dialog = await fixture.NextDialogAsync();
            await fixture.ReplyAsync("reply-" + index, dialog.GetProperty("id").GetString()!, index == at ? "cancel" : "confirm");
        }
        await resetting.WaitAsync(Deadline);
        Assert.False(fixture.Response("reset").GetProperty("result").GetProperty("scheduled").GetBoolean());
        Assert.Equal(0, fixture.Scheduled);
        Assert.True((await fixture.ResponseAsync("available", "doctor", new { })).TryGetProperty("result", out _));
    }

    /// <summary>网页不能提交路径或自报确认, 来源不可信时完全不触发检查和弹窗.</summary>
    [Fact]
    public async Task RejectsCallerPathsAndSpoofedApprovalBeforeAnyHostAction()
    {
        await using var fixture = new Fixture();
        var invalid = await fixture.ResponseAsync("invalid", "app.resetUserData", new { path = "outside", confirmed = true });
        Assert.Equal("VALIDATION_ERROR", invalid.GetProperty("error").GetProperty("code").GetString());
        await fixture.Bridge.ReceiveAsync("https://untrusted.test", Request("spoof", "app.resetUserData", new { }));
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Scheduled);
        Assert.False(fixture.Dialogs.Reader.TryRead(out _));
    }

    /// <summary>已有业务仍在途时拒绝进入重置, 不取消正在运行的请求或抢走提交资源.</summary>
    [Fact]
    public async Task ExistingFlightPreventsResetWithoutCancellingThatOperation()
    {
        var reader = new BridgeTestReader { Wait = true };
        await using var fixture = new Fixture(reader: reader);
        var scanning = fixture.CallAsync("scan", "powerbi.instances", new { });
        await reader.Entered.Task.WaitAsync(Deadline);
        Assert.Equal("BUSY", (await fixture.ResponseAsync("reset", "app.resetUserData", new { })).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.Checks);
        Assert.False(reader.Cancelled);
        reader.Gate.TrySetResult();
        await scanning.WaitAsync(Deadline);
    }

    /// <summary>宿主导航撤销当前确认, 即使网页随后重放最终同意也不得安排重置.</summary>
    [Fact]
    public async Task DocumentInvalidationCancelsConfirmationAndRejectsLateReply()
    {
        await using var fixture = new Fixture();
        var resetting = fixture.CallAsync("reset", "app.resetUserData", new { });
        var dialog = await fixture.NextDialogAsync();
        fixture.Bridge.InvalidateDocument();
        await resetting.WaitAsync(Deadline);
        Assert.Equal("CANCELLED", fixture.Response("reset").GetProperty("error").GetProperty("code").GetString());
        Assert.False((await fixture.ReplyAsync("late", dialog.GetProperty("id").GetString()!, "confirm")).GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(0, fixture.Scheduled);
    }

    /// <summary>第二次确认后仍重新核验固定根的权威事实, 回执出现待核验时不得安排关闭或删除.</summary>
    [Fact]
    public async Task RechecksSafetyAfterFinalConfirmationAndPreservesFailure()
    {
        await using var fixture = new Fixture(failSecondCheck: true);
        var resetting = fixture.CallAsync("reset", "app.resetUserData", new { });
        for (var index = 0; index < 2; index++)
        {
            var dialog = await fixture.NextDialogAsync();
            await fixture.ReplyAsync("confirm-" + index, dialog.GetProperty("id").GetString()!, "confirm");
        }
        await resetting.WaitAsync(Deadline);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", fixture.Response("reset").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.Scheduled);
        Assert.True((await fixture.ResponseAsync("available", "doctor", new { })).TryGetProperty("result", out _));
    }

    /// <summary>构造标准参数信封, 不包含任何真实目录、密钥或用户数据.</summary>
    private static string Request(string id, string method, object parameters) =>
        JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters });

    /// <summary>真实消息与弹窗通道由测试拥有, 确认回调只记录次数, 关闭排空不访问用户存储.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ConcurrentQueue<JsonElement> output = new();
        private readonly UiDialogBroker broker;
        internal Channel<JsonElement> Dialogs { get; } = Channel.CreateUnbounded<JsonElement>();
        internal BridgeSession Bridge { get; }
        internal int Scheduled { get; private set; }
        internal int Checks { get; private set; }

        /// <summary>绑定可控安全预检与可选只读扫描服务, 不注入任何真实清理回调.</summary>
        internal Fixture(bool failSecondCheck = false, BridgeTestReader? reader = null)
        {
            void Send(string message)
            {
                var value = JsonSerializer.Deserialize<JsonElement>(message);
                output.Enqueue(value);
                if (value.TryGetProperty("uiDialog", out var dialog)) Dialogs.Writer.TryWrite(dialog);
            }
            broker = new(Send);
            Bridge = new(new(false, null), new(new RuntimeEnvironment()), Send, "test", dialogs: broker,
                powerBi: reader is null ? null : new(reader, TimeProvider.System),
                checkUserDataReset: _ =>
                {
                    Checks++;
                    if (failSecondCheck && Checks == 2) throw new AppException("EXECUTION_NEEDS_VERIFICATION", "test");
                    return Task.CompletedTask;
                }, scheduleUserDataReset: () => Scheduled++);
        }

        /// <summary>读取下一个宿主签发的对话框身份, 以可控通道等待而非固定 sleep.</summary>
        internal Task<JsonElement> NextDialogAsync() => Dialogs.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        /// <summary>向真实桥接发送当前可信文档消息.</summary>
        internal Task CallAsync(string id, string method, object parameters) => Bridge.ReceiveAsync(OriginPolicy.Production, Request(id, method, parameters));
        /// <summary>只返回指定请求唯一结果, 不混入其他弹窗事件.</summary>
        internal JsonElement Response(string id) => Assert.Single(output, value => value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        /// <summary>等待消息终态后取得指定响应.</summary>
        internal async Task<JsonElement> ResponseAsync(string id, string method, object parameters)
        {
            await CallAsync(id, method, parameters).WaitAsync(Deadline);
            return Response(id);
        }
        /// <summary>弹窗回复只包含宿主身份与白名单操作, 无自造确认标志.</summary>
        internal Task<JsonElement> ReplyAsync(string id, string dialogId, string action) => ResponseAsync(id, "ui.dialogRespond", new { dialogId, action, values = new { } });
        /// <summary>排空请求并释放弹窗通道, 不启动任何窗口或删除操作.</summary>
        public async ValueTask DisposeAsync()
        {
            await Bridge.ShutdownAsync();
            broker.Dispose();
        }
    }
}
