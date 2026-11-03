// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.SyncFocus.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将 Desktop 同步可见性作用域封送到宿主 UI 线程, 保持窗口状态和事件订阅的所有权.

using PbiUtils.Windows.NativeUi;
using PbiUtils.Windows.Desktop;
using PbiUtils.Windows;

namespace PbiUtils.Desktop.Shell;

public sealed partial class MainForm
{
    /// <summary>当前窗口专属的焦点适配, 不更改 Power BI Desktop 或其他应用的窗口属性.</summary>
    /// <param name="host">拥有同步请求和 UI 消息循环的宿主.</param>
    /// <param name="available">宿主仍可显示, 未进入关闭或资源释放时为 true.</param>
    /// <param name="reportFailure">已有的安全日志入口.</param>
    private sealed class DesktopSyncFocusWindow(Form host, Func<bool> available, Action<Exception> reportFailure)
    {
        /// <summary>在 UI 线程建立短期保护, 释放也回到同一线程; 不使用提交前的取消令牌中断保存尾部.</summary>
        /// <param name="target">本轮已核实的 Desktop 身份, 不接受网页窗口参数.</param>
        /// <returns>在同步的 finally 中异步释放的作用域.</returns>
        public async Task<IAsyncDisposable> BeginAsync(DesktopProcessTarget target)
        {
            DesktopSyncFocusLease? lease = null;
            void Begin() => lease = new(new Window(host, available, target, reportFailure));
            try
            {
                if (host.InvokeRequired) await host.InvokeAsync(Begin);
                else Begin();
            }
            catch (Exception error) { reportFailure(error); }
            // 焦点辅助失败必须留日志, 但不能阻止真实同步或改写模型/文件保存事实.
            return new AsyncLease(host, lease, reportFailure);
        }

        /// <summary>把真实宿主和已核实目标转换为可见性协议, 不保留外部 HWND.</summary>
        private sealed class Window(Form host, Func<bool> available, DesktopProcessTarget target, Action<Exception> reportFailure) : IDesktopSyncFocusWindow
        {
            public bool Available => available() && !host.IsDisposed && !host.Disposing && host.IsHandleCreated && host.Visible && host.WindowState != FormWindowState.Minimized;
            public bool TopMost { get => host.TopMost; set { if (!host.IsDisposed && !host.Disposing) host.TopMost = value; } }
            public ForegroundApplication Foreground
            {
                get
                {
                    // Desktop 的 WebView2 子进程可短暂取得前台, 以同一次观察的真实根所有者分类.
                    var processId = DesktopForegroundEvents.OwnerProcessId;
                    if (processId == 0) return ForegroundApplication.None;
                    if (processId == Environment.ProcessId) return ForegroundApplication.Host;
                    return DesktopForegroundEvents.IsTargetProcess(target, processId) ? ForegroundApplication.Target : ForegroundApplication.Other;
                }
            }
            public IDisposable ObserveForeground(Action changed) => DesktopForegroundEvents.Observe(changed, reportFailure);
            public void RestoreForeground() { if (Available) host.Activate(); }
        }

        /// <summary>保存尾部排空后在创建线程解除订阅和临时置顶, 不同步阻塞宿主 UI.</summary>
        private sealed class AsyncLease(Form host, DesktopSyncFocusLease? lease, Action<Exception> reportFailure) : IAsyncDisposable
        {
            /// <summary>由原保存尾部等待释放完成; 宿主生命周期保证 UI 在在途请求排空前仍有效.</summary>
            public async ValueTask DisposeAsync()
            {
                if (lease is null) return;
                try
                {
                    if (host.InvokeRequired) await host.InvokeAsync(lease.Dispose);
                    else lease.Dispose();
                }
                catch (Exception error) { reportFailure(error); }
            }
        }
    }
}
