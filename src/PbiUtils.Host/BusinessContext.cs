// FilePath    : pbi-utils\src\PbiUtils.Host\BusinessContext.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为认证连接分发 GUI/MCP/CLI 的固定入口, 独立拥有业务和提交排空生命周期.

using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Logging;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Commands;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Host;

internal sealed partial class BusinessContext : IBusinessHostContext, IMcpDesktopContext
{
    private readonly BusinessHostRegistration registration;
    private readonly BusinessHostPeer peer;
    private readonly string userRoot;
    private readonly LoggingRuntime logging;
    private readonly HostGuiInteractions? gui;
    private readonly GuiDocumentContext document = new();
    private readonly Lock lifecycle = new();
    private readonly HashSet<TaskCompletionSource> calls = [];
    private readonly Channel<Outbound> outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task delivery;
    private Exception? deliveryFailure;
    private Task? disposal;
    private bool resetScheduled;
    private sealed record Outbound(string? Name, JsonElement Payload = default, TaskCompletionSource? Barrier = null);

    /// <summary>只有完成 OS 身份核验的服务端工厂可以构造, 不在握手中发起 GUI 回调.</summary>
    private BusinessContext(BusinessHostRegistration registration, BusinessHostPeer peer, string userRoot, LoggingRuntime logging)
    {
        this.registration = registration;
        this.peer = peer;
        this.userRoot = userRoot;
        this.logging = logging;
        gui = registration.Options.Kind == "gui" ? new HostGuiInteractions(peer, logging.Factory.CreateLogger<HostGuiInteractions>()) : null;
        delivery = DeliverAsync();
    }

