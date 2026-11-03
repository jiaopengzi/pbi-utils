// FilePath    : pbi-utils\src\PbiUtils.Host\BusinessContext.Gui.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发 GUI 业务与文档/关闭控制, 维持回调和取消的独立控制通路.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host;

internal sealed partial class BusinessContext
{
    /// <summary>真实页面来源已在前台核验; 后台仍核验上下文、文档及全部原业务字段.</summary>
    private async Task<JsonElement> InvokeGuiAsync(string method, JsonElement parameters, CancellationToken token)
    {
        switch (method)
        {
            case "gui.invoke":
                var input = parameters.Deserialize<GuiHostRequest>(GuiHostJson.Options) ?? throw Invalid();
                if (input.Message.ValueKind != JsonValueKind.Object || !input.Message.TryGetProperty("method", out var methodValue)
                    || methodValue.ValueKind != JsonValueKind.String || !input.Message.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw Invalid();
                Task receiving;
                lock (lifecycle)
                {
                    document.RequireRequest(input.DocumentEpoch, methodValue.GetString()!);
                    // ReceiveAsync 在首次异步等待前登记 Flight; 与关闭/导航同锁, 不留准入后尚未被排空看见的空隙.
                    using (gui!.Enter(input.DocumentEpoch, id.GetString()!))
                        receiving = bridge!.ReceiveAsync(OriginPolicy.Production, input.Message.GetRawText(), token);
                }
                await receiving;
                await FlushAsync();
                return GuiHostJson.Element(new GuiHostCompletion(methodValue.GetString() == "app.resetUserData" && resetScheduled));
            case "gui.invalidate":
                var navigation = parameters.Deserialize<DocumentInput>(GuiHostJson.Options) ?? throw Invalid();
                lock (lifecycle)
                {
                    document.Navigate(navigation.DocumentEpoch);
                    dialogs!.CancelActive();
                    bridge!.InvalidateDocument();
                }
                return GuiHostJson.Element(new { completed = true });
            case "gui.prepareClose":
                RequireEmpty(parameters);
                string ticket;
                Task pending;
                lock (lifecycle)
                {
                    ticket = document.PrepareClose();
                    dialogs!.CancelActive();
                    pending = bridge!.CancelPendingAsync();
                }
                await pending;
                return GuiHostJson.Element(new GuiClosePreparation(ticket, bridge.HasPendingChanges));
            case "gui.abortClose":
                var abort = parameters.Deserialize<TicketInput>(GuiHostJson.Options) ?? throw Invalid();
                bool accepted;
                lock (lifecycle)
                {
                    accepted = document.AbortClose(abort.Ticket);
                    if (accepted) { resetScheduled = false; bridge!.CancelUserDataReset(); }
                }
                return GuiHostJson.Element(new { accepted });
            case "gui.cancelReset":
                RequireEmpty(parameters);
                lock (lifecycle) { resetScheduled = false; bridge!.CancelUserDataReset(); }
                return GuiHostJson.Element(new { completed = true });
            case "gui.close":
                var close = parameters.Deserialize<TicketInput>(GuiHostJson.Options) ?? throw Invalid();
                Task shuttingDown;
                lock (lifecycle)
                {
                    document.ConfirmClose(close.Ticket);
                    shuttingDown = bridge!.ShutdownAsync();
                }
                await shuttingDown;
                return GuiHostJson.Element(new { completed = true });
            default: throw new AppException("HOST_METHOD_NOT_ALLOWED", "GUI 后台入口未注册", 3);
        }
    }

    /// <summary>维护预约由已认证 GUI 在传输层取得, 这里再次核验真实未决账本.</summary>
    private Task ValidateResetAsync(CancellationToken token) => new UserDataReset().ValidateAsync(token);
    /// <summary>只记录关闭意图, 原业务结果可靠发送后才通知 GUI 开始正常关闭.</summary>
    private void ScheduleReset() => resetScheduled = true;
    private sealed record DocumentInput(long DocumentEpoch);
    private sealed record TicketInput(string Ticket);
}
