// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopSaveControlLookup.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 有界读取Desktop保存控件, 不执行控件动作也不重放未知保存调用.

using System.Windows.Automation;
using PbiUtils.Core;

namespace PbiUtils.Windows.Desktop.Files;

public static class DesktopSaveControlLookup
{
    public static T[] Available<T>(IEnumerable<T> candidates, Func<T, bool> matches)
    {
        var available = new List<T>();
        foreach (var candidate in candidates)
        {
            try { if (matches(candidate)) available.Add(candidate); }
            catch (ElementNotAvailableException) { }
        }
        return [.. available];
    }

    public static async Task<T> FindAsync<T>(Func<CancellationToken, Task> verifyIdentity, Func<CancellationToken, Task<T>> read,
        TimeSpan retryDelay, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested(); await verifyIdentity(token);
            try { return await read(token); }
            catch (ElementNotAvailableException) { }
            catch (AppException error) when (error.Code == "DESKTOP_SAVE_UNAVAILABLE") { }
            if (attempt < 2) await Task.Delay(retryDelay, token);
        }
        throw new AppException("DESKTOP_SAVE_UNAVAILABLE", "未找到唯一可用的 Desktop 保存按钮, 请关闭弹窗并展开主窗口后重试", 3);
    }

    /// <summary>一次Invoke结果未知时只读观察, 不能再次调用该动作.</summary>
    public static async Task<bool> InvokeOnceAsync(Func<Task> invoke, Func<CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        try { await invoke(); }
        catch
        {
            if (await confirm(token)) return true;
            throw;
        }
        return await confirm(token);
    }
}
