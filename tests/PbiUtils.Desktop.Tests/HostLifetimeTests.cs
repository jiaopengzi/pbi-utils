// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\HostLifetimeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用门闩验证宿主初始化、排空和清理失败的真实生命周期对象.

using PbiUtils.Desktop.Shell;
using PbiUtils.Desktop;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>纯生命周期门闩测试, 不声称已执行真实 WinForms/WebView2 交互.</summary>
public sealed class HostLifetimeTests
{
    /// <summary>初始化中关闭与连续关闭不提前释放资源; 排空完成后只释放一次.</summary>
    [Fact]
    public async Task InitializationAndCommittedWorkDrainBeforeRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        var lifetime = new HostLifetime(async () => { entered.SetResult(); await drain.Task; }, () => releases++);
        var first = lifetime.StopAsync();
        Assert.Same(first, lifetime.StopAsync());
        Assert.Equal(HostLifecycle.Stopping, lifetime.State);
        Assert.False(entered.Task.IsCompleted);
        lifetime.InitializationFinished();
        await entered.Task;
        Assert.Equal(HostLifecycle.Draining, lifetime.State);
        Assert.Equal(0, releases);
        drain.SetResult();
        await first;
        await lifetime.StopAsync();
        Assert.Equal(1, releases);
        Assert.Equal(HostLifecycle.Disposed, lifetime.State);
    }

    /// <summary>排空失败不会提前清理, 失败可观察并在用户再次关闭时重试.</summary>
    [Fact]
    public async Task DrainFailureRetainsResourcesAndCanRetry()
    {
        var calls = 0;
        var releases = 0;
        var lifetime = new HostLifetime(() => ++calls == 1 ? Task.FromException(new IOException("synthetic drain failure")) : Task.CompletedTask, () => releases++);
        lifetime.InitializationFinished();
        await Assert.ThrowsAsync<IOException>(lifetime.StopAsync);
        Assert.Equal(HostLifecycle.Stopping, lifetime.State);
        Assert.Equal(0, releases);
        await lifetime.StopAsync();
        Assert.Equal(2, calls);
        Assert.Equal(1, releases);
        Assert.Equal(HostLifecycle.Disposed, lifetime.State);
    }

    /// <summary>资源清理异常被返回给宿主异常边界, 不成为未观察 async void 异常.</summary>
    [Fact]
    public async Task CleanupFailureRemainsObservable()
    {
        var attempts = 0;
        var lifetime = new HostLifetime(() => Task.CompletedTask, () => { if (++attempts == 1) throw new IOException("synthetic cleanup failure"); });
        lifetime.InitializationFinished();
        await Assert.ThrowsAsync<IOException>(lifetime.StopAsync);
        Assert.Equal(HostLifecycle.Stopping, lifetime.State);
        await lifetime.StopAsync();
        Assert.Equal(HostLifecycle.Disposed, lifetime.State);
    }

    /// <summary>初始化错误已被宿主显示后, 此前创建的资源仍沿关闭路径释放一次.</summary>
    [Fact]
    public async Task FailedInitializationStillReleasesAcquiredResources()
    {
        var owned = true;
        var calls = 0;
        var lifetime = new HostLifetime(() => Task.CompletedTask, () => { owned = false; calls++; });
        var closing = lifetime.StopAsync();
        // 对应初始化异常边界的 finally, 不把停止状态恢复为 Running.
        lifetime.InitializationFinished();
        await closing;
        await lifetime.StopAsync();
        Assert.False(owned);
        Assert.Equal(1, calls);
        Assert.Equal(HostLifecycle.Disposed, lifetime.State);
    }
}
