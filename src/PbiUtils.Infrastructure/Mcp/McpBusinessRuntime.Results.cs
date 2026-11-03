// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Results.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将真实业务部分结果及独立回执转为 MCP 终态, 不丢失已创建或已保存事实.

using PbiUtils.Application.Logging;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    /// <summary>完成回执后保留原结果, 已知失败和未知结果均设置 MCP isError.</summary>
    /// <param name="result">实际业务结果或带宿主分类的结果, 不接受工具输入冒充.</param>
    /// <param name="execution">纯文件调用的独占回执, Desktop 已由宿主持久化.</param>
    /// <param name="operation">当前日志范围, 部分失败不能记录为成功.</param>
    /// <returns>成功保持原业务对象; 失败统一包含 result、稳定 error.code 和 needsVerification.</returns>
    internal static CallToolResult CompleteResult(object? result, ExecutionJournal.Execution? execution, BusinessOperation? operation = null)
    {
        var (value, outcome) = result switch
        {
            McpToolValue local => (local.Value, local.Outcome),
            McpDesktopResult desktop => (desktop.Value, new BusinessOutcome(desktop.IsError ? desktop.ErrorCode ?? "EXECUTION_FAILED" : null, desktop.NeedsVerification)),
            _ => (result, BusinessOutcome.From(result))
        };
        try { outcome.Finish(execution); }
        catch (AppException error)
        {
            operation?.Fail(error);
            return Result(new { result = value, error = new { code = error.Code, message = error.Message, causeCode = outcome.ErrorCode }, needsVerification = true }, true);
        }
        if (outcome.IsError) operation?.Reject(outcome.ErrorCode!);
        else operation?.Complete();
        return outcome.IsError
            ? Result(new { result = value, error = new { code = outcome.ErrorCode }, needsVerification = outcome.NeedsVerification }, true)
            : Result(value, false);
    }

    /// <summary>精简业务展示时携带原始完整快照的终态, 不通过 JSON 字段推测部分保存.</summary>
    private sealed record McpToolValue(object Value, BusinessOutcome Outcome);
}
