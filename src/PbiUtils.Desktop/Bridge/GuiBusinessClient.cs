// FilePath    : pbi-utils\src\PbiUtils.Desktop\Bridge\GuiBusinessClient.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将可信页面业务全部转发到共享宿主, 本窗口只执行有限原生回调及显示结果.

using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;

namespace PbiUtils.Desktop.Bridge;

/// <summary>一个 GUI 对应一个后台上下文, 不在连接失败时本地执行业务或自动重放请求.</summary>
internal sealed class GuiBusinessClient : IAsyncDisposable
{
    private readonly OriginPolicy origin;
    private readonly Action<string, long> send;
    private readonly Func<GuiHostCallback, CancellationToken, Task<JsonElement>> callback;
    private readonly UiDialogBroker localDialogs;
    private readonly Func<Task> scheduleReset;
    private readonly ConcurrentDictionary<string, long> requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> hostDialogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> callbacks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim navigation = new(1, 1);
    private BusinessHostClient? connection;
    private long epoch;
    private bool resetScheduled;
    private int resetInProgress;
    private IAsyncDisposable? maintenance;
    private bool disposed;

    private GuiBusinessClient(OriginPolicy origin, Action<string, long> send,
        Func<GuiHostCallback, CancellationToken, Task<JsonElement>> callback, UiDialogBroker localDialogs, Func<Task> scheduleReset)
    { this.origin = origin; this.send = send; this.callback = callback; this.localDialogs = localDialogs; this.scheduleReset = scheduleReset; }

    /// <summary>只创建远端业务连接, GUI 不组合模型、账号或存储服务.</summary>
    public static async Task<GuiBusinessClient> ConnectAsync(OriginPolicy origin, string userRoot, string webViewVersion,
        Action<string, long> send, Func<GuiHostCallback, CancellationToken, Task<JsonElement>> callback,
        UiDialogBroker localDialogs, Func<Task> scheduleReset, ExternalToolLaunchTarget? externalToolTarget = null, CancellationToken token = default)
        => await ConnectAsync(origin, send, callback, localDialogs, scheduleReset,
            (requests, events) => BusinessHostClient.ConnectAsync(userRoot, new("gui", true, webViewVersion, externalToolTarget), requests, events, token));

    /// <summary>组合固定 GUI 适配与认证传输, 连接策略不参与业务或回调授权.</summary>
    internal static async Task<GuiBusinessClient> ConnectAsync(OriginPolicy origin, Action<string, long> send,
        Func<GuiHostCallback, CancellationToken, Task<JsonElement>> callback, UiDialogBroker localDialogs, Func<Task> scheduleReset,
        Func<BusinessHostRequestHandler, BusinessHostEventHandler, Task<BusinessHostClient>> connect)
    {
        var client = new GuiBusinessClient(origin, send, callback, localDialogs, scheduleReset);
        client.connection = await connect(client.OnCallbackAsync, client.OnEventAsync);
        return client;
    }

    /// <summary>当前 GUI 文档代际, 原生回调切回 UI 线程时须再次核验.</summary>
    public long DocumentEpoch => Interlocked.Read(ref epoch);