    /// <summary>构造失败仍异步排空已转交的业务资源, 不泄漏半成品上下文.</summary>
    public static async Task<IBusinessHostContext> CreateAsync(BusinessHostRegistration registration, BusinessHostPeer peer, string userRoot, LoggingRuntime logging,
        IBusinessHostMcpConnections? mcpConnections = null)
    {
        var context = new BusinessContext(registration, peer, userRoot, logging);
        try { context.Compose(userRoot, logging, registration.Options.Kind == "gui" ? mcpConnections : null); return context; }
        catch (Exception error)
        {
            try { await context.DisposeAsync(); }
            catch (Exception cleanup)
            {
                error.Data["HostContextCleanupFailed"] = true;
                using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<BusinessContext>(), "Host.ContextConstructionCleanup");
                operation.Fail(cleanup);
            }
            throw;
        }
    }

    /// <summary>MCP 只能观察自己的后台上下文身份, 不枚举或采用其他客户端.</summary>
    public string SessionId => registration.ContextId;

    /// <summary>客户端类型决定固定入口, 请求参数不能扩大角色或写入权限.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            if (disposal is not null) throw new AppException("HOST_CONTEXT_CLOSED", "当前业务会话正在关闭", 3);
            calls.Add(completion);
        }
        try { return await InvokeCoreAsync(method, parameters, token); }
        finally { lock (lifecycle) { calls.Remove(completion); completion.TrySetResult(); } }
    }

    /// <summary>只在已登记的调用生命周期内执行固定客户端分发.</summary>
    private async Task<JsonElement> InvokeCoreAsync(string method, JsonElement parameters, CancellationToken token)
    {
        JsonInputValidation.RejectDuplicates(parameters);
        if (registration.Options.Kind == "gui") return await InvokeGuiAsync(method, parameters, token);
        if (registration.Options.Kind == "mcp" && mcp is not null)
        {
            if (method == "mcp.tools")
            {
                RequireEmpty(parameters);
                return GuiHostJson.Element(mcp.Tools);
            }
            if (method == "mcp.call")
            {
                var request = parameters.Deserialize<CallToolRequestParams>(JsonSerializerOptions.Web)
                    ?? throw new AppException("INVALID_REQUEST", "MCP 请求为空", 2);
                var result = await mcp.CallAsync(request, token);
                return GuiHostJson.Element(new { result.IsError, result.StructuredContent });
            }
        }
        if (registration.Options.Kind == "cli" && method == "cli.run")
        {
            var input = parameters.Deserialize<CliInput>(GuiHostJson.Options) ?? throw Invalid();
            if (input.Args.Length == 0 || input.Args.Length > 128 || input.Args.Any(value => value is null || value.Length > 32768)
                || !Path.IsPathFullyQualified(input.WorkingDirectory) || input.WorkingDirectory.Length > 2048) throw Invalid();
            var result = await CliBusinessCommands.RunAsync(input.Args, userRoot, logging.Factory, token,
                progress: SendCliProgress, workingDirectory: input.WorkingDirectory);
            await FlushAsync();
            return GuiHostJson.Element(result);
        }
        throw new AppException("HOST_METHOD_NOT_ALLOWED", "此客户端不能调用该后台入口", 3);
    }

    /// <summary>只在本 MCP 上下文调用白名单模型能力, 凭据和 GUI 回答不在目录内.</summary>
    public async Task<McpDesktopResult> InvokeAsync(string method, JsonElement parameters, long? connectionRevision, CancellationToken token)
    {
        if (registration.Options.Kind != "mcp" || !McpDesktopCatalog.TryGet(method, out var operation))
            throw new AppException("MCP_METHOD_NOT_ALLOWED", "此方法不是本客户端可用能力", 3);
        if (!operation.ReadOnly && !registration.Options.AllowWrite)
            throw new AppException("MCP_WRITE_DISABLED", "当前 MCP 为只读模式", 3);
        long? observed = null;
        var value = await bridge!.InvokeMcpAsync(method, parameters, token, connectionRevision, revision => observed = revision);
        if (value is McpDesktopResult classified) return classified with { ConnectionRevision = observed };
        var outcome = BusinessOutcome.From(value is ModelConfigurationContext configuration ? configuration.Project : value);
        return new(value, outcome.IsError, outcome.ErrorCode, outcome.NeedsVerification, observed);
    }

    /// <summary>后台业务只排队结构化消息, 不在提交回调同步等待远端 UI.</summary>
    private void Send(string message)
    {
        if (gui is null || deliveryFailure is not null) return;
        var value = new GuiHostMessage(gui.CurrentEpoch ?? document.Epoch, JsonSerializer.Deserialize<JsonElement>(message));
        if (!outbound.Writer.TryWrite(new("bridge.message", GuiHostJson.Element(value))))
            throw new AppException("GUI_DELIVERY_BUSY", "前台消息未能及时送达, 请检查连接", 3);
    }

    /// <summary>单写序列保持事件/终态顺序; 断线只停止投递, 不把模型提交误当取消.</summary>
    private async Task DeliverAsync()
    {
        await foreach (var item in outbound.Reader.ReadAllAsync())
        {
            if (item.Name is not null && deliveryFailure is null)
            {
                try { await peer.SendEventAsync(item.Name, item.Payload); }
                catch (Exception error)
                {
                    deliveryFailure = error;
                    using var operation = BusinessLog.Begin(logging.Factory.CreateLogger<BusinessContext>(), "Host.GuiDelivery");
                    operation.Fail(error);
                }
            }
            item.Barrier?.TrySetResult();
        }
    }

    /// <summary>等待本轮已排队事件实际完成投递, 失败保留可靠业务状态且不重放.</summary>
    private async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await outbound.Writer.WriteAsync(new(null, Barrier: completion));
        await completion.Task;
        if (deliveryFailure is not null) throw new AppException("BUSINESS_HOST_CONNECTION_LOST", "前台连接已中断, 请核验操作结果, 不得自动重放", 3);
    }

    /// <summary>EOF 只关闭本连接, 提交和回执排空后才释放账号、工作区及消息队列.</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task[] pending;
        lock (lifecycle) pending = [.. calls.Select(call => call.Task)];
        Exception? failure = null;
        try { if (mcp is not null) await mcp.DisposeAsync(); }
        catch (Exception error) { failure = error; }
        try { if (bridge is not null) await bridge.ShutdownAsync(); }
        catch (Exception error) { failure ??= error; }
        finally
        {
            // Bridge Flight 完成后, 原 GUI 调用仍可能正在 flush 最后一条结果, 必须先排空外层请求再关闭发送队列.
            await Task.WhenAll(pending);
            dialogs?.Dispose();
            cloud?.Dispose();
            outbound.Writer.TryComplete();
            await delivery;
        }
        if (failure is not null) throw new AppException("SHUTDOWN_FAILED", "当前业务会话资源未能完整释放", 4);
    }

    /// <summary>CLI 阶段进入同一个有界发送队列, 不同步阻塞提交或丢弃未观察异常.</summary>
    private void SendCliProgress(JsonElement value)
    {
        if (deliveryFailure is not null) return;
        if (!outbound.Writer.TryWrite(new("cli.progress", value.Clone())))
            throw new AppException("CLI_DELIVERY_BUSY", "CLI 阶段消息未能及时送达", 3);
    }

    private sealed record CliInput(string[] Args, string WorkingDirectory);
    private static AppException Invalid() => new("INVALID_REQUEST", "后台请求字段无效", 2);
    private static void RequireEmpty(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any()) throw Invalid();
    }
}
