// FilePath    : pbi-utils\src\PbiUtils.Cli\Mcp\McpStdioLifetime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 记录已接受 MCP 请求直到 SDK 实际发送终态, 后台结束时安全关闭 stdio 服务.

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PbiUtils.Cli.Mcp;

/// <summary>只管理协议输出排空, 不改变业务授权、提交或结果分类.</summary>
internal sealed class McpStdioLifetime
{
    private readonly Lock sync = new();
    private readonly HashSet<RequestId> pending = [];
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;

    /// <summary>在 SDK 收到请求和发送结果的真实边界跟踪, 关闭后不再进入新的业务处理器.</summary>
    internal McpServerFilters Filters => new()
    {
        Message = new()
        {
            IncomingFilters = [next => async (context, token) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest request)
                {
                    lock (sync)
                    {
                        if (stopping) return;
                        pending.Add(request.Id);
                    }
                    try { await next(context, token).ConfigureAwait(false); }
                    catch { Complete(request.Id); throw; }
                }
                else await next(context, token).ConfigureAwait(false);
            }],
            OutgoingFilters = [next => async (context, token) =>
            {
                // next 完成后 SDK 已将结果交给真实传输; 发送失败同样终结等待, 原异常继续传播.
                try { await next(context, token).ConfigureAwait(false); }
                finally
                {
                    if (context.JsonRpcMessage is JsonRpcResponse response) Complete(response.Id);
                    else if (context.JsonRpcMessage is JsonRpcError error) Complete(error.Id);
                }
            }]
        }
    };

    /// <summary>原子关闭新请求入口并等待所有已接受请求的输出终态, 不用延时猜测提交完成.</summary>
    internal Task StopAndDrainAsync()
    {
        lock (sync)
        {
            stopping = true;
            if (pending.Count == 0) drained.TrySetResult();
            return drained.Task;
        }
    }

    /// <summary>真实结果或协议错误发送完成后移除对应请求, 最后一个结果完成排空.</summary>
    private void Complete(RequestId id)
    {
        lock (sync)
        {
            pending.Remove(id);
            if (stopping && pending.Count == 0) drained.TrySetResult();
        }
    }
}