    /// <summary>核验真实来源后转发全部业务, 仅本窗口关窗提示在明确本地身份下回答.</summary>
    public async Task ReceiveAsync(string source, string message, long requestEpoch)
    {
        if (!origin.Allows(source) || disposed || requestEpoch != DocumentEpoch) return;
        string? id = null;
        var reset = false;
        var ownsReset = false;
        var hostResetScheduled = false;
        var entered = false;
        try
        {
            using var parsed = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 24 });
            var envelope = parsed.RootElement;
            JsonInputValidation.RejectDuplicates(envelope);
            if (!envelope.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id = identity.GetString()) || id.Length > 100
                || !envelope.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) throw Invalid();
            if (method.GetString() == "ui.dialogRespond" && TryLocalDialog(envelope, id, requestEpoch)) return;
            if (!requests.TryAdd(id, requestEpoch)) throw new AppException("DUPLICATE_ID", "请求身份正在执行", 3);
            entered = true;
            reset = method.GetString() == "app.resetUserData";
            if (reset)
            {
                if (Interlocked.CompareExchange(ref resetInProgress, 1, 0) != 0) throw new AppException("BUSY", "用户数据重置正在执行", 3);
                ownsReset = true;
                maintenance = await connection!.AcquireMaintenanceAsync();
            }
            // 导航控制先抵达后台, 再开放新文档业务; 旧文档在等待期间不能重新绑定.
            await navigation.WaitAsync();
            try { if (requestEpoch != DocumentEpoch) throw new AppException("STALE_DOCUMENT", "页面已更新", 3); }
            finally { navigation.Release(); }
            var completed = (await connection!.Peer.InvokeAsync("gui.invoke", GuiHostJson.Element(new GuiHostRequest(requestEpoch, envelope.Clone()))))
                .Deserialize<GuiHostCompletion>(GuiHostJson.Options) ?? throw Invalid();
            if (completed.ResetScheduled)
            {
                hostResetScheduled = true;
                if (!ownsReset || maintenance is null || requestEpoch != DocumentEpoch) throw Invalid();
                resetScheduled = true;
                await scheduleReset();
            }
        }
        catch (Exception error)
        {
            if (ownsReset && hostResetScheduled)
            {
                resetScheduled = false;
                try { await connection!.Peer.InvokeAsync("gui.cancelReset", GuiHostJson.Element(new { })); }
                catch (Exception cancellationError) { error = cancellationError; }
            }
            if (id is not null && requestEpoch == DocumentEpoch)
            {
                var failure = error as AppException;
                send(JsonSerializer.Serialize(new { protocolVersion = 1, id, error = new { code = failure?.Code ?? "BUSINESS_HOST_CONNECTION_LOST", message = failure?.Message ?? "业务连接失败, 请核验操作结果后重试" } }, GuiHostJson.Options), requestEpoch);
            }
        }
        finally
        {
            if (entered && id is not null)
            {
                requests.TryRemove(id, out _);
                foreach (var item in callbacks.Where(item => item.Value == id)) callbacks.TryRemove(item.Key, out _);
            }
            if (ownsReset && !resetScheduled)
            {
                if (maintenance is { } lease) { maintenance = null; await lease.DisposeAsync(); }
                Interlocked.Exchange(ref resetInProgress, 0);
            }
        }
    }

    /// <summary>只把准确属于本地 broker 的回复留本地, 其他仅允许已接收的后台弹窗身份.</summary>
    private bool TryLocalDialog(JsonElement envelope, string id, long requestEpoch)
    {
        if (!envelope.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Count() != 3 || !parameters.TryGetProperty("dialogId", out var dialog)
            || dialog.ValueKind != JsonValueKind.String || !parameters.TryGetProperty("action", out var action)
            || action.ValueKind != JsonValueKind.String || !parameters.TryGetProperty("values", out var values)) throw Invalid();
        var dialogId = dialog.GetString()!;
        if (hostDialogs.ContainsKey(dialogId)) return false;
        var accepted = localDialogs.Owns(dialogId) && localDialogs.TryRespond(dialogId, action.GetString()!, values);
        send(JsonSerializer.Serialize(new { protocolVersion = 1, id, result = new { accepted } }, GuiHostJson.Options), requestEpoch);
        return true;
    }

    /// <summary>原生回调必须属于仍在执行的原请求与当前文档, 每个 callbackId 只接受一次.</summary>
    private async Task<JsonElement> OnCallbackAsync(string method, JsonElement parameters, CancellationToken token)
    {
        if (method != "gui.callback") throw new AppException("GUI_CALLBACK_NOT_ALLOWED", "未注册的前台交互", 3);
        var value = parameters.Deserialize<GuiHostCallback>(GuiHostJson.Options) ?? throw Invalid();
        if (disposed || value.DocumentEpoch != DocumentEpoch || !requests.TryGetValue(value.RequestId, out var requestEpoch)
            || requestEpoch != value.DocumentEpoch || value.CallbackId.Length != 64 || !value.CallbackId.All(char.IsAsciiHexDigit)
            || !callbacks.TryAdd(value.CallbackId, value.RequestId)) throw new AppException("STALE_GUI_CALLBACK", "前台交互已失效", 3);
        return await callback(value, token);
    }

    /// <summary>保持现有网页信封并把文档身份交给 UI 队列复核, 不用异步事件决定重置事实.</summary>
    private Task OnEventAsync(string name, JsonElement parameters, CancellationToken token)
    {
        if (name == "bridge.message")
        {
            var value = parameters.Deserialize<GuiHostMessage>(GuiHostJson.Options) ?? throw Invalid();
            if (!disposed && value.DocumentEpoch == DocumentEpoch)
            {
                if (value.Message.TryGetProperty("uiDialog", out var opened)) hostDialogs.TryAdd(opened.GetProperty("id").GetString()!, 0);
                if (value.Message.TryGetProperty("uiDialogClosed", out var closed)) hostDialogs.TryRemove(closed.GetProperty("id").GetString()!, out _);
                send(value.Message.GetRawText(), value.DocumentEpoch);
            }
        }
        else throw new AppException("GUI_EVENT_NOT_ALLOWED", "未注册的前台事件", 3);
        return Task.CompletedTask;
    }

    /// <summary>导航撤销旧文档回调和弹窗, 发送顺序在同一门闩内固定.</summary>
    public async Task InvalidateDocumentAsync()
    {
        var next = Interlocked.Increment(ref epoch);
        localDialogs.CancelActive(); hostDialogs.Clear(); callbacks.Clear();
        await navigation.WaitAsync();
        try { await connection!.Peer.InvokeAsync("gui.invalidate", GuiHostJson.Element(new { documentEpoch = next })); }
        finally { navigation.Release(); }
    }

    /// <summary>冻结后台新业务并等准备阶段排空, 关闭提示期间保留精确票据.</summary>
    public async Task<GuiClosePreparation> PrepareCloseAsync() =>
        (await connection!.Peer.InvokeAsync("gui.prepareClose", GuiHostJson.Element(new { }))).Deserialize<GuiClosePreparation>(GuiHostJson.Options) ?? throw Invalid();

    /// <summary>取消同次关闭并释放尚未执行的维护预约.</summary>
    public async Task AbortCloseAsync(string ticket)
    {
        await connection!.Peer.InvokeAsync("gui.abortClose", GuiHostJson.Element(new { ticket }));
        resetScheduled = false;
        if (maintenance is { } lease) { maintenance = null; await lease.DisposeAsync(); }
        Interlocked.Exchange(ref resetInProgress, 0);
    }

    /// <summary>确认仅关闭本上下文, 后台仍服务其他客户端.</summary>
    public Task ConfirmCloseAsync(string ticket) => connection!.Peer.InvokeAsync("gui.close", GuiHostJson.Element(new { ticket }));

    /// <summary>重置前精确等待原后台进程退出, 不按进程名称结束任何程序.</summary>
    public Task WaitForHostExitAsync(CancellationToken token) => connection!.WaitForHostExitAsync(token);

    /// <summary>维护票据由前台 Program 持有至浏览器/日志释放及实际重置结束.</summary>
    public IAsyncDisposable? TakeMaintenanceLease() { var lease = maintenance; maintenance = null; return lease; }

    /// <summary>断开只排空自身, 未转交的维护票据同时释放.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (connection is not null) await connection.DisposeAsync();
        if (maintenance is { } lease) { maintenance = null; await lease.DisposeAsync(); }
        navigation.Dispose();
    }

    private static AppException Invalid() => new("INVALID_REQUEST", "前台通道字段无效", 2);
}
