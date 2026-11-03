// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\ResetIntent.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 约束关闭后只消费一次的重置意图.

namespace PbiUtils.Desktop.Shell;

/// <summary>维护单个窗口的重置意图, 取消关闭时清除.</summary>
internal sealed class ResetIntent
{
    private bool scheduled;
    /// <summary>是否有尚未消费的重置意图.</summary>
    public bool IsScheduled => scheduled;
    /// <summary>标记已完成确认的请求.</summary>
    public void Schedule() => scheduled = true;
    /// <summary>关闭取消或失败时撤销请求.</summary>
    public void Cancel() => scheduled = false;
    /// <summary>消费意图, 返回此前是否安排了重置.</summary>
    public bool Consume() { var result = scheduled; scheduled = false; return result; }
}
