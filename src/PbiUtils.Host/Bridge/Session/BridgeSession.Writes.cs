// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Writes.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将GUI真实副作用纳入共享后台的可靠回执和全客户端写入准入.

using PbiUtils.Host.Bridge.Configuration;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Mcp;

using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>GUI复用中性业务目录的副作用分类, 对话框回答/取消/独占维护不占业务写锁.</summary>
    /// <param name="method">已验证网页白名单方法.</param>
    /// <param name="parameters">本请求精确参数, 回执仅保存摘要.</param>
    /// <param name="id">网页请求关联身份, 不直接作为磁盘文件名.</param>
    /// <param name="flight">唯一取消、提交、回执与排空所有者.</param>
    /// <returns>原业务结果; 活跃或未知写入阻止新副作用.</returns>
    private Task<object?> DispatchGuiAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        // 无journal只用于独立纯业务组合测试; 产品Host composition始终提供同一用户根的journal.
        if (mcpJournal is not null && BusinessOperationCatalog.TryGet(method, out var operation) && operation.RequiresExecutionReceipt)
            return DispatchWriteAsync(method, parameters, id, flight);
        return DispatchAsync(method, parameters, id, flight);
    }

    /// <summary>GUI 与 MCP 共同持有唯一回执租约, 真实业务提交回调仍由当前 flight 拥有.</summary>
    /// <param name="method">入口已核验且需要可靠回执的固定业务方法.</param>
    /// <param name="parameters">本请求的精确参数, 仅摘要进入原账本.</param>
    /// <param name="id">原请求身份, 不作为磁盘文件名.</param>
    /// <param name="flight">原会话拥有取消、提交与排空状态.</param>
    /// <returns>可靠记下终态后的原业务结果; 未决提交和持久化失败保持阻断.</returns>
    private async Task<object?> DispatchWriteAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        var journal = mcpJournal ?? throw new AppException("MCP_WRITE_UNAVAILABLE", "当前宿主未提供可靠工具执行回执", 3);
        using var execution = journal.Prepare(Guid.NewGuid().ToString("N"), method, parameters, workspaceId ?? "application");
        flight.Execution = execution;
        try
        {
            var result = method switch
            {
                "model.edit" when flight.IsMcp => await EditMcpModelAsync(parameters, id, flight),
                "project.connectRemote" when flight.IsMcp => await ConnectMcpRemoteAsync(parameters, flight),
                _ => await DispatchAsync(method, parameters, id, flight)
            };
            if (flight.IsMcp) PublishMcpModelChange(result, flight);
            try { BusinessOutcome.From(result is ModelConfigurationContext context ? context.Project : result).Finish(execution); }
            catch (Exception receiptError) when (receiptError is AppException or IOException or UnauthorizedAccessException)
            {
                // 业务真实结果仍须回传; 原 commit_started 保留阻止重放, 不能让回执故障抹去已创建/已提交事实.
                if (flight.IsMcp) return new McpDesktopResult(result, true, "RECEIPT_SAVE_FAILED", true);
                throw new AppException("RECEIPT_SAVE_FAILED", "业务已执行但结果回执保存失败, 请核验实际模型或文件, 不得重放", 3);
            }
            return result;
        }
        catch (Exception error)
        {
            // 提交后即使客户端已取消, 也排空独立回执; 持久化失败不覆盖原业务首因.
            try { execution.Finish(false, error is AppException app ? app.Code : error is OperationCanceledException ? "CANCELLED" : "EXECUTION_FAILED"); }
            catch (Exception receiptError) when (receiptError is AppException or IOException or UnauthorizedAccessException)
            { error.Data["McpReceiptSaveFailed"] = true; }
            throw;
        }
        finally { flight.Execution = null; }
    }

}
