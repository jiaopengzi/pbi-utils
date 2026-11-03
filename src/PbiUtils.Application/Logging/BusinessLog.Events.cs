// FilePath    : pbi-utils\src\PbiUtils.Application\Logging\BusinessLog.Events.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 集中强类型日志模板和等级检查, 启用后才计算诊断字段, 不向日志传原始异常.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace PbiUtils.Application.Logging;

/// <summary>固定事件由编译期日志生成器过滤; 昂贵字段在公开封装内延迟计算.</summary>
public static partial class BusinessLog
{
    /// <summary>记录业务开始, 保留原 Operation/Outcome 模板, 统一设置稳定事件名.</summary>
    internal static void Started(ILogger logger, string operation) => StartedCore(logger, operation, "Started");

    /// <summary>终态已由 BusinessOperation 裁决, 等级关闭时不构造耗时、异常链或栈.</summary>
    internal static void Ended(ILogger logger, string operation, string outcome, LogLevel level, long started, string? code, Exception? error)
    {
        if (!logger.IsEnabled(level)) return;
        List<string> types = [], hresults = [], stacks = [];
        for (var current = error; current is not null && types.Count < 6; current = current.InnerException)
        {
            types.Add(current.GetType().FullName ?? current.GetType().Name);
            hresults.Add($"0x{current.HResult:X8}");
            // StackTrace 不读取异常正文或源文件路径, 仅保留可用于定位的代码调用位置.
            var stack = new StackTrace(current, false).ToString();
            stacks.Add(stack.Length > 4000 ? stack[..4000] : stack);
        }
        var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
        var errorTypes = string.Join(" -> ", types);
        var hResultCodes = string.Join(" -> ", hresults);
        var safeStack = string.Join("\n", stacks);
        EndedCore(logger, level, operation, outcome, elapsedMs, code, errorTypes, hResultCodes, safeStack);
    }

    /// <summary>从原始超时值延迟计算毫秒, 日志关闭时不计算默认时限.</summary>
    public static void RemoteConnectionSubmitted(ILogger logger, TimeSpan? timeout)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var timeoutMs = (timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        RemoteConnectionSubmittedCore(logger, "SelectionSubmitted", timeoutMs);
    }

