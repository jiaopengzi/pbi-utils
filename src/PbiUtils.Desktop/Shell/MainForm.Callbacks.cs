// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.Callbacks.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在真实 GUI 线程执行所属后台请求的有限原生交互, 不提供通用程序或文件执行入口.

using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Windows;

namespace PbiUtils.Desktop.Shell;

public sealed partial class MainForm
{
    private readonly Dictionary<string, IAsyncDisposable> focusLeases = new(StringComparer.Ordinal);

    /// <summary>真实 IPC 已核验请求归属, 切回 UI 线程后再次核验窗口和文档, 不在 WebView 回调栈打开系统窗口.</summary>
    private Task<JsonElement> InvokeGuiCallbackAsync(GuiHostCallback request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Invoke()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (IsDisposed || Disposing || bridge is null || request.DocumentEpoch != bridge.DocumentEpoch
                    || closing && request.Kind != "focusEnd")
                    throw new AppException("STALE_GUI_CALLBACK", "前台交互已失效", 3);
                completion.TrySetResult(await DispatchGuiCallbackAsync(request, token));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        try { BeginInvoke(Invoke); }
        catch (InvalidOperationException) { completion.TrySetException(new AppException("GUI_CLOSED", "前台窗口已关闭", 3)); }
        return completion.Task;
    }

    /// <summary>仅固定类型回调能够进入系统适配, 所有输入来自已认证后台并继续严格反序列化.</summary>
    private async Task<JsonElement> DispatchGuiCallbackAsync(GuiHostCallback request, CancellationToken token)
    {
        var input = request.Parameters;
        switch (request.Kind)
        {
            case "selectProject":
                Empty(input);
                return GuiHostJson.Element(new GuiPathSelection(await SelectProjectAsync(token)));
            case "selectSample":
                Empty(input);
                return GuiHostJson.Element(new GuiPathSelection(await ChooseSampleDirectoryAsync(token)));
            case "selectScaffold":
                Empty(input);
                return GuiHostJson.Element(new GuiPathSelection(await ChooseScaffoldDirectoryAsync(token)));
            case "selectExport":
                var export = input.Deserialize<ModelTableExportRequest>(GuiHostJson.Options) ?? throw InvalidCallback();
                return GuiHostJson.Element(new GuiPathSelection(await ChooseTableExportFileAsync(export, token)));
            case "openDirectory":
                var directory = input.Deserialize<DirectoryInput>(GuiHostJson.Options) ?? throw InvalidCallback();
                OpenBoundDirectory(directory.Directory, "DIRECTORY_UNAVAILABLE", "目录不存在或不可用");
                break;
            case "learningUpdate":
                var bounds = input.Deserialize<LearningViewRequest>(GuiHostJson.Options) ?? throw InvalidCallback();
                bounds.Validate();
                await UpdateLearningViewAsync(bounds, token);
                break;
            case "learningClose":
                Empty(input); CloseLearningView(); break;
            case "learningOpen":
                Empty(input); OpenLearningSite(); break;
            case "focusBegin":
                var target = input.Deserialize<FocusInput>(GuiHostJson.Options) ?? throw InvalidCallback();
                if (target.ProcessId <= 0 || target.Started == default) throw InvalidCallback();
                var lease = await new DesktopSyncFocusWindow(this, () => !closing, LogReceiveFailure).BeginAsync(new(target.ProcessId, target.Started));
                var leaseId = Guid.NewGuid().ToString("N");
                focusLeases.Add(leaseId, lease);
                return GuiHostJson.Element(new GuiFocusLease(leaseId));
            case "focusEnd":
                var release = input.Deserialize<GuiFocusLease>(GuiHostJson.Options) ?? throw InvalidCallback();
                if (!focusLeases.Remove(release.LeaseId, out var focus)) throw InvalidCallback();
                await focus.DisposeAsync();
                break;
            default: throw new AppException("GUI_CALLBACK_NOT_ALLOWED", "未注册的原生交互", 3);
        }
        return GuiHostJson.Element(new { completed = true });
    }

    /// <summary>后台断线也解除本 GUI 自有焦点辅助, 不影响模型事务或其他窗口.</summary>
    private async Task ReleaseFocusAsync()
    {
        var leases = focusLeases.Values.ToArray();
        focusLeases.Clear();
        foreach (var lease in leases) await lease.DisposeAsync();
    }

    private sealed record DirectoryInput(string Directory);
    private sealed record FocusInput(int ProcessId, DateTime Started);
    private static AppException InvalidCallback() => new("INVALID_GUI_CALLBACK", "前台交互字段无效", 3);
    private static void Empty(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any()) throw InvalidCallback();
    }
}
