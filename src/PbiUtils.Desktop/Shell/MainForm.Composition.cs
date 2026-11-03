// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.Composition.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 只连接专用业务后台, 前台不再组合模型、账号、文件或业务存储服务.

using PbiUtils.Desktop.Bridge;
using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Desktop.Shell;

public sealed partial class MainForm
{
    /// <summary>后台按此连接创建独立业务上下文, GUI 只提供固定 UI 回调和结果显示.</summary>
    private async Task ComposeBridgeAsync(OriginPolicy origin, string userData, string version)
    {
        bridge = await GuiBusinessClient.ConnectAsync(origin, userData, version, SendBusinessMessage,
            InvokeGuiCallbackAsync, dialogs, ScheduleUserDataResetAsync, externalToolTarget: externalToolTarget);
    }

    /// <summary>只排空并断开本窗口上下文, 不关闭其他客户端或共享后台.</summary>
    private async Task ShutdownBusinessAsync()
    {
        try { if (bridge is not null) await bridge.DisposeAsync(); }
        finally { await ReleaseFocusAsync(); }
    }
}
