// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 管理 WebView2 生命周期, 本地资源加载与宿主安全边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Desktop.Bridge;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Desktop.Shell;

/// <summary>只负责浏览器生命周期和安全边界的桌面宿主.</summary>
public sealed partial class MainForm : Form
{
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    // 图标逻辑名固定在入口命名空间, Shell 的物理位置不影响嵌入资源定位.
    private readonly Icon brandIcon = new(typeof(Program), "logo.ico");
    private GuiBusinessClient? bridge;
    private OriginPolicy? policy;
    private readonly LoggingRuntime logging;
    private readonly HostLifetime lifetime;
    private readonly UiMessageDispatcher messages;
    private readonly UiDialogBroker dialogs;
    private bool initializationStarted;
    private bool closing;
    private bool shutdownFinished;
    private bool brandReleased;
    private bool closeRequested;
    private readonly ResetIntent resetIntent = new();
    private readonly string? startupNotice;
    private readonly ExternalToolLaunchTarget? externalToolTarget;
    private readonly List<Task> browserExits = [];
    private bool resetBrowsersExited = true;
    private IAsyncDisposable? resetMaintenance;
    private Task? resetHostExited;

    /// <summary>取得关闭后的单次重置意图.</summary>
    internal bool ConsumeResetIntent() => resetIntent.Consume();
    /// <summary>重置关闭时浏览器是否均已退出.</summary>
    internal bool ResetBrowsersExited => resetBrowsersExited;
    /// <summary>实际数据删除期间仍由 Program 持有维护预约, 阻止客户端自动重连抢占.</summary>
    internal IAsyncDisposable? TakeResetMaintenance() { var value = resetMaintenance; resetMaintenance = null; return value; }
    /// <summary>只等待已核验的本后台退出, 不枚举或结束其他进程.</summary>
    internal Task ResetHostExited => resetHostExited ?? Task.CompletedTask;

    /// <summary>记录已确认意图并在当前桥接回复结束后请求正常关闭.</summary>
    private void ScheduleUserDataReset()
    {
        if (closing || closeRequested || IsDisposed || Disposing) throw new PbiUtils.Core.AppException("BUSY", "窗口正在关闭", 3);
        resetIntent.Schedule();
        try { BeginInvoke(Close); }
        catch { resetIntent.Cancel(); throw; }
    }

