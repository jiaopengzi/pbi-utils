// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 实现会话级白名单分发, 参数校验, 超时取消与资源清理.

using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Protocol;
using PbiUtils.Host.Bridge.Connections;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Host.Bridge.Calendar;
using PbiUtils.Application.Settings;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Diagnostics;
using PbiUtils.Application.Connections;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;
using PbiUtils.Infrastructure.Udf;

using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;

namespace PbiUtils.Host.Bridge.Session;

/// <summary>单 WebView 会话的白名单分发、取消竞争与资源管理.</summary>
/// <param name="origin">真实来源校验策略.</param>
/// <param name="doctor">共享诊断.</param>
/// <param name="send">宿主结构化消息发送器.</param>
/// <param name="runtime">WebView2 版本.</param>
/// <param name="powerBi">共享只读 Power BI 实例用例.</param>
/// <param name="timeout">测试可控超时, 默认 30 秒.</param>
/// <param name="requestTimeProvider">请求时限时钟, 默认使用系统时钟, 测试可手动推进.</param>
/// <param name="uiSettings">独立语言偏好用例, 不影响模型写入与 工具精确授权.</param>
/// <param name="dialogs">宿主绑定的一次性前端对话框, 不向业务或外部工具 提供动态写入授权.</param>
/// <param name="updateLearningView">更新独立无桥接学习浏览器的受限显示区域.</param>
/// <param name="closeLearningView">释放当前窗口拥有的学习浏览器.</param>
/// <param name="openLearningSite">由系统浏览器打开宿主固定学习站点.</param>
/// <param name="checkUserDataReset">宿主固定用户目录的只读安全预检, 不加载凭据或删除数据.</param>
/// <param name="scheduleUserDataReset">双确认后仅安排正常关闭, 实际删除在浏览器和日志释放后执行.</param>
/// <param name="mcpConnections">仅已认证 GUI 注入的 MCP 连接管理能力, 不向 MCP 业务目录开放.</param>
/// <param name="productUpdates">独立产品更新查询与偏好, 不持有模型业务锁.</param>
/// <param name="openUpdateLink">仅 GUI 组合根注入, 打开已核验的固定仓库发布目标.</param>
/// <param name="externalToolTarget">仅已认证 GUI 启动上下文注入的一次性 Desktop 候选, 网页不能提供.</param>
public sealed partial class BridgeSession(OriginPolicy origin, DoctorService doctor,
    Action<string> send, string runtime, TimeSpan? timeout = null, PowerBiService? powerBi = null,
    ProjectService? projects = null, Func<CancellationToken, Task<string?>>? selectProject = null,
    IDisposable? ownedWorkspace = null,
    PbiUtils.Infrastructure.Connections.LocalRecentProjects? recentProjects = null,
    ModelConnectionService? connections = null,
    RemoteConnectionForm? remoteForm = null,
    PowerBiServiceConnection? cloudConnection = null, LoggingService? logging = null, Func<CancellationToken, Task>? openLogDirectory = null,
    ILogger<BridgeSession>? logger = null, TimeProvider? requestTimeProvider = null, ModelEditService? edits = null, ToolWritePolicy? toolWrites = null,
    SampleDataService? samples = null, string? sampleDirectory = null, Func<CancellationToken, Task<string?>>? chooseSampleDirectory = null,
    Func<string, string, CancellationToken, Task<bool>>? confirmSample = null, Func<string, CancellationToken, Task>? openSampleDirectory = null,
    UiSettingsService? uiSettings = null, UiDialogBroker? dialogs = null, ScaffoldBridge? scaffold = null,
    ModelConfigurationBridge? configuration = null, ModelTableExportService? tableExports = null,
    Func<ModelTableExportRequest, CancellationToken, Task<string?>>? chooseTableExportFile = null,
    IModelTableStatisticsReader? tableStatistics = null,
    LocalModelExportLocationStore? exportLocations = null,
    Func<string, CancellationToken, Task>? openExportDirectory = null,
    IModelTableRowCountReader? tableRowCounts = null,
    Func<LearningViewRequest, CancellationToken, Task>? updateLearningView = null,
    Func<CancellationToken, Task>? closeLearningView = null, Func<CancellationToken, Task>? openLearningSite = null,
    ExecutionJournal? mcpJournal = null,
    Func<CancellationToken, Task>? checkUserDataReset = null, Action? scheduleUserDataReset = null,
    IBusinessHostMcpConnections? mcpConnections = null,
    ProductUpdateService? productUpdates = null, Func<Uri, CancellationToken, Task>? openUpdateLink = null,
    ExternalToolLaunchTarget? externalToolTarget = null) : IDisposable
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonSerializerOptions SampleJson = new(StrictJson) { PropertyNameCaseInsensitive = false };

    private readonly ILogger log = logger ?? NullLogger<BridgeSession>.Instance;
    private ExternalToolLaunchTarget? pendingExternalToolTarget = externalToolTarget;
    private readonly Lock sync = new();
    private readonly Dictionary<string, Flight> flights = [];
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private bool disposed;
    private bool resourcesReleased;
    private Exception? shutdownFailure;
    private Task? scaffoldShutdown;
    private string lifecycle = "Running";
    private string? projectPath;
    private string? workspaceId;
    private string? connectionKind;
    private long connectionRevision;
    private readonly SemaphoreSlim projectGate = new(1, 1);
    private readonly SemaphoreSlim calendarPreviewGate = new(1, 1);
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private string sampleOutput = sampleDirectory ?? samples?.RestoreOutputPath() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PowerBISampleData");

    /// <summary>用于验证资源回收的在途数量.</summary>
    public int PendingCount { get { lock (sync) return flights.Count; } }
    /// <summary>关闭先停止接收并等待提交取得结果, 最后释放资源.</summary>
    public string Lifecycle { get { lock (sync) return lifecycle; } }
    /// <summary>原生关闭前检查未保存草稿, 不隐式写入.</summary>
    public bool HasPendingChanges => edits?.HasPendingChanges == true || configuration?.HasPendingChanges == true;

    /// <summary>接收真实宿主来源和 JSON 消息, 发送唯一结果或错误.</summary>
    /// <param name="source">宿主真实来源, 不取自 JSON.</param>
    /// <param name="message">请求文档, 限 64 KiB.</param>
    /// <returns>请求处理结束的任务.</returns>
    public async Task ReceiveAsync(string source, string message, CancellationToken cancellationToken = default)
    {
        _ = await ReceiveCoreAsync(source, message, null, cancellationToken);
    }

    /// <summary>网页和受信本机 MCP 共用来源、参数、时限、取消及提交排空; MCP 响应只返回调用方.</summary>
    /// <param name="source">网页真实来源, 或由内部 MCP 入口固定的受信来源.</param>
    /// <param name="message">同一版本与预算的完整请求信封.</param>
    /// <param name="mcpOperation">仅由内部白名单查询取得的 MCP 能力; 网页不能设置.</param>
    /// <param name="externalToken">本机调用取消, 提交开始后仍须排空.</param>
    /// <returns>实际业务结果、稳定错误及本次请求绑定的连接代际, 不包含异常原文.</returns>
    private async Task<(object? Result, object? Error, int ExitCode, long? ConnectionRevision)> ReceiveCoreAsync(string source, string message, McpDesktopOperation? mcpOperation, CancellationToken externalToken)
    {
        // 非可信来源完全不接收桥接结果, 避免向外站泄露数据.
        if (!origin.Allows(source)) return (null, null, 0, null);
        string? id = null;
        Flight? flight = null;
        object? result = null;
        object? error = null;
        BusinessOperation? operation = null;
        Exception? failure = null;
        var isDialogResponse = false;
        CancellationTokenRegistration externalCancellation = default;
        try
        {
            var messageBytes = Encoding.UTF8.GetByteCount(message);
            if (messageBytes > CalendarBridgeBudget.Absolute) throw new AppException("MESSAGE_TOO_LARGE", "消息超过允许的绝对大小");
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) throw new AppException("INVALID_REQUEST", "请求必须为对象");
            var names = request.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != names.Distinct().Count() || names.Any(n => n is not ("protocolVersion" or "id" or "method" or "params" or "connectionRevision")))
                throw new AppException("INVALID_REQUEST", "未知或重复字段");
            if (!request.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id = idElement.GetString()) || id.Length > 100)
                throw new AppException("INVALID_REQUEST", "id 必须为 1–100 字符");
            if (!request.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != 1)
                throw new AppException("UNSUPPORTED_PROTOCOL", "仅支持 protocolVersion=1");
            if (!request.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String
                || !request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                throw new AppException("INVALID_REQUEST", "缺少 method 或对象 params");
            var method = methodElement.GetString()!;
            var requestedRevision = 0L;
            var hasRequestedRevision = request.TryGetProperty("connectionRevision", out var revisionElement);
            if (hasRequestedRevision
                && (revisionElement.ValueKind != JsonValueKind.Number || !revisionElement.TryGetInt64(out requestedRevision)
                    || requestedRevision is < 0 or > 9007199254740991))
                throw new AppException("INVALID_REQUEST", "连接代际必须为有效非负整数");
            CalendarBridgeBudget.Validate(method, parameters, messageBytes);
            isDialogResponse = method == "ui.dialogRespond";
            // 仅记录白名单方法; 不将客户端自报的方法名或请求正文写入日志.
            operation = BusinessLog.Begin(log, KnownMethod(method) ? "Bridge." + method : "Bridge.Unknown", root: true);
            var parameterNames = parameters.EnumerateObject().Select(p => p.Name).ToArray();
            if (parameterNames.Length != parameterNames.Distinct().Count())
                throw new AppException("VALIDATION_ERROR", "参数字段不可重复");
            lock (sync)
            {
                // 关闭失败后的宿主提示仍需受理其一次性回答, 不重新开放其他业务方法.
                ObjectDisposedException.ThrowIf(disposed && !isDialogResponse, this);
                if (resettingUserData && method is not ("ui.dialogRespond" or "request.cancel"))
                    throw new AppException("BUSY", "用户数据重置正在确认或关闭, 暂不接收其他操作", 3);
                if (flights.ContainsKey(id)) throw new AppException("DUPLICATE_ID", "id 已在执行");
                if (flights.Count >= 64 && method != "ui.dialogRespond") throw new AppException("BUSY", "在途请求过多", 4);
                if (method == "app.resetUserData")
                {
                    if (flights.Count != 0) throw new AppException("BUSY", "请等待当前操作完成后再重置用户数据", 3);
                    resettingUserData = true;
                }
                flight = new Flight
                {
                    Id = id,
                    Method = method,
                    IsMcp = mcpOperation is not null,
                    ConnectionRevision = mcpOperation is null ? requestedRevision
                        : CanObserveMcpConnection(method, parameters) ? connectionRevision
                        : hasRequestedRevision ? requestedRevision : -1,
                    BindsConnection = mcpOperation is null ? UsesConnection(method, parameters) : UsesMcpConnection(method, parameters)
                };
                RequireConnectionRevision(flight);
                flights.Add(id, flight);
            }
            externalCancellation = externalToken.Register(() => Cancel(id!, "CANCELLED"));
            externalToken.ThrowIfCancellationRequested();
            // 远端填写不创建在途请求; 只有用户提交才开始独立的30秒连接预算.
            flight.Timer = ArmTimeout(id, flight, timeout ?? (mcpOperation is { ReadOnly: false } ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(method == "app.resetUserData" ? 390 : method == "udf.catalogRefresh" ? 600 : method is "project.open" or "project.openRecent" or "scaffold.openDesktop" or "powerbi.serviceSignIn" or "model.edit" or "model.save" or "model.exportTables" or "model.tableRowCounts" or "model.openExportDirectory" or "sample.chooseDirectory" or "sample.generate" or "sample.clear" ? 180 : 30)),
                method == "project.connectRemote" ? "RemoteConnect" : method == "project.open" ? "FileSelection" : "Request");
            result = mcpOperation is null ? await DispatchGuiAsync(method, parameters, id, flight)
                : await DispatchMcpAsync(mcpOperation, parameters, id, flight);
        }
        catch (Exception ex)
        {
            failure = ex;
            var (code, safeMessage) = ex switch
            {
                AppException app => (app.Code, app.Message),
                JsonException => ("INVALID_REQUEST", "无法解析 JSON"),
                OperationCanceledException => ("CANCELLED", "操作已取消"),
                _ => ("EXECUTION_FAILED", "请求执行失败")
            };
            error = new { code, message = ModelTableExportService.DecorateCleanupFailure(ex, UdfCleanupNotice.Decorate(ex, safeMessage)) };
        }
        finally
        {
            flight?.Timer?.Dispose();
            externalCancellation.Dispose();
            try
            {
                lock (sync)
                {
                    if (flight is not null)
                    {
                        // 取消与完成在同一临界区裁决, 已接受取消永远不会再返回成功.
                        flights.Remove(id!);
                        if (flight.Method == "app.resetUserData" && !userDataResetScheduled) resettingUserData = false;
                        if (flight.Reason is { } reason)
                            error = new { code = reason, message = ModelTableExportService.DecorateCleanupFailure(failure, UdfCleanupNotice.Decorate(failure, reason == "TIMEOUT" ? "请求超时" : "操作已取消")) };
                        flight.Dispose();
                    }
                    if (disposed && flights.Count == 0) ReleaseResources();
                    if (mcpOperation is null && (!disposed || isDialogResponse))
                    {
                        // 预览完成到发回之间代际可能增长; 在同一发送锁内按最终真实信封复核, 不扩大预算.
                        if (error is null && flight?.Method == "calendar.preview")
                        {
                            try { CalendarBridgeBudget.PreviewResult(id, result, connectionRevision); }
                            catch (AppException ex)
                            {
                                failure = ex;
                                error = new { code = ex.Code, message = ex.Message };
                                result = null;
                            }
                        }
                        if (error is null) Send(new { protocolVersion = 1, id, connectionRevision, result });
                        else Send(new { protocolVersion = 1, id, connectionRevision, error });
                    }
                    if (flight?.Reason is { } finalReason) operation?.Reject(finalReason);
                    else if (failure is not null) operation?.Fail(failure);
                    else if (error is null)
                    {
                        // GUI 更新保留可展示的 failed 结果, 日志仍按共享事实记录唯一失败终态.
                        var businessError = result is ProductUpdateCheckResponse updates ? BusinessOutcome.From(updates).ErrorCode
                            : mcpOperation is null ? null : result is McpDesktopResult wrapped
                            ? wrapped.IsError ? wrapped.ErrorCode ?? "EXECUTION_FAILED" : null
                            : BusinessOutcome.From(result is ModelConfigurationContext context ? context.Project : result).ErrorCode;
                        if (businessError is not null) operation?.Reject(businessError);
                        else operation?.Complete();
                    }
                }
            }
            finally { operation?.Dispose(); flight?.Completed.TrySetResult(); }
        }
        return (result, error, flight?.Reason == "CANCELLED" ? 130 : failure is AppException appFailure ? appFailure.ExitCode : failure is OperationCanceledException ? 130 : 4,
            flight is { BindsConnection: true } ? flight.ConnectionRevision : null);
    }

    /// <summary>用于日志安全命名, 分发仍由 switch 白名单决定.</summary>
    private static bool KnownMethod(string? method) => method is not null && BridgeMethods.All.Contains(method);


}
