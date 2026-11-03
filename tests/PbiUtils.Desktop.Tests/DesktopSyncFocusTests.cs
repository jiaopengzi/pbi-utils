// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopSyncFocusTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Desktop 同步期间的临时可见性、用户切换应用和作用域释放边界, 不替代真实窗口验收.

using PbiUtils.Desktop.Shell;
using PbiUtils.Desktop;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>用可控窗口状态验证同步可见性, 不创建原生窗口或操作用户 Desktop.</summary>
public sealed class DesktopSyncFocusTests
{
    /// <summary>自动重载抢前台时宿主保持可见, 完成后恢复原置顶状态且清除监听.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetActivationKeepsHostVisibleAndCompletionRestoresState(bool initiallyTopMost)
    {
        var window = new Window { TopMost = initiallyTopMost };
        var lease = new DesktopSyncFocusLease(window);
        Assert.True(window.TopMost);
        window.Change(ForegroundApplication.Target);
        Assert.True(window.TopMost);
        lease.Dispose();
        Assert.Equal(initiallyTopMost, window.TopMost);
        Assert.Equal(1, window.Activations);
        Assert.Equal(0, window.Listeners);
        lease.Dispose();
        Assert.Equal(1, window.Activations);
    }

    /// <summary>用户切换到第三个应用后立即归还置顶状态, 后续事件及完成不再抢回前台.</summary>
    [Fact]
    public void SwitchingToAnotherApplicationRelinquishesProtectionPermanently()
    {
        var window = new Window();
        using var lease = new DesktopSyncFocusLease(window);
        var lateCallback = window.Changed;
        window.Change(ForegroundApplication.Other);
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Listeners);
        window.Change(ForegroundApplication.Target);
        lateCallback?.Invoke();
        lease.Dispose();
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Activations);
    }

    /// <summary>开始前宿主已隐藏、最小化或失去前台时不建立全局置顶行为.</summary>
    [Theory]
    [InlineData(false, "Host")]
    [InlineData(true, "Other")]
    [InlineData(true, "Target")]
    public void InactiveHostDoesNotTakeForeground(bool available, string foreground)
    {
        var window = new Window { Available = available, Foreground = Enum.Parse<ForegroundApplication>(foreground) };
        using var lease = new DesktopSyncFocusLease(window);
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Listeners);
        Assert.Equal(0, window.Activations);
    }

    /// <summary>失败或取消的 finally 同样撤销临时置顶, 窗口失效后迟到事件不能激活宿主.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAndCancellationReleaseTheSameScope(bool canceled)
    {
        var window = new Window();
        Assert.ThrowsAny<Exception>((Action)(() =>
        {
            using var lease = new DesktopSyncFocusLease(window);
            window.Available = false;
            window.Change(ForegroundApplication.Target);
            if (canceled) throw new OperationCanceledException();
            throw new IOException("Synthetic synchronization failure");
        }));
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Listeners);
        Assert.Equal(0, window.Activations);
    }

    /// <summary>前台切换中的空句柄不代表用户离开; 宿主已经在前台时完成不重复激活.</summary>
    [Fact]
    public void TransientEmptyForegroundDoesNotEndProtection()
    {
        var window = new Window();
        using var lease = new DesktopSyncFocusLease(window);
        window.Change(ForegroundApplication.None);
        Assert.True(window.TopMost);
        window.Change(ForegroundApplication.Host);
        lease.Dispose();
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Activations);
    }

    /// <summary>恢复激活本身失败时仍归还原置顶状态和订阅, 保留错误供原保存尾部处理.</summary>
    [Fact]
    public void ForegroundRestoreFailureStillReleasesTemporaryState()
    {
        var window = new Window { FailRestore = true };
        var lease = new DesktopSyncFocusLease(window);
        window.Change(ForegroundApplication.Target);
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.False(window.TopMost);
        Assert.Equal(0, window.Listeners);
        lease.Dispose();
        Assert.Equal(1, window.Activations);
    }

    /// <summary>提供真实可见性作用域使用的窗口协议, 仅替代原生输入与显示效果.</summary>
    private sealed class Window : IDesktopSyncFocusWindow
    {
        public bool Available { get; set; } = true;
        public bool TopMost { get; set; }
        public ForegroundApplication Foreground { get; set; } = ForegroundApplication.Host;
        public Action? Changed { get; private set; }
        public int Activations { get; private set; }
        public bool FailRestore { get; init; }
        public int Listeners => Changed?.GetInvocationList().Length ?? 0;
        public IDisposable ObserveForeground(Action changed) { Changed += changed; return new Subscription(() => Changed -= changed); }
        public void RestoreForeground() { Activations++; if (FailRestore) throw new InvalidOperationException("Synthetic foreground restore failure"); }
        public void Change(ForegroundApplication foreground) { Foreground = foreground; Changed?.Invoke(); }
    }

    /// <summary>仅移除本轮订阅, 用于断言作用域不会遗留监听.</summary>
    private sealed class Subscription(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
