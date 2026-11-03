// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopFileSaveRecovery.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以完整文件指纹和稳定宿主身份确认已手动完成的 PBIP 尾部保存.

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>只读确认策略, 不调用保存按钮或重放模型修改; PBIX 不由clean状态推断内容.</summary>
public static class DesktopFileSaveRecovery
{
    /// <summary>由实际Bridge和磁盘适配器提供两次观测, settleInterval允许宿主等待迟到的dirty状态.</summary>
    public static async Task<bool> TryConfirmAsync(DesktopFileTarget target, string? expectedFingerprint,
        Func<CancellationToken, Task<DesktopFileTarget>> inspect, Func<CancellationToken, Task<string>> fingerprint,
        TimeSpan settleInterval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(target.Path);
        if (!Path.IsPathFullyQualified(target.Path) || !(extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pbir", StringComparison.OrdinalIgnoreCase))
            || expectedFingerprint is not { Length: 64 } || expectedFingerprint.Any(character => !char.IsAsciiHexDigit(character))) return false;
        bool SameClean(DesktopFileTarget observed) => observed.ProcessId == target.ProcessId && observed.Started == target.Started
            && observed.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase) && !observed.HasUnsavedChanges;
        if (!SameClean(await inspect(token)) || await fingerprint(token) != expectedFingerprint) return false;
        await Task.Delay(settleInterval, token);
        var confirmed = SameClean(await inspect(token)) && await fingerprint(token) == expectedFingerprint;
        token.ThrowIfCancellationRequested();
        return confirmed;
    }
}
