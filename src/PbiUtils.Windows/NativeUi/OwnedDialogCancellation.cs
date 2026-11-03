// FilePath    : pbi-utils\src\PbiUtils.Windows\NativeUi\OwnedDialogCancellation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 取消已绑定宿主与精确标题的原生文件夹模态框, 不使用全局输入.

using System.Runtime.InteropServices;

namespace PbiUtils.Windows.NativeUi;

/// <summary>只向指定窗口拥有的同名弹窗发送关闭消息, 处理取消早于弹窗建立的竞争.</summary>
public sealed partial class OwnedDialogCancellation : IDisposable
{
    private readonly Lock sync = new();
    private readonly IntPtr owner;
    private readonly string title;
    private readonly CancellationToken cancellation;
    private readonly Timer timer;
    private bool disposed;

    /// <summary>为本次模态框建立短周期取消观察, 不改变窗口焦点.</summary>
    /// <param name="owner">本宿主的原生窗口句柄.</param>
    /// <param name="title">本次对话框使用的准确标题.</param>
    /// <param name="cancellation">请求超时、用户取消或关闭令牌.</param>
    public OwnedDialogCancellation(IntPtr owner, string title, CancellationToken cancellation)
    {
        this.owner = owner; this.title = title; this.cancellation = cancellation;
        timer = new Timer(CloseIfCancelled, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>仅取消已属于当前宿主且标题一致的弹窗; 弹窗尚未建立时下次继续观察.</summary>
    private unsafe void CloseIfCancelled(object? state)
    {
        lock (sync)
        {
            if (disposed || !cancellation.IsCancellationRequested) return;
            var popup = GetWindow(owner, 6); // GW_ENABLEDPOPUP.
            if (popup == IntPtr.Zero || popup == owner || GetWindow(popup, 4) != owner) return; // GW_OWNER.
            var text = stackalloc char[256];
            var count = GetWindowText(popup, text, 256);
            if (count > 0 && new ReadOnlySpan<char>(text, count).SequenceEqual(title.AsSpan()))
                _ = PostMessage(popup, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE.
        }
    }

    /// <summary>同步停止观察, 返回后旧令牌不能关闭新的同名窗口.</summary>
    public void Dispose()
    {
        lock (sync) { disposed = true; timer.Dispose(); }
    }

    /// <summary>读取绑定窗口的拥有关系或可用弹窗.</summary>
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);
    /// <summary>读取精确弹窗标题, 不读取其他窗口正文.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(IntPtr window, char* text, int capacity);
    /// <summary>向精确已验证的本宿主弹窗发送关闭消息.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
