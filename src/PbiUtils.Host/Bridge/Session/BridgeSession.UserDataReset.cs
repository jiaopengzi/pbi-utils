// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.UserDataReset.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以宿主两次一次性确认签发完整用户数据重置, 不接受网页路径且只安排正常关闭.

using System.Text.Json;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    private bool resettingUserData;
    private bool userDataResetScheduled;

    /// <summary>仅供可信宿主在尚未删除数据的关闭取消/失败路径撤销安排; 已停止会话不会重新接收业务.</summary>
    public void CancelUserDataReset()
    {
        lock (sync)
        {
            userDataResetScheduled = false;
            resettingUserData = false;
        }
    }

    /// <summary>只读预检并经两次宿主确认安排重置; 目录删除由日志和浏览器释放后的宿主执行.</summary>
    /// <param name="parameters">必须为空, 不接受路径、确认标志或自造权限.</param>
    /// <param name="flight">当前独占重置请求, 提交前支持取消, 提交后禁止取消并等待正常关闭.</param>
    /// <returns>两次确认且宿主受理时 scheduled=true, 任一次取消时为 false; 本响应不表示已完成删除.</returns>
    private async Task<object?> ResetUserDataAsync(JsonElement parameters, Flight flight)
    {
        RequireEmpty(parameters);
        if (dialogs is null || checkUserDataReset is null || scheduleUserDataReset is null)
            throw new AppException("UNSUPPORTED", "当前宿主不支持完整重置用户数据", 3);
        RequireSavedModel();
        await checkUserDataReset(flight.Cancellation.Token);
        foreach (var (title, message) in new[]
        {
            ("hostDialog.resetDataTitle", "hostDialog.resetDataMessage"),
            ("hostDialog.resetDataFinalTitle", "hostDialog.resetDataFinalMessage")
        })
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            RequireSavedModel();
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(flight.Cancellation.Token);
            confirmation.CancelAfter(TimeSpan.FromMinutes(3));
            var answer = await dialogs.ShowAsync(new("confirm", title, message), confirmation.Token);
            if (answer.Action != "confirm") return new { scheduled = false, isSimulation = false };
        }
        // 确认期间虽已冻结业务, 排队取消与持久回执仍须在签发前重新核验.
        RequireSavedModel();
        await checkUserDataReset(flight.Cancellation.Token);
        BeginCommit(flight);
        scheduleUserDataReset();
        lock (sync) userDataResetScheduled = true;
        return new { scheduled = true, isSimulation = false };
    }
}
