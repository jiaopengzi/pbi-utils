// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\UiMessageDispatcherTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证宿主 UI 消息延后、关闭及来源复查和异步错误边界, 不替代原生交互验收.

using PbiUtils.Desktop.Bridge;
using PbiUtils.Desktop;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>用可控 UI 队列验证消息起点及关闭竞态, 不创建 WebView2 或原生选择窗口.</summary>
public sealed class UiMessageDispatcherTests
{
    /// <summary>原始消息事件返回前不执行处理, 已复制的消息按 UI 投递次序开始.</summary>
    [Fact]
    public void ReceivesOnlyAfterOriginalCallbackReturns()
    {
        Queue<Action> queue = new();
        List<string> received = [];
        var inOriginalCallback = true;
        var dispatcher = new UiMessageDispatcher(queue.Enqueue, () => true, error => throw new InvalidOperationException("Unexpected dispatch failure", error));
        dispatcher.Post(() =>
        {
            Assert.False(inOriginalCallback);
            received.Add("first");
            return Task.CompletedTask;
        });
        dispatcher.Post(() =>
        {
            received.Add("second");
            return Task.CompletedTask;
        });
        Assert.Empty(received);
        inOriginalCallback = false;
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Collection(received, item => Assert.Equal("first", item), item => Assert.Equal("second", item));
    }

    /// <summary>入队后关闭宿主或撤销文档来源, 执行时重查并丢弃尚未开始的消息.</summary>
    [Fact]
    public void RechecksLifetimeAndOriginBeforeStarting()
    {
        Queue<Action> queue = new();
        var closing = false;
        var trustedDocument = true;
        var received = 0;
        var dispatcher = new UiMessageDispatcher(queue.Enqueue, () => !closing && trustedDocument,
            error => throw new InvalidOperationException("Unexpected dispatch failure", error));
        dispatcher.Post(() => { received++; return Task.CompletedTask; });
        closing = true;
        queue.Dequeue()();
        closing = false;
        dispatcher.Post(() => { received++; return Task.CompletedTask; });
        trustedDocument = false;
        queue.Dequeue()();
        trustedDocument = true;
        dispatcher.Post(() => { received++; return Task.CompletedTask; });
        queue.Dequeue()();
        Assert.Equal(1, received);
    }

    /// <summary>业务开始后不由调度器丢弃或重放, 关闭仍由原桥接等待权威终态.</summary>
    [Fact]
    public async Task StartedWorkKeepsItsOriginalCompletionBoundary()
    {
        Queue<Action> queue = new();
        var available = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dispatcher = new UiMessageDispatcher(queue.Enqueue, () => available,
            error => completed.TrySetException(error));
        dispatcher.Post(async () =>
        {
            calls++;
            entered.SetResult();
            await release.Task;
            completed.SetResult();
        });
        queue.Dequeue()();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        available = false;
        Assert.False(completed.Task.IsCompleted);
        release.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls);
    }

    /// <summary>消息处理同步抛错或异步失败均报告一次, 不逃出原始或已投递 UI 回调.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveFailuresReachSafeErrorBoundary(bool asynchronous)
    {
        Queue<Action> queue = new();
        var expected = new IOException("Synthetic receive failure");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        var dispatcher = new UiMessageDispatcher(queue.Enqueue, () => true, error => { failures++; reported.TrySetResult(error); });
        dispatcher.Post(asynchronous ? async () => { await release.Task; throw expected; } : () => throw expected);
        Assert.Null(Record.Exception(() => queue.Dequeue()()));
        if (asynchronous)
        {
            Assert.False(reported.Task.IsCompleted);
            release.SetResult();
        }
        Assert.Same(expected, await reported.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, failures);
    }

    /// <summary>窗口句柄在入队前失效时, 入队异常被报告且不启动业务.</summary>
    [Fact]
    public void QueueFailureDoesNotStartReceiveOrEscape()
    {
        var expected = new InvalidOperationException("Synthetic unavailable UI handle");
        Exception? reported = null;
        var received = false;
        var dispatcher = new UiMessageDispatcher(_ => throw expected, () => true, error => reported = error);
        Assert.Null(Record.Exception(() => dispatcher.Post(() => { received = true; return Task.CompletedTask; })));
        Assert.Same(expected, reported);
        Assert.False(received);
    }
}