    /// <summary>从原始 TimeSpan 延迟取得毫秒, 不在业务调用点判断等级.</summary>
    public static void BridgeTimeout(ILogger logger, string stage, TimeSpan duration)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        BridgeTimeoutCore(logger, stage, duration.TotalMilliseconds);
    }

    /// <summary>仅记录固定的身份模式, 不接受账号或凭据, 关闭 Debug 不映射显示文本.</summary>
    public static void RemoteDialogSubmitted(ILogger logger, bool explicitIdentity, bool remembered)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        RemoteDialogSubmittedCore(logger, "Submitted", explicitIdentity ? "WindowsExplicit" : "CurrentIdentity", remembered);
    }

    /// <summary>启用 Debug 才调用平台适配器的身份级别读取, 不接收身份对象或账号.</summary>
    /// <param name="readLevel">平台提供的只读采集器, 建议传静态方法以避免捕获分配.</param>
    public static void WindowsNetworkContext(ILogger logger, Func<TokenImpersonationLevel?> readLevel)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var impersonationLevel = readLevel()?.ToString() ?? "None";
        WindowsNetworkContextCore(logger, "OutboundContextApplied", impersonationLevel);
    }

    /// <summary>仅在 Debug 启用后提取最多六层类型与安全数字码, 从不转发原始异常.</summary>
    /// <param name="stage">调用方固定的阶段名, 不含模型、账号或服务器.</param>
    /// <param name="error">只读取类型、HRESULT 和标准网络数字码的异常.</param>
    /// <param name="xmlaCodes">平台适配器的受限错误码提取器, 不将 SDK 依赖引入共享层.</param>
    public static void ConnectionFailure(ILogger logger, string stage, Exception error, Func<Exception, string[]>? xmlaCodes = null)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 6; current = current.InnerException, depth++)
        {
            var errorType = current.GetType().FullName;
            var hResult = $"0x{current.HResult:X8}";
            var socketError = current is SocketException socket ? socket.SocketErrorCode.ToString() : null;
            var nativeErrorCode = current is Win32Exception native ? native.NativeErrorCode : (int?)null;
            ConnectionFailureCore(logger, stage, depth, errorType, hResult, socketError, nativeErrorCode);
            var codes = xmlaCodes?.Invoke(current);
            if (codes is { Length: > 0 })
            {
                // 只接受平台提取器返回的前六个数字码, 防止适配器意外夹带错误正文.
                var safeCodes = codes.Take(6).Where(code => code is { Length: 10 }
                    && code.StartsWith("0x", StringComparison.Ordinal) && code.Skip(2).All(char.IsAsciiHexDigit)).ToArray();
                if (safeCodes.Length > 0)
                {
                    var safeCodeText = string.Join(" -> ", safeCodes);
                    XmlaFailureCore(logger, stage, safeCodeText);
                }
            }
        }
    }

    /// <summary>记录固定原生连接窗口阶段.</summary>
    [LoggerMessage(EventId = 0, EventName = "RemoteDialogStage", Level = LogLevel.Debug, Message = "Remote dialog stage {Stage}")]
    public static partial void RemoteDialogStage(ILogger logger, string stage);

    /// <summary>记录连接窗口的稳定校验码, 不包含异常正文.</summary>
    [LoggerMessage(EventId = 0, EventName = "RemoteDialogRejected", Level = LogLevel.Debug, Message = "Remote dialog stage {Stage}; code {ErrorCode}")]
    private static partial void RemoteDialogRejectedCore(ILogger logger, string stage, string errorCode);

    /// <summary>固定校验失败阶段, 调用方只提供已确认安全的 AppException.Code.</summary>
    public static void RemoteDialogRejected(ILogger logger, string errorCode) => RemoteDialogRejectedCore(logger, "ValidationRejected", errorCode);

    /// <summary>记录桥接取消裁决, 不包含任意客户端请求正文.</summary>
    [LoggerMessage(EventId = 0, EventName = "BridgeCancellation", Level = LogLevel.Debug, Message = "Bridge cancellation accepted {Reason} at {Stage}")]
    public static partial void BridgeCancellation(ILogger logger, string reason, string stage);

    /// <summary>记录固定 Windows 网络身份阶段, 不包含账号或令牌.</summary>
    [LoggerMessage(EventId = 0, EventName = "WindowsNetworkStage", Level = LogLevel.Debug, Message = "Windows network identity stage {Stage}")]
    public static partial void WindowsNetworkStage(ILogger logger, string stage);

    /// <summary>记录固定出站登录模式, 不包含登录输入.</summary>
    public static void WindowsNetworkPreparing(ILogger logger) => WindowsNetworkPreparingCore(logger, "Preparing", "NewCredentials");

    /// <summary>记录模型来源与固定认证方式, 不记录连接地址或模型名.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelConnectionSelected", Level = LogLevel.Debug, Message = "Model connection selected {EndpointKind}; authentication {AuthenticationMode}")]
    public static partial void ModelConnectionSelected(ILogger logger, string endpointKind, string authenticationMode);

    /// <summary>记录模型连接和架构读取的固定阶段.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelStage", Level = LogLevel.Debug, Message = "Model stage {Stage}")]
    public static partial void ModelStage(ILogger logger, string stage);

    /// <summary>记录只读架构断链后的一次重连, 只包含固定阶段、次数与安全数字码.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelReadRetry", Level = LogLevel.Warning, Message = "Read-only model connection attempt {Attempt}; stage {Stage}; native {NativeErrorCode}")]
    public static partial void ModelReadRetry(ILogger logger, string stage, int attempt, int nativeErrorCode);

    /// <summary>记录连接完成与来源, 不包括服务器或模型身份.</summary>
    public static void ModelConnected(ILogger logger, string endpointKind) => ModelConnectedCore(logger, "Connected", endpointKind);

    /// <summary>记录模型定位失败的零匹配或多匹配状态, 不记录模型名称.</summary>
    public static void ModelLookup(ILogger logger, int matches)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        ModelLookupCore(logger, "ModelLookup", matches == 0 ? "None" : "Multiple");
    }

    /// <summary>记录模型以 ID 或名称匹配, 不记录实际 ID 或名称.</summary>
    public static void ModelResolved(ILogger logger, bool byId)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        ModelResolvedCore(logger, "ModelResolved", byId ? "DatabaseId" : "DatabaseName");
    }

    /// <summary>记录 SSAS 网络探针固定阶段.</summary>
    [LoggerMessage(EventId = 0, EventName = "SsasStage", Level = LogLevel.Debug, Message = "SSAS stage {Stage}")]
    public static partial void SsasStage(ILogger logger, string stage);

    /// <summary>记录 SSAS 网络探针跳过或超时原因, 不记录地址.</summary>
    [LoggerMessage(EventId = 0, EventName = "SsasStageReason", Level = LogLevel.Debug, Message = "SSAS stage {Stage}; reason {Reason}")]
    public static partial void SsasStageReason(ILogger logger, string stage, string reason);

    /// <summary>记录探针固定时限, 不记录连接输入.</summary>
    public static void SsasProbeStarted(ILogger logger) => SsasProbeStartedCore(logger, "TcpProbeStarted", 5000);

    /// <summary>记录固定认证方式与密码是否保存, 不接受密文或凭据.</summary>
    [LoggerMessage(EventId = 0, EventName = "RemotePreferenceSaved", Level = LogLevel.Debug, Message = "Remote connection preference saved; authentication {AuthenticationMode}; password {PasswordSaved}")]
    public static partial void RemotePreferenceSaved(ILogger logger, string authenticationMode, bool passwordSaved);

    /// <summary>记录 staging 清理未完成, 保留原固定消息且不包含路径.</summary>
    [LoggerMessage(EventId = 0, EventName = "StagingCleanupIncomplete", Level = LogLevel.Warning, Message = "Business staging cleanup incomplete; original and published files preserved")]
    public static partial void StagingCleanupIncomplete(ILogger logger);

    /// <summary>记录历史临时文件清理未完成, 保留原固定消息且不包含路径.</summary>
    [LoggerMessage(EventId = 0, EventName = "HistoryCleanupIncomplete", Level = LogLevel.Warning, Message = "Business history temporary cleanup incomplete; original history preserved")]
    public static partial void HistoryCleanupIncomplete(ILogger logger);

    /// <summary>生成开始事件, Outcome 由封装固定为 Started.</summary>
    [LoggerMessage(EventId = 0, EventName = "BusinessStarted", Level = LogLevel.Information, Message = "Business {Operation} {Outcome}")]
    private static partial void StartedCore(ILogger logger, string operation, string outcome);

    /// <summary>仅记录安全终态字段, 异常槽保持为空.</summary>
    [LoggerMessage(EventId = 0, EventName = "BusinessEnded", Message = "Business {Operation} {Outcome} in {ElapsedMs} ms; code {ErrorCode}; types {ErrorTypes}; HRESULT {HResults}; stack {SafeStack}")]
    private static partial void EndedCore(ILogger logger, LogLevel level, string operation, string outcome, double elapsedMs,
        string? errorCode, string errorTypes, string hResults, string safeStack);

    /// <summary>生成原生连接提交事件, 毫秒仅由公开封装计算.</summary>
    [LoggerMessage(EventId = 0, EventName = "RemoteConnectionSubmitted", Level = LogLevel.Debug, Message = "Remote connection stage {Stage}; timeout {TimeoutMs}")]
    private static partial void RemoteConnectionSubmittedCore(ILogger logger, string stage, double timeoutMs);

    /// <summary>生成桥接超时事件, 保留原阶段和毫秒字段.</summary>
    [LoggerMessage(EventId = 0, EventName = "BridgeTimeout", Level = LogLevel.Debug, Message = "Bridge timeout armed at {Stage}; timeout {TimeoutMs}")]
    private static partial void BridgeTimeoutCore(ILogger logger, string stage, double timeoutMs);

    /// <summary>生成原生窗口提交事件, 不访问 UI 控件.</summary>
    [LoggerMessage(EventId = 0, EventName = "RemoteDialogSubmitted", Level = LogLevel.Debug, Message = "Remote dialog stage {Stage}; authentication {AuthenticationMode}; remembered {ConnectionRemembered}")]
    private static partial void RemoteDialogSubmittedCore(ILogger logger, string stage, string authenticationMode, bool connectionRemembered);

    /// <summary>生成已采集的身份级别事件, 不持有 Windows 身份对象.</summary>
    [LoggerMessage(EventId = 0, EventName = "WindowsNetworkContext", Level = LogLevel.Debug, Message = "Windows network identity stage {Stage}; impersonation {ImpersonationLevel}")]
    private static partial void WindowsNetworkContextCore(ILogger logger, string stage, string impersonationLevel);

    /// <summary>生成固定出站登录准备事件.</summary>
    [LoggerMessage(EventId = 0, EventName = "WindowsNetworkPreparing", Level = LogLevel.Debug, Message = "Windows network identity stage {Stage}; logon {LogonType}")]
    private static partial void WindowsNetworkPreparingCore(ILogger logger, string stage, string logonType);

    /// <summary>生成连接完成事件, 不接受实际连接对象.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelConnected", Level = LogLevel.Debug, Message = "Model stage {Stage}; endpoint {EndpointKind}")]
    private static partial void ModelConnectedCore(ILogger logger, string stage, string endpointKind);

    /// <summary>生成零匹配或多匹配事件.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelLookup", Level = LogLevel.Debug, Message = "Model stage {Stage}; match {MatchStatus}")]
    private static partial void ModelLookupCore(ILogger logger, string stage, string matchStatus);

    /// <summary>生成模型定位方式事件, 实际对象身份不进入日志.</summary>
    [LoggerMessage(EventId = 0, EventName = "ModelResolved", Level = LogLevel.Debug, Message = "Model stage {Stage}; match {MatchKind}")]
    private static partial void ModelResolvedCore(ILogger logger, string stage, string matchKind);

    /// <summary>生成固定 SSAS 探针时限事件.</summary>
    [LoggerMessage(EventId = 0, EventName = "SsasProbeStarted", Level = LogLevel.Debug, Message = "SSAS stage {Stage}; timeout {TimeoutMs}")]
    private static partial void SsasProbeStartedCore(ILogger logger, string stage, int timeoutMs);

    /// <summary>生成不带异常对象的连接失败安全摘要.</summary>
    [LoggerMessage(EventId = 0, EventName = "ConnectionFailure", Level = LogLevel.Debug, Message = "Model connection failure at {Stage}; depth {Depth}; type {ErrorType}; HRESULT {HResult}; socket {SocketError}; native {NativeErrorCode}")]
    private static partial void ConnectionFailureCore(ILogger logger, string stage, int depth, string? errorType, string hResult, string? socketError, int? nativeErrorCode);

    /// <summary>生成平台提取的 XMLA 数字码, 不接受 SDK 错误对象.</summary>
    [LoggerMessage(EventId = 0, EventName = "XmlaFailure", Level = LogLevel.Debug, Message = "Model XMLA failure at {Stage}; codes {XmlaErrorCodes}")]
    private static partial void XmlaFailureCore(ILogger logger, string stage, string xmlaErrorCodes);
}
