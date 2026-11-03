// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.Learning.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立无桥接 WebView2 承载固定学习网站, Vue 拥有布局及操作, 宿主仅适配外站浏览器.

using PbiUtils.Infrastructure.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;

namespace PbiUtils.Desktop.Shell;

public sealed partial class MainForm
{
    private WebView2? learningView;
    private Task? learningInitialization;
    private LearningViewRequest? learningBounds;
    private int learningGeneration;
    private int learningLayoutRevision;

    /// <summary>按可信 Vue 的受限区域显示外站; 网站禁止跨源 iframe, 因而使用无宿主能力的独立顶级文档.</summary>
    /// <param name="bounds">当前 CSS 区域和视口, 已由桥接校验.</param>
    /// <param name="token">请求取消; WebView2 初始化自身不能立即中断, 完成后复核所有权.</param>
    /// <returns>原生浏览器已初始化并按最新区域显示的任务, 不代表外站网络加载成功.</returns>
    private async Task UpdateLearningViewAsync(LearningViewRequest bounds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closing || IsDisposed) return;
        var revision = ++learningLayoutRevision;
        learningBounds = bounds;
        if (!bounds.Visible)
        {
            learningView?.Visible = false;
            return;
        }
        if (learningView is null)
        {
            learningView = new WebView2 { Visible = false, TabStop = true };
            Controls.Add(learningView);
            learningInitialization = InitializeLearningViewAsync(learningView, learningGeneration);
        }
        var browser = learningView;
        var generation = learningGeneration;
        var initialization = learningInitialization!;
        try
        {
            await initialization;
            if (generation != learningGeneration || browser.IsDisposed || closing || IsDisposed) return;
            token.ThrowIfCancellationRequested();
            ApplyLearningBounds();
        }
        catch (Exception) when (generation != learningGeneration || browser.IsDisposed || closing || IsDisposed) { }
        catch
        {
            // 隐藏只更新布局, 不能让同一浏览器的失败初始化继续作为可重用实例.
            // 已成功初始化时仍按最新布局保护, 旧请求取消不能关闭后续请求正在使用的视图.
            if (ReferenceEquals(browser, learningView) && generation == learningGeneration
                && (initialization.IsFaulted || initialization.IsCanceled || revision == learningLayoutRevision))
                CloseLearningView();
            throw;
        }
    }

    /// <summary>仅为外站建立独立配置目录和浏览器, 不注册消息、宿主对象、脚本或应用资源映射.</summary>
    /// <param name="browser">本次创建的唯一浏览器.</param>
    /// <param name="generation">创建时的生命周期代次, 关闭后不能重新显示.</param>
    /// <returns>初始化任务; 引擎初始化异常交给受限桥接报告.</returns>
    private async Task InitializeLearningViewAsync(WebView2 browser, int generation)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(RuntimeEnvironment.UserDataDirectory(), "WebView2Learning"));
        if (generation != learningGeneration || browser.IsDisposed || closing || IsDisposed) return;
        TrackBrowser(environment);
        await browser.EnsureCoreWebView2Async(environment);
        if (generation != learningGeneration || browser.IsDisposed || closing || IsDisposed) return;
        var core = browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += LearningNavigationStarting;
        core.FrameNavigationStarting += LearningNavigationStarting;
        core.NewWindowRequested += LearningNewWindowRequested;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.Navigate(LearningViewRequest.Site);
    }

    /// <summary>只允许固定学习主机的 HTTPS 导航, 不影响应用可信来源及原有桥接来源策略.</summary>
    /// <param name="sender">独立外站浏览器.</param>
    /// <param name="args">引擎提供的真实目标.</param>
    private static void LearningNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        args.Cancel = !LearningViewRequest.AllowsNavigation(args.Uri);

    /// <summary>阻止额外窗口; 用户点击同站链接时在当前无桥接浏览器打开, 不启动任意外部程序.</summary>
    /// <param name="sender">独立外站浏览器.</param>
    /// <param name="args">新窗口目标与真实用户点击状态.</param>
    private void LearningNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.IsUserInitiated && LearningViewRequest.AllowsNavigation(args.Uri)) learningView?.CoreWebView2.Navigate(args.Uri);
    }

    /// <summary>使用宿主真实客户端尺寸换算 CSS 像素和 DPI, 显示始终裁剪在应用窗口内.</summary>
    private void ApplyLearningBounds()
    {
        if (learningView is null || learningBounds is not { Visible: true } bounds) return;
        var scaleX = view.ClientSize.Width / bounds.ViewportWidth;
        var scaleY = view.ClientSize.Height / bounds.ViewportHeight;
        var rectangle = Rectangle.FromLTRB((int)Math.Ceiling(bounds.Left * scaleX), (int)Math.Ceiling(bounds.Top * scaleY),
            (int)Math.Floor((bounds.Left + bounds.Width) * scaleX), (int)Math.Floor((bounds.Top + bounds.Height) * scaleY));
        rectangle.Intersect(view.ClientRectangle);
        learningView.Bounds = rectangle;
        learningView.Visible = rectangle.Width > 0 && rectangle.Height > 0;
        if (learningView.Visible) learningView.BringToFront();
    }

    /// <summary>释放当前路由拥有的外站浏览器; 初始化迟到结果不能恢复旧控件或越过关闭边界.</summary>
    private void CloseLearningView()
    {
        learningGeneration++;
        learningLayoutRevision++;
        learningBounds = null;
        learningInitialization = null;
        var browser = learningView;
        learningView = null;
        if (browser is null) return;
        browser.Visible = false;
        Controls.Remove(browser);
        browser.Dispose();
    }

    /// <summary>系统默认浏览器仅打开编译固定的学习站点, 网页不传地址或程序参数.</summary>
    private static void OpenLearningSite()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = LearningViewRequest.Site,
            UseShellExecute = true
        });
    }
}
