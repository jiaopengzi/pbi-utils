// FilePath    : pbi-utils\src\PbiUtils.Windows\NativeUi\DesktopForegroundEvents.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在宿主消息线程短期观察前台切换, 以进程启动身份识别本轮 Desktop, 不更改外部窗口.

using PbiUtils.Windows.Desktop;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PbiUtils.Windows.NativeUi;

/// <summary>只读前台身份与有限 WinEvent 订阅, 调用方必须在拥有消息循环的同一线程建立和释放.</summary>
public static partial class DesktopForegroundEvents
{
    /// <summary>读取当前前台所属进程, 切换中的空句柄返回 0.</summary>
    public static int ProcessId
    {
        get
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(window, out var processId);
            return processId;
        }
    }

    /// <summary>一次读取前台窗口及其根所有者, 将 Desktop 自有的跨进程 WebView2 窗口归到同一外壳.</summary>
    /// <returns>根所有者进程 PID; 切换中的空窗口或已消失窗口返回 0, 不猜测进程后代或可执行文件.</returns>
    public static int OwnerProcessId
    {
        get
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            var owner = GetAncestor(window, 3); // GA_ROOTOWNER 同时沿父窗口和所有者关系找到真实顶层外壳.
            if (owner == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(owner, out var processId);
            return processId;
        }
    }

    /// <summary>只认可本轮完整进程启动身份, 包括由目标外壳拥有的跨进程前台窗口.</summary>
    /// <param name="target">已经由宿主核验的本轮 Desktop 进程启动身份.</param>
    /// <returns>一次前台根所有者采样仍属于同一 Desktop 进程时为 true.</returns>
    public static bool IsTargetForeground(DesktopProcessTarget target) => IsTargetProcess(target, OwnerProcessId);

    /// <summary>验证调用方已采样的前台根所有者, 不二次读取正在变化的前台窗口.</summary>
    /// <param name="target">已经由宿主核验的本轮 Desktop 进程启动身份.</param>
    /// <param name="processId">同一次前台观察取得的根所有者 PID, 不能来自网页.</param>
    /// <returns>完整目标进程启动身份仍匹配时为 true; PID 复用或目标退出均拒绝.</returns>
    public static bool IsTargetProcess(DesktopProcessTarget target, int processId)
    {
        if (processId != target.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            return process.ProcessName == "PBIDesktop" && process.StartTime.ToUniversalTime() == target.Started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>在当前 UI 消息线程订阅前台事件; 不注入其他进程, 回调异常转交既有安全日志边界.</summary>
    /// <param name="changed">重新读取当前状态的回调, 不信任可能迟到的事件窗口.</param>
    /// <param name="reportFailure">宿主已有的异常报告入口.</param>
    /// <returns>必须由创建线程释放的本轮订阅.</returns>
    /// <exception cref="Win32Exception">Windows 拒绝订阅或释放监听.</exception>
    public static IDisposable Observe(Action changed, Action<Exception> reportFailure) => new Subscription(changed, reportFailure);

    /// <summary>保留托管回调根直到原生监听成功解除, 避免异步 WinEvent 调用已回收委托.</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly int threadId = Environment.CurrentManagedThreadId;
        private readonly GCHandle callbackRoot;
        private IntPtr hook;

        /// <summary>只订阅前台变化事件, 由 Windows 在当前消息线程按序投递.</summary>
        public Subscription(Action changed, Action<Exception> reportFailure)
        {
            WinEventCallback callback = (_, _, _, _, _, _, _) =>
            {
                try { changed(); }
                catch (Exception error) { reportFailure(error); }
            };
            var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
            callbackRoot = GCHandle.Alloc(callback);
            hook = SetWinEventHook(3, 3, IntPtr.Zero, callbackPointer, 0, 0, 0);
            if (hook != IntPtr.Zero) return;
            var error = Marshal.GetLastWin32Error();
            callbackRoot.Free();
            throw new Win32Exception(error);
        }

        /// <summary>同一消息线程解除监听, 失败时保留委托根并报告, 不留下指向已回收委托的回调.</summary>
        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            if (threadId != Environment.CurrentManagedThreadId) throw new InvalidOperationException("前台监听必须由创建线程释放");
            if (!UnhookWinEvent(hook)) throw new Win32Exception(Marshal.GetLastWin32Error());
            hook = IntPtr.Zero;
            callbackRoot.Free();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);

    // 源生成导入仅传递原生函数指针; 对应托管委托由订阅保根至成功解除监听.
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, IntPtr callback, uint processId, uint threadId, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
