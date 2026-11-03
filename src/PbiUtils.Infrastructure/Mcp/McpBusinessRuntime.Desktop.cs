// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Desktop.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将当前桌面全部受支持业务契约注册为 MCP 工具, 继续由可信宿主核验身份、写权限与可靠回执.

using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    private static readonly string[] DesktopRequiredArguments = ["parameters"];
    private readonly Dictionary<string, long> observedDesktopRevisions = new(StringComparer.Ordinal);

    /// <summary>逐业务注册真实参数结构; 页面内部控制和凭据提交不属于外部 MCP 工具.</summary>
    partial void RegisterDesktopTools()
    {
        var emptySchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new { } });
        RegisterRaw("desktop_sessions", "List this client's isolated context in the shared local business host. GUI and other MCP clients can coexist, but cannot share this context or its permissions.",
            false, emptySchema, (input, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any()) throw new AppException("INVALID_ARGUMENT", "此工具不接受参数");
                var context = RequireDesktopContext();
                return Task.FromResult<object>(new { sessions = new[] { new McpDesktopSession(context.SessionId, Environment.ProcessId, "", true) } });
            });
        foreach (var operation in McpDesktopCatalog.All)
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
                required = DesktopRequiredArguments,
                properties = new
                {
                    sessionId = new { type = "string", description = "Exact desktop_sessions context identity; optional for this client's own isolated context.", maxLength = 100 },
                    parameters = operation.InputSchema
                }
            });
            if (McpDesktopRead.Supports(operation.Method))
            {
                var expanded = JsonNode.Parse(schema.GetRawText())!;
                expanded["properties"]!["read"] = McpDesktopRead.Schema(operation.Method);
                schema = JsonSerializer.SerializeToElement(expanded);
            }
            RegisterRaw("desktop_" + operation.Method.Replace('.', '_'),
                operation.Description + " Uses a same-installation host, automatically started in the background when needed. "
                + (McpDesktopRead.Supports(operation.Method) ? "Defaults to a compact summary. Use optional read.section (tables/columns/measures/functions/configuration), limit 1-100 and offset for details; columns requires table. Later pages require read.projectId and read.baselineId. Expressions are opt-in with includeExpressions. Configuration section is only available for configuration_context. " : ""),
                !operation.ReadOnly, schema, (input, token) => InvokeDesktopAsync(operation, input, token),
                operation.Method.StartsWith("powerbi.service", StringComparison.Ordinal) || operation.Method is "udf.catalogRefresh" or "learning.open");
        }
    }

    /// <summary>校验外部包装参数后只发送白名单业务内容, 不让调用方改变宿主或接收器.</summary>
    private async Task<object> InvokeDesktopAsync(McpDesktopOperation operation, JsonElement input, CancellationToken token)
    {
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(property => property.Name is not ("parameters" or "sessionId" or "read"))
            || !input.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw new AppException("INVALID_ARGUMENT", "桌面工具需要 parameters 对象和可选 sessionId");
        var read = McpDesktopRead.Parse(operation.Method, input);
        string? sessionId = null;
        if (input.TryGetProperty("sessionId", out var selected))
        {
            if (selected.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sessionId = selected.GetString()) || sessionId.Length > 100)
                throw new AppException("INVALID_ARGUMENT", "桌面会话身份无效");
        }
        token.ThrowIfCancellationRequested();
        var context = RequireDesktopContext();
        if (sessionId is not null && sessionId != context.SessionId)
            throw new AppException("MCP_DESKTOP_SESSION_OWNED", "此上下文不属于当前客户端, 请使用本客户端会话", 3);
        sessionId = context.SessionId;
        var result = await context.InvokeAsync(operation.Method, parameters,
            observedDesktopRevisions.TryGetValue(sessionId, out var revision) ? revision : null, token).ConfigureAwait(false);
        // 只采用该业务结果实际观察的代际; 错误和无关目录/设置读取不能使旧模型请求重新获得写权限.
        var projected = read.Apply(result, McpDesktopRead.Supports(operation.Method));
        if (!result.IsError && result.ConnectionRevision is { } observed) observedDesktopRevisions[sessionId] = observed;
        return projected;
    }

    /// <summary>仅使用组合根注入的独立上下文, 不枚举或自动采用其他客户端与前台窗口.</summary>
    private IMcpDesktopContext RequireDesktopContext() => desktopContext
        ?? throw new AppException("MCP_DESKTOP_REQUIRED", "当前运行缺少共享业务宿主上下文", 3);
}