    /// <summary>等待 UI 实际接受重置关闭, 排队期间用户关窗造成的拒绝回到原请求, 不逃出消息泵.</summary>
    private Task ScheduleUserDataResetAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Schedule()
        {
            try { ScheduleUserDataReset(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        try { BeginInvoke(Schedule); }
        catch (InvalidOperationException) { completion.TrySetException(new PbiUtils.Core.AppException("GUI_CLOSED", "前台窗口已关闭", 3)); }
        return completion.Task;
    }

    /// <summary>撤销未完成的重置, 已进入关闭的桥接仍遵循其关闭状态.</summary>
    private void CancelUserDataReset()
    {
        resetIntent.Cancel();
    }

    /// <summary>跟踪浏览器退出, 以免删除仍在使用的用户数据.</summary>
    /// <param name="environment">本窗口拥有的浏览器环境.</param>
    private void TrackBrowser(CoreWebView2Environment environment)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        browserExits.Add(exited.Task);
    }

    /// <summary>创建窗口并订阅初始化事件.</summary>
    /// <param name="logging">当前会话拥有的日志设施.</param>
    /// <param name="startupNotice">上一轮重置结果的本地化键, 普通启动为空.</param>
    /// <param name="externalToolTarget">外部工具启动时的本机候选; 只交给已认证后台核验.</param>
    public MainForm(LoggingRuntime logging, string? startupNotice = null, ExternalToolLaunchTarget? externalToolTarget = null)
    {
        this.logging = logging;
        this.startupNotice = startupNotice;
        this.externalToolTarget = externalToolTarget;
        lifetime = new HostLifetime(ShutdownBusinessAsync, ReleaseResources);
        messages = new(action => { BeginInvoke(action); }, CanReceiveMessage, LogReceiveFailure);
        dialogs = new(Send);
        Text = "pbi-utils";
        Icon = brandIcon;
        BackColor = Color.FromArgb(245, 246, 250);
        // 内容区以 800 高度和 1920:1080 桌面比例起步, 最小尺寸补上原生标题栏和边框.
        ClientSize = new Size(800 * 1920 / 1080, 800);
        MinimumSize = SizeFromClientSize(ClientSize);
        Controls.Add(view);
        Shown += Initialize;
    }

    /// <summary>加载用户级 WebView2 与受限本地资源.</summary>
    /// <param name="sender">窗体.</param>
    /// <param name="args">显示事件.</param>
    private async void Initialize(object? sender, EventArgs args)
    {
        initializationStarted = true;
        using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<MainForm>(), "Desktop.Initialize");
        try
        {
#if DEBUG
            const bool debug = true;
#else
            const bool debug = false;
#endif
            policy = new OriginPolicy(debug, Environment.GetEnvironmentVariable("PBI_UTILS_DEV_URL"));
            var userData = UserDataStore.Initialize();
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(RuntimeEnvironment.UserDataDirectory(), "WebView2"));
            if (IsDisposed || closing) return;
            TrackBrowser(environment);
            await view.EnsureCoreWebView2Async(environment);
            if (IsDisposed || closing) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            // 凭据改由 Vue 临时收集后, 禁止浏览器独立保存或自动填充, 仍只使用宿主的显式 DPAPI 通道.
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.FrameNavigationStarting += NavigationStarting;
            core.NewWindowRequested += OpenDocumentation;
            core.DownloadStarting += (_, e) => { e.Cancel = true; };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; };
            await ComposeBridgeAsync(policy, userData, version);
            if (IsDisposed || closing) return;
            core.WebMessageReceived += MessageReceived;
            if (policy.Origin == OriginPolicy.Production)
            {
                var web = Path.Combine(AppContext.BaseDirectory, "web");
                if (!File.Exists(Path.Combine(web, "index.html"))) throw new InvalidOperationException("前端资源缺失, 请重新构建或发布");
                core.SetVirtualHostNameToFolderMapping("app.pbi-utils.local", web, CoreWebView2HostResourceAccessKind.DenyCors);
            }
            if (startupNotice is not null) core.NavigationCompleted += ShowStartupNotice;
            core.Navigate(policy.Origin + "/index.html");
            operation.Complete();
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            if (IsDisposed) return;
            // 初始化失败也释放已创建的业务与浏览器资源; 清理错误不能逃出 async void.
            try
            {
                await ShutdownBusinessAsync();
                ReleaseResources();
            }
            catch (Exception cleanupError)
            {
                using var cleanup = BusinessLog.Begin(logging.Factory.CreateLogger<MainForm>(), "Desktop.InitializeCleanup");
                cleanup.Fail(cleanupError);
            }
            // 前端尚未就绪时无法展示 Vue 错误页, 安全日志与退出码保留失败事实, 不另建 WinForms UI.
            Environment.ExitCode = 3;
            if (!closing) Close();
        }
        finally { lifetime.InitializationFinished(); }
    }

    /// <summary>保持 UI 消息泵直到在途业务终结, 之后宿主才关闭 Serilog 并刷盘.</summary>
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (shutdownFinished) return;
        e.Cancel = true;
        if (closing || closeRequested) return;
        closeRequested = true;
        string? closeTicket = null;
        try
        {
            dialogs.CancelActive();
            var prepared = bridge is null ? null : await bridge.PrepareCloseAsync();
            closeTicket = prepared?.Ticket;
            if (prepared?.HasPendingChanges == true)
            {
                using var confirmationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var answer = await dialogs.ShowAsync(new("confirm", "hostDialog.closeTitle", "hostDialog.closeMessage"), confirmationTimeout.Token);
                if (answer.Action != "confirm")
                {
                    await bridge!.AbortCloseAsync(closeTicket!);
                    CancelUserDataReset();
                    return;
                }
            }
            if (closeTicket is not null) await bridge!.ConfirmCloseAsync(closeTicket);
        }
        catch (Exception error)
        {
            LogReceiveFailure(error);
            if (closeTicket is not null)
            {
                try { await bridge!.AbortCloseAsync(closeTicket); }
                catch (Exception abortError) { LogReceiveFailure(abortError); }
            }
            CancelUserDataReset();
            if (error is not PbiUtils.Core.AppException { Code: "BUSINESS_HOST_CONNECTION_LOST" }) return;
            // 后台失联不能永久困住窗口; 明确确认后只关闭本 GUI, 不猜测后台提交结果.
            try
            {
                using var confirmationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var answer = await dialogs.ShowAsync(new("confirm", "hostDialog.closeTitle", "hostDialog.closeMessage"), confirmationTimeout.Token);
                if (answer.Action != "confirm") return;
            }
            catch (Exception confirmationError) { LogReceiveFailure(confirmationError); return; }
        }
        finally { closeRequested = false; }
        closing = true;
        using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<MainForm>(), "Desktop.Shutdown");
        try
        {
            if (!initializationStarted) lifetime.InitializationFinished();
            if (resetIntent.IsScheduled && bridge is not null)
            {
                resetMaintenance = bridge.TakeMaintenanceLease();
                resetHostExited = bridge.WaitForHostExitAsync(CancellationToken.None);
            }
            await lifetime.StopAsync();
            if (resetIntent.IsScheduled)
            {
                try { await Task.WhenAll(browserExits).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException) { resetBrowsersExited = false; }
            }
            shutdownFinished = true;
            operation.Complete();
            if (!IsDisposed) Close();
        }
        catch (Exception error)
        {
            operation.Fail(error);
            CancelUserDataReset();
            if (resetMaintenance is { } lease)
            {
                resetMaintenance = null;
                await lease.DisposeAsync();
            }
            closing = false;
            if (!IsDisposed)
            {
                using var noticeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try { await dialogs.ShowAsync(new("notice", "hostDialog.noticeTitle", "hostDialog.shutdownFailed"), noticeTimeout.Token); }
                catch (Exception noticeError) { LogReceiveFailure(noticeError); }
            }
        }
    }

    /// <summary>导航完成后通过 Vue 展示上一轮重置结果.</summary>
    /// <param name="sender">发出事件的浏览器.</param>
    /// <param name="args">导航结果, 失败时不发送通知.</param>
    private async void ShowStartupNotice(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender is CoreWebView2 core) core.NavigationCompleted -= ShowStartupNotice;
        if (!args.IsSuccess || startupNotice is null || closing) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await dialogs.ShowAsync(new("notice", "hostDialog.noticeTitle", startupNotice), timeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LogReceiveFailure(error); }
    }

    /// <summary>用户点击固定示例文档才交给系统浏览器, 其他新窗口仍阻断.</summary>
    private void OpenDocumentation(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!e.IsUserInitiated || e.Uri != "https://jiaopengzi.com/p/19051044919050241") return;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = e.Uri, UseShellExecute = true });
        }
        catch (Exception error)
        {
            using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<MainForm>(), "Desktop.OpenDocumentation");
            operation.Fail(error);
        }
    }

    /// <summary>所有导航按真实 URI 审核, 本阶段不提供外部链接.</summary>
    /// <param name="sender">浏览器.</param>
    /// <param name="e">导航目标.</param>
    private async void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = policy?.Allows(e.Uri) != true;
        if (!e.Cancel)
        {
            CloseLearningView(); dialogs.CancelActive();
            try
            {
                var invalidation = bridge?.InvalidateDocumentAsync() ?? Task.CompletedTask;
                await ReleaseFocusAsync();
                await invalidation;
            }
            catch (Exception error) { LogReceiveFailure(error); }
        }
    }

    /// <summary>复制真实消息后投递到 UI 队列, 原生模态窗口不得在 WebView2 回调栈内打开.</summary>
    /// <param name="sender">浏览器.</param>
    /// <param name="e">带真实 source 的消息.</param>
    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closing || IsDisposed || Disposing || view.IsDisposed || !IsHandleCreated) return;
        try
        {
            // 事件参数只在原始回调内读取; 延后执行仍由 Bridge 校验真实来源及请求内容.
            var source = e.Source;
            var message = e.WebMessageAsJson;
            var owner = bridge;
            var documentEpoch = owner?.DocumentEpoch;
            messages.Post(() => ReferenceEquals(owner, bridge) && owner is not null && documentEpoch is { } epoch
                ? owner.ReceiveAsync(source, message, epoch) : Task.CompletedTask);
        }
        catch (Exception error) { LogReceiveFailure(error); }
    }

    /// <summary>队列执行前复查窗口生命周期及当前真实文档来源, 关闭后的消息不再创建业务请求.</summary>
    /// <returns>宿主仍可处理当前可信文档消息时为 true.</returns>
    private bool CanReceiveMessage() => !closing && !IsDisposed && !Disposing && !view.IsDisposed && IsHandleCreated
        && bridge is not null && view.CoreWebView2 is { } core && policy?.Allows(core.Source) == true;

    /// <summary>保留原消息入口的安全异常日志, 不让异步队列异常逃出 UI 回调.</summary>
    /// <param name="error">消息读取、调度或处理阶段的异常.</param>
    private void LogReceiveFailure(Exception error)
    {
        using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<MainForm>(), "Desktop.Receive");
        operation.Fail(error);
    }

    /// <summary>将结果切回 UI 线程并再次检查当前文档来源.</summary>
    /// <param name="message">已序列化 JSON.</param>
    private void Send(string message) => Send(message, bridge?.DocumentEpoch, bridge);

    /// <summary>后台事件保留接收时的文档身份, UI 排队期间导航后不得发送进新页面.</summary>
    private void SendBusinessMessage(string message, long documentEpoch) => Send(message, documentEpoch, bridge);

    /// <summary>实际 UI 投递前再次核验原连接与原页面, 本地提示也不跨导航送达.</summary>
    private void Send(string message, long? documentEpoch, GuiBusinessClient? owner)
    {
        if (IsDisposed || Disposing || view.IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Send(message, documentEpoch, owner)); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
            return;
        }
        if (!ReferenceEquals(owner, bridge) || documentEpoch != bridge?.DocumentEpoch) return;
        if (view.CoreWebView2 is { } core && policy?.Allows(core.Source) == true) core.PostWebMessageAsJson(message);
    }

    /// <summary>只释放本窗口拥有的资源; 正常关闭必须在业务排空后调用.</summary>
    private void ReleaseResources()
    {
        CloseLearningView();
        Shown -= Initialize;
        bridge = null;
        dialogs.Dispose();
        if (!view.IsDisposed)
        {
            if (view.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= MessageReceived;
                core.NavigationStarting -= NavigationStarting;
                core.FrameNavigationStarting -= NavigationStarting;
            }
            view.Dispose();
        }
        if (!brandReleased)
        {
            Icon = null;
            brandIcon.Dispose();
            brandReleased = true;
        }
    }

    /// <summary>取消任务、解除订阅并释放自己的浏览器资源.</summary>
    /// <param name="disposing">是否释放托管资源.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseResources();
        base.Dispose(disposing);
    }
}
