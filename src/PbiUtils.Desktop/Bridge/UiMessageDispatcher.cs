// FilePath    : pbi-utils\src\PbiUtils.Desktop\Bridge\UiMessageDispatcher.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在原始 WebView2 回调返回后处理 UI 消息, 统一关闭复查和异步异常边界.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PbiUtils.Desktop.Tests")]

namespace PbiUtils.Desktop.Bridge;

/// <summary>保持宿主 UI 线程, 只延后消息起点, 已开始的业务仍由原桥接取消和排空.</summary>
/// <param name="post">将回调异步投递到宿主 UI 消息队列, 不得内联执行.</param>
/// <param name="canReceive">执行前重新检查宿主生命周期及当前文档来源.</param>
/// <param name="reportFailure">宿主既有的安全异常日志边界.</param>
internal sealed class UiMessageDispatcher(Action<Action> post, Func<bool> canReceive, Action<Exception> reportFailure)
{
    /// <summary>只把处理工作加入 UI 队列, 不在 WebView2 原始事件内启动业务.</summary>
    /// <param name="receive">已复制消息的处理操作, 可在 UI 线程打开原生窗口.</param>
    public void Post(Func<Task> receive)
    {
        try { post(() => { _ = ReceiveAsync(receive); }); }
        catch (Exception error) { reportFailure(error); }
    }

    /// <summary>当前消息轮次重新验证宿主状态, 同步及异步失败均回到既有日志边界.</summary>
    /// <param name="receive">尚未开始的消息处理操作.</param>
    /// <returns>本次消息处理或安全丢弃完成的任务.</returns>
    private async Task ReceiveAsync(Func<Task> receive)
    {
        try
        {
            if (canReceive()) await receive();
        }
        catch (Exception error) { reportFailure(error); }
    }
}
