// FilePath    : pbi-utils\src\PbiUtils.Desktop\Program.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在 STA 线程初始化并运行 WinForms 桌面宿主.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Desktop.Shell;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Infrastructure;

namespace PbiUtils.Desktop;

/// <summary>Windows 桌面唯一启动入口.</summary>
internal static class Program
{
    /// <summary>在 STA 线程运行薄 WinForms 宿主.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ExternalToolLaunchTarget? externalToolTarget = null;
        string? argumentNotice = null;
        try { externalToolTarget = ExternalToolLaunchTarget.Parse(args); }
        catch (PbiUtils.Core.AppException) { argumentNotice = "hostDialog.externalToolArgumentsInvalid"; Environment.ExitCode = 2; }
        ApplicationConfiguration.Initialize();
        try
        {
            string? notice = argumentNotice;
            while (true)
            {
                bool reset;
                bool browserExited;
                Task hostExited;
                IAsyncDisposable? maintenance;
                using (var logging = new LoggingRuntime(RuntimeEnvironment.UserDataDirectory(), "desktop"))
                {
                    logging.EnableDiagnostics();
                    var logger = logging.Factory.CreateLogger("Desktop.Host");
                    using var lifetime = BusinessLog.Begin(logger, "Desktop.Run");
                    using var form = new MainForm(logging, notice, externalToolTarget);
                    // 数据重置后的新窗口不得重放原外部工具启动意图.
                    externalToolTarget = null;
                    try { System.Windows.Forms.Application.Run(form); lifetime.Complete(); }
                    catch (Exception error) { lifetime.Fail(error, fatal: true); throw; }
                    reset = form.ConsumeResetIntent();
                    browserExited = form.ResetBrowsersExited;
                    hostExited = form.ResetHostExited;
                    maintenance = form.TakeResetMaintenance();
                }
                if (!reset)
                {
                    if (maintenance is not null) Task.Run(async () => await maintenance.DisposeAsync()).GetAwaiter().GetResult();
                    break;
                }
                try
                {
                    if (!browserExited || maintenance is null) throw new InvalidOperationException("重置维护资源尚未就绪");
                    // 消息泵和 GUI 日志已经释放, 维护预约跨后台退出和删除全过程, 不结束其他用户进程.
                    Task.Run(async () =>
                    {
                        await hostExited.WaitAsync(TimeSpan.FromSeconds(30));
                        await new UserDataReset().ResetAsync(CancellationToken.None);
                    }).GetAwaiter().GetResult();
                    notice = "hostDialog.resetDataCompleted";
                }
                catch { notice = "hostDialog.resetDataFailed"; }
                finally
                {
                    if (maintenance is not null) Task.Run(async () => await maintenance.DisposeAsync()).GetAwaiter().GetResult();
                }
            }
        }
        catch
        {
            // Vue 无法初始化时不另建业务 UI; 已初始化日志保留安全诊断, 启动调用方获得失败退出码.
            Console.Error.WriteLine("pbi-utils 启动失败, 请检查 WebView2、用户数据目录、日志目录权限和磁盘空间.");
            Environment.ExitCode = 3;
        }
    }
}
