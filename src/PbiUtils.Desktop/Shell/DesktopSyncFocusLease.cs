// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\DesktopSyncFocusLease.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在 Desktop 自动同步期间临时保持宿主可见, 用户切换其他应用或结束后归还窗口状态.

namespace PbiUtils.Desktop.Shell;

/// <summary>当前前台窗口与本轮同步参与者的关系, 不包含外部窗口句柄.</summary>
internal enum ForegroundApplication { None, Host, Target, Other }

/// <summary>宿主拥有的可见性适配, 仅在 UI 线程使用, 目标身份由原生适配核验.</summary>
internal interface IDesktopSyncFocusWindow
{
    bool Available { get; }
    bool TopMost { get; set; }
    ForegroundApplication Foreground { get; }
    /// <summary>监听前台变化, 返回值只解除本轮订阅; 失败保留原生错误.</summary>
    IDisposable ObserveForeground(Action changed);
    /// <summary>仅在宿主仍可显示时请求激活, 不修改其他应用属性.</summary>
    void RestoreForeground();
}

/// <summary>只在本轮同步维持临时置顶, 结束或用户离开后释放, 不修改系统前台锁.</summary>
internal sealed class DesktopSyncFocusLease : IDisposable
{
    private readonly IDesktopSyncFocusWindow window;
    private readonly bool originalTopMost;
    private IDisposable? observer;
    private bool active;

    /// <summary>仅为当前可见且在前台的宿主建立保护, 后台发起的同步不抢夺用户焦点.</summary>
    /// <param name="window">已绑定本轮 Desktop 身份的宿主窗口适配.</param>
    /// <exception cref="Exception">原生监听或窗口状态更新失败时保留原错误, 撤销已建立的监听.</exception>
    public DesktopSyncFocusLease(IDesktopSyncFocusWindow window)
    {
        this.window = window;
        originalTopMost = window.TopMost;
        if (!window.Available || window.Foreground != ForegroundApplication.Host) return;
        try
        {
            observer = window.ObserveForeground(ForegroundChanged);
            window.TopMost = true;
            active = true;
        }
        catch
        {
            try { observer?.Dispose(); }
            finally { window.TopMost = originalTopMost; }
            throw;
        }
    }

    /// <summary>前台转到非参与应用或宿主已不可显示时立即放弃本轮保护, 迟到回调不再重新开启.</summary>
    private void ForegroundChanged()
    {
        if (active && (!window.Available || window.Foreground == ForegroundApplication.Other)) Release(restoreForeground: false);
    }

    /// <summary>恢复原置顶状态并移除监听; 仅当前台仍是本轮目标时请求将宿主激活.</summary>
    /// <param name="restoreForeground">正常结束时允许恢复本轮目标自动取得的前台.</param>
    private void Release(bool restoreForeground)
    {
        if (!active) return;
        active = false;
        try
        {
            observer?.Dispose();
            if (restoreForeground && window.Available && window.Foreground == ForegroundApplication.Target) window.RestoreForeground();
        }
        finally
        {
            observer = null;
            window.TopMost = originalTopMost;
        }
    }

    /// <summary>成功、失败和取消共用同一释放路径; 重复释放和迟到事件均无副作用.</summary>
    public void Dispose() => Release(restoreForeground: true);
}
