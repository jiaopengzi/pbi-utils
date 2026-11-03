// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Scaffolding\ScaffoldBridge.Calendar.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以独立串行队列处理日历预览、偏好和纯内容导入, 关闭由原 owner 统一排空.

using System.Text.Json;

namespace PbiUtils.Host.Bridge.Scaffolding;

public sealed partial class ScaffoldBridge
{
    /// <summary>纯日历请求可取消排队且不阻塞模型草稿, 存储仍保留自身原子锁, 已接受请求由关闭统一排空.</summary>
    private async Task<object> HandleCalendarAsync(string method, JsonElement parameters, Action? committing, CancellationToken token)
    {
        int epoch;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed || stopping, this);
            epoch = generation;
            if (calendarPending++ == 0) calendarDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await calendarGate.WaitAsync(token);
            try
            {
                EnsureCurrent(epoch);
                return await calendarBridge.HandleAsync(method, parameters, () => EnsureCurrent(epoch), committing, token);
            }
            finally { calendarGate.Release(); }
        }
        finally
        {
            lock (sync) if (--calendarPending == 0) calendarDrained.TrySetResult();
        }
    }
}
