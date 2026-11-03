// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\GuiDocumentContext.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定单 GUI 文档代际与一次关窗票据, 控制回复不受业务冻结影响.

using System.Security.Cryptography;
using PbiUtils.Core;

namespace PbiUtils.Host.Bridge.Session;

/// <summary>仅属于一个已认证 GUI 连接; 不持有模型或跨客户端状态.</summary>
public sealed class GuiDocumentContext
{
    private readonly Lock sync = new();
    private long epoch;
    private string? closeTicket;
    private bool closed;

    /// <summary>当前文档代际, 后台回调必须绑定发起请求时冻结的值.</summary>
    public long Epoch { get { lock (sync) return epoch; } }

    /// <summary>核验文档和关闭状态, 只允许原弹窗回复与取消穿过关闭准备.</summary>
    public void RequireRequest(long documentEpoch, string method)
    {
        lock (sync)
        {
            if (closed) throw new AppException("HOST_CONTEXT_CLOSED", "当前业务会话已关闭", 3);
            if (documentEpoch != epoch) throw new AppException("STALE_DOCUMENT", "页面已更新, 请重新发起操作", 3);
            if (closeTicket is not null && method is not ("ui.dialogRespond" or "request.cancel"))
                throw new AppException("BUSY", "窗口正在确认关闭, 请完成当前提示", 3);
        }
    }

    /// <summary>只接受严格递增文档代际; 回退或关闭后导航明确拒绝.</summary>
    public void Navigate(long documentEpoch)
    {
        lock (sync)
        {
            if (closed) throw new AppException("HOST_CONTEXT_CLOSED", "当前业务会话已关闭", 3);
            if (documentEpoch <= epoch || documentEpoch > 9007199254740991)
                throw new AppException("STALE_DOCUMENT", "页面代际已失效", 3);
            epoch = documentEpoch;
        }
    }

    /// <summary>冻结新业务并签发不可预测票据; 原有关闭准备不能被替换.</summary>
    public string PrepareClose()
    {
        lock (sync)
        {
            if (closed) throw new AppException("HOST_CONTEXT_CLOSED", "当前业务会话已关闭", 3);
            return closeTicket ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }
    }

    /// <summary>仅同次未确认关闭可恢复业务, 迟到或伪造票据返回 false.</summary>
    public bool AbortClose(string ticket)
    {
        lock (sync)
        {
            if (closed || closeTicket != ticket) return false;
            closeTicket = null;
            return true;
        }
    }

    /// <summary>精确票据永久停止本上下文接收新业务, 后续由拥有者排空资源.</summary>
    public void ConfirmClose(string ticket)
    {
        lock (sync)
        {
            if (closed || closeTicket is null || closeTicket != ticket)
                throw new AppException("INVALID_CLOSE_TICKET", "关闭请求已失效", 3);
            closed = true;
        }
    }
}
