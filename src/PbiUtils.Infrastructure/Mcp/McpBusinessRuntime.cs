// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : MCP 工具白名单, 严格 JSON 输入, 启动权限和统一业务调用生命周期.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Infrastructure.Updates;

using PbiUtils.Application.Input;
using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>共享业务宿主为每个客户端创建独立运行时, 计划、权限和取消状态不跨连接共享.</summary>
public sealed partial class McpBusinessRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions InputJson = CreateInputOptions();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string userRoot;
    private readonly ILoggerFactory logs;
    private readonly ScaffoldRuntime scaffold;
    private readonly ChangeService changes;
    private readonly SampleDataService sample;
    private readonly ProductUpdateService productUpdates;
    private ExecutionJournal.Execution? currentExecution;
    private bool stopping;
    private readonly IMcpDesktopContext? desktopContext;
    public bool AllowWrite { get; }
    public IList<Tool> Tools => entries.Values.Select(value => value.Tool).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    /// <summary>构建实际共享服务, 不登录或自动联网; 可选传输工厂仅由组合方注入, 不公开为 MCP 参数.</summary>
    /// <param name="allowWrite">进程启动时确定的写入许可.</param>
    /// <param name="userRoot">宿主固定的应用存储根.</param>
    /// <param name="logs">现有业务日志工厂.</param>
    /// <param name="clientFactory">隔离测试可替换 HTTP 传输, 仍执行真实 Git 解析、快照事务及回执.</param>
    /// <param name="desktopContext">共享宿主内此连接专属的模型上下文, 不由工具参数构造.</param>
    /// <param name="productUpdates">可选受信更新用例, 隔离测试替换传输; 工具不能注入服务或地址.</param>
    public McpBusinessRuntime(bool allowWrite, string userRoot, ILoggerFactory logs, Func<UdfSourceEntry, HttpClient>? clientFactory = null,
        IMcpDesktopContext? desktopContext = null, ProductUpdateService? productUpdates = null)
    {
        AllowWrite = allowWrite;
        this.userRoot = userRoot;
        this.logs = logs;
        this.desktopContext = desktopContext;
        this.productUpdates = productUpdates ?? ProductUpdateFactory.Create(userRoot);
        scaffold = new ScaffoldRuntime(userRoot, logs, clientFactory);
        changes = new ChangeService(new ChangeWorkspace(logger: logs.CreateLogger<ChangeWorkspace>()), TimeProvider.System, logs.CreateLogger<ChangeService>());
        sample = new SampleDataService(new SampleDataAdapter(logs.CreateLogger<SampleDataAdapter>()), logs.CreateLogger<SampleDataService>());
        RegisterCoreTools();
        RegisterDesktopTools();
    }

    /// <summary>桌面适配由同一组合根注册, 不在核心工具复制 UI 或 Windows 业务.</summary>
    partial void RegisterDesktopTools();

    /// <summary>共享严格构造字段规则并拒绝向非空字段传 null, schema 与反序列化使用同一选项.</summary>
    private static JsonSerializerOptions CreateInputOptions()
    {
        var options = ScaffoldInputJson.Create();
        options.RespectNullableAnnotations = true;
        return options;
    }

    /// <summary>由真实输入类型生成 JSON schema, 拒绝缺必填项、未知字段、重复字段及超限文本.</summary>
    private void Register<T>(string name, string description, bool writes, Func<T, CancellationToken, Task<object>> invoke, bool openWorld = false)
    {
        var schemaNode = InputJson.GetJsonSchemaAsNode(typeof(T));
        // MCP arguments 始终是对象; 类型导出器对根引用类型默认加入 null, 此处按实际协议收紧.
        schemaNode["type"] = "object";
        var schema = JsonSerializer.SerializeToElement(schemaNode);
        RegisterRaw(name, description, writes, schema, (input, token) =>
        {
            JsonInputValidation.RejectDuplicates(input);
            T value;
            try { value = input.Deserialize<T>(InputJson) ?? throw new AppException("INVALID_ARGUMENT", "工具参数不能为空"); }
            catch (JsonException) { throw new AppException("INVALID_ARGUMENT", "工具参数存在缺失、未知字段或类型错误"); }
            return invoke(value, token);
        }, openWorld);
    }

    /// <summary>统一发布等价的可移植 schema, 原始契约与运行时参数验证保持; 授权仍由公共分发边界强制检查.</summary>
    /// <param name="name">固定工具名.</param>
    /// <param name="description">客户端可见的业务说明.</param>
    /// <param name="writes">真实副作用标记, 不由 schema 授权.</param>
    /// <param name="inputSchema">导出器或嵌入契约提供的权威输入结构.</param>
    /// <param name="invoke">现有参数校验和业务执行入口.</param>
    /// <param name="openWorld">是否可能访问外部环境的说明标记.</param>
    /// <exception cref="InvalidOperationException">schema 结构读取或转换失败时直接传播, 不发布放宽后的替代契约.</exception>
    private void RegisterRaw(string name, string description, bool writes, JsonElement inputSchema, Func<JsonElement, CancellationToken, Task<object>> invoke, bool openWorld = false)
        => entries.Add(name, new Entry(new Tool
        {
            Name = name,
            Description = description,
            InputSchema = McpInputSchema.CreatePortable(inputSchema),
            Annotations = new ToolAnnotations { ReadOnlyHint = !writes, DestructiveHint = writes, IdempotentHint = !writes, OpenWorldHint = openWorld }
        }, writes, invoke));

    /// <summary>未知工具是协议错误; 参数与业务失败是可见工具错误, 不回传任意异常或日志正文.</summary>
    /// <param name="request">官方 SDK 已解析的参数.</param>
    /// <param name="token">请求取消令牌, 提交边界由共享服务控制.</param>
    /// <returns>结构化 JSON 及相同的文本结果, 失败带稳定错误码.</returns>
    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? request, CancellationToken token)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || !entries.TryGetValue(request.Name, out var entry)) throw new McpProtocolException("Unknown tool", McpErrorCode.InvalidParams);
        var entered = false;
        ExecutionJournal.Execution? execution = null;
        using var operation = BusinessLog.Begin(logs.CreateLogger("Mcp.Tools"), "Mcp.Execute");
        try
        {
            if (stopping) throw new AppException("MCP_STOPPING", "MCP 正在关闭", 3);
            if (entry.Writes && !AllowWrite) throw new AppException("MCP_WRITE_DISABLED", "当前 MCP 为只读模式; 仅用户可在客户端启动参数中添加 --allow-write", 3);
            var input = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>(), JsonSerializerOptions.Web);
            if (Encoding.UTF8.GetByteCount(input.GetRawText()) > 4 * 1024 * 1024) throw new AppException("INPUT_TOO_LARGE", "工具参数超过 4 MiB", 2);
            JsonInputValidation.RejectDuplicates(input);
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new AppException("BUSY", "此 MCP 进程正在执行另一业务操作", 3);
            entered = true;
            if (stopping) throw new AppException("MCP_STOPPING", "MCP 正在关闭", 3);
            // 模型入口由Bridge持有回执; 核心文件入口在同一后台持有统一账本租约.
            if (entry.Writes && !request.Name.StartsWith("desktop_", StringComparison.Ordinal))
            {
                execution = new ExecutionJournal(userRoot).Prepare(Guid.NewGuid().ToString("N"), request.Name, input, "local-files");
                currentExecution = execution;
            }
            var result = await entry.Invoke(input, token).ConfigureAwait(false);
            return CompleteResult(result, execution, operation);
        }
        catch (Exception error)
        {
            operation.Fail(error);
            var (code, message) = error switch
            {
                AppException app => (app.Code, app.Message),
                JsonException => ("INVALID_ARGUMENT", "工具参数 JSON 字段或类型无效"),
                OperationCanceledException => ("CANCELLED", "操作已取消"),
                _ => ("EXECUTION_FAILED", "工具执行失败, 请检查运行环境与业务日志")
            };
            try { execution?.Finish(false, code); }
            catch (AppException receiptError)
            { return Result(new { error = new { code = receiptError.Code, message = receiptError.Message, causeCode = code } }, true); }
            if (error is OperationCanceledException) throw;
            return Result(new { error = new { code, message } }, true);
        }
        finally
        {
            if (entered)
            {
                currentExecution = null;
                execution?.Dispose();
                gate.Release();
            }
        }
    }

    /// <summary>仅由共享服务的真实文件提交回调调用; 回执持久失败则禁止进入实际副作用.</summary>
    private void CommitLocalWrite() => (currentExecution
        ?? throw new AppException("RECEIPT_REQUIRED", "缺少当前 MCP 写入回执, 未提交", 3)).CommitStarted();

    /// <summary>结果上限在序列化后执行; 不截断成功结果或把部分对象伪装为完整响应.</summary>
    private static CallToolResult Result(object? value, bool error)
    {
        var structured = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        var text = structured.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > 16 * 1024 * 1024)
        {
            structured = JsonSerializer.SerializeToElement(new { error = new { code = "OUTPUT_TOO_LARGE", message = "结果超过 16 MiB; 请缩小请求范围. 若调用执行写入, 此限制不表示未提交, 请先检查实际产物." } });
            text = structured.GetRawText(); error = true;
        }
        return new CallToolResult { IsError = error, StructuredContent = structured, Content = [new TextContentBlock { Text = text }] };
    }

    /// <summary>停止接收后等待已开始的提交和回读完成, 再清理本运行时持有的预览.</summary>
    public async ValueTask DisposeAsync()
    {
        stopping = true;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var plan in configurationPlans.Values) plan.Request.Configuration?.Dispose();
            configurationPlans.Clear();
            // 模型上下文由连接组合根拥有并排空, 不能随核心工具释放而提前销毁.
            try
            {
                try { if (scaffoldDigest is { } digest) await DiscardScaffoldAsync(digest, CancellationToken.None).ConfigureAwait(false); }
                finally { changes.Dispose(); }
            }
            finally { scaffold.Dispose(); }
        }
        finally { gate.Release(); }
    }

    /// <summary>显式白名单项; 工具文本和参数不能改变宿主标记的写入能力.</summary>
    private sealed record Entry(Tool Tool, bool Writes, Func<JsonElement, CancellationToken, Task<object>> Invoke);
}
