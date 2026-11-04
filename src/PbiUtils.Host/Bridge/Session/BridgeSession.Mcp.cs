// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Mcp.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将已认证本机 MCP 请求送入同一宿主生命周期, 模型写入免二次弹窗, 保留精确授权与独立执行回执.

using PbiUtils.Infrastructure.Input;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>只供已核验本机 IPC 宿主调用; 第三方不能代理弹窗回复、原始取消、用户数据重置或凭据输入.</summary>
    /// <param name="method">固定 MCP 业务目录中的方法.</param>
    /// <param name="parameters">完整业务参数, 与网页沿用同一字段和消息预算.</param>
    /// <param name="token">连接关闭或客户端取消, 真正提交开始后等待业务与回执排空.</param>
    /// <param name="expectedConnectionRevision">调用方已观察的连接代际; 当前目标操作缺失或过期时拒绝.</param>
    /// <param name="observeConnectionRevision">成功读取、选择或操作当前目标后接收本次请求所绑定的代际.</param>
    /// <returns>与应用原业务一致的真实结果; 不向网页发送此 IPC 请求的结果信封.</returns>
    /// <exception cref="AppException">方法、参数、授权、资源或业务失败时保留稳定错误码.</exception>
    public async Task<object?> InvokeMcpAsync(string method, JsonElement parameters, CancellationToken token,
        long? expectedConnectionRevision = null, Action<long>? observeConnectionRevision = null)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(method) || !McpDesktopCatalog.TryGet(method, out var operation))
            throw new AppException("MCP_METHOD_NOT_ALLOWED", "此方法不属于可调用的本机业务能力", 3);
        if (parameters.ValueKind != JsonValueKind.Object) throw new AppException("INVALID_REQUEST", "MCP 参数必须为对象");
        try { JsonInputValidation.RejectDuplicates(parameters); }
        catch (JsonException) { throw new AppException("INVALID_REQUEST", "MCP 参数字段不可重复"); }
        // 严格拒绝秘密/表单字段, 地址在建立请求及可靠回执前验证, 不让无效输入进入持久化.
        if (method == "project.connectRemote") using (ReadMcpRemoteAddress(parameters)) { }
        var id = Guid.NewGuid().ToString("N");
        var envelope = new Dictionary<string, object?> { ["protocolVersion"] = 1, ["id"] = id, ["method"] = method, ["params"] = parameters };
        if (expectedConnectionRevision is { } expected) envelope["connectionRevision"] = expected;
        var request = JsonSerializer.Serialize(envelope, json);
        var (result, responseError, exitCode, observedRevision) = await ReceiveCoreAsync(origin.Origin, request, operation, token);
        if (responseError is null)
        {
            var succeeded = result is McpDesktopResult wrapped ? !wrapped.IsError
                : !BusinessOutcome.From(result is ModelConfigurationContext context ? context.Project : result).IsError;
            if (succeeded && observedRevision is { } observed) observeConnectionRevision?.Invoke(observed);
            return result;
        }
        var error = JsonSerializer.SerializeToElement(responseError, json);
        throw new AppException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!, exitCode);
    }

    /// <summary>在正式业务之前冻结宿主绑定并准备精确参数回执, 不让模型自报批准字段授予权限.</summary>
    /// <param name="operation">内部白名单的真实权限要求.</param>
    /// <param name="parameters">本次请求文档仍存活的精确业务参数.</param>
    /// <param name="id">宿主新建的唯一执行身份.</param>
    /// <param name="flight">同一请求的计时、取消、提交及回执所有权.</param>
    /// <returns>共享业务结果, 先写完回执再返回.</returns>
    private async Task<object?> DispatchMcpAsync(McpDesktopOperation operation, JsonElement parameters, string id, Flight flight)
    {
        if (operation.ReadOnly) return await DispatchAsync(operation.Method, parameters, id, flight);
        return await DispatchWriteAsync(operation.Method, parameters, id, flight);
    }

    /// <summary>对当前宿主模型暂存真实变更并绑定 before/after, 自动签发精确一次授权并复核完整基线后提交.</summary>
    /// <param name="parameters">同一 model.edit 六字段契约, 不接受路径、批准或任意代码执行字段.</param>
    /// <param name="id">一次运行与授权身份.</param>
    /// <param name="flight">提交回执、取消和排空仍由原请求拥有.</param>
    /// <returns>独立权威回读的模型与各保存阶段事实.</returns>
    private async Task<object> EditMcpModelAsync(JsonElement parameters, string id, Flight flight)
    {
        if (edits is null) throw new AppException("WRITE_AUTHORIZATION_UNAVAILABLE", "当前宿主未提供工具模型授权", 3);
        var fields = Strings(parameters, ["operation", "objectName", "property", "value", "table"], 30000);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            var selectedWorkspace = workspaceId ?? throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
            var result = await edits.EditAuthorizedAndSaveAsync(new(fields["operation"], fields["objectName"], fields["property"], fields["value"], fields["table"]),
                id, selectedWorkspace, toolWrites ?? new ToolWritePolicy((_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                }), flight.Cancellation.Token, () => BeginModelCommit(id, flight));
            configuration?.ObserveSaved(result);
            return result;
        }
        finally { projectGate.Release(); }
    }
}
