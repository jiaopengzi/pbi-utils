// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\HostLifetime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在初始化、排空与释放之间固定宿主的单次关闭顺序.

namespace PbiUtils.Desktop.Shell;

/// <summary>关闭阶段可观察; 释放失败保留停止状态供明确重试.</summary>
public enum HostLifecycle { Initializing, Running, Stopping, Draining, Disposed }

/// <summary>把异步等待与资源所有权分开, 不阻塞 UI 消息泵或提前断开提交资源.</summary>
public sealed class HostLifetime(Func<Task> drain, Action release)
{
    private readonly Lock sync = new();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? shutdown;
    private HostLifecycle state = HostLifecycle.Initializing;

    /// <summary>当前阶段只在短临界区内读取和修改.</summary>
    public HostLifecycle State { get { lock (sync) return state; } }

    /// <summary>初始化成功或已显示错误后解除关闭等待; 不能把停止阶段退回运行.</summary>
    public void InitializationFinished()
    {
        lock (sync)
        {
            if (state == HostLifecycle.Initializing) state = HostLifecycle.Running;
            initialized.TrySetResult();
        }
    }

    /// <summary>连续关闭共用同一任务, 失败后的明确关闭操作可以重试清理.</summary>
    public Task StopAsync()
    {
        lock (sync)
        {
            if (state == HostLifecycle.Disposed) return Task.CompletedTask;
            if (shutdown is { IsCompleted: false } || shutdown is { IsCompletedSuccessfully: true }) return shutdown;
            state = HostLifecycle.Stopping;
            return shutdown = DrainAsync();
        }
    }

    /// <summary>等待初始化发布完资源后排空业务, 最后才释放拥有的资源.</summary>
    private async Task DrainAsync()
    {
        try
        {
            await initialized.Task;
            lock (sync) state = HostLifecycle.Draining;
            await drain();
            release();
            lock (sync) state = HostLifecycle.Disposed;
        }
        catch
        {
            lock (sync) state = HostLifecycle.Stopping;
            throw;
        }
    }
}
