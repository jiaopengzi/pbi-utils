// FilePath    : pbi-utils\src\PbiUtils.Application\Logging\BusinessLog.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一业务操作进出、耗时、关联及安全异常日志, 不记录输入输出正文.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PbiUtils.Core;

namespace PbiUtils.Application.Logging;

/// <summary>仅供业务边界调用的结构化日志, 通用工具函数不埋点.</summary>
public static partial class BusinessLog
{
    /// <summary>记录同步业务及其唯一终态, 保持原异常语义.</summary>
    public static T Run<T>(ILogger logger, string operation, Func<T> action)
    {
        using var flight = Begin(logger, operation);
        try { var result = action(); flight.Complete(); return result; }
        catch (Exception error) { flight.Fail(error); throw; }
    }

    /// <summary>记录无返回值的同步业务.</summary>
    public static void Run(ILogger logger, string operation, Action action) => Run(logger, operation, () => { action(); return true; });

    /// <summary>异步业务范围跨 await 传播, 不改变结果或取消.</summary>
    public static async Task<T> RunAsync<T>(ILogger logger, string operation, Func<Task<T>> action)
    {
        using var flight = Begin(logger, operation);
        try { var result = await action(); flight.Complete(); return result; }
        catch (Exception error) { flight.Fail(error); throw; }
    }

    /// <summary>记录无返回值的异步业务.</summary>
    public static Task RunAsync(ILogger logger, string operation, Func<Task> action) => RunAsync(logger, operation, async () => { await action(); return true; });

    /// <summary>宿主协议的终态需在取消竞争裁决后手工完成.</summary>
    public static BusinessOperation Begin(ILogger logger, string operation, bool root = false) => new(logger, operation, root);
}

/// <summary>一个业务的成对事件; 根请求和嵌套操作具有独立关联身份.</summary>
public sealed class BusinessOperation : IDisposable
{
    private sealed record Context(string RequestId, string OperationId);
    private static readonly AsyncLocal<Context?> Current = new();
    private readonly Context? parent;
    private readonly ILogger logger;
    private readonly string operation;
    private readonly IDisposable? scope;
    private readonly long started = Stopwatch.GetTimestamp();
    private bool ended;
    private bool disposed;

    internal BusinessOperation(ILogger logger, string operation, bool root = false)
    {
        this.logger = logger;
        this.operation = operation;
        parent = Current.Value;
        var context = new Context(root ? Guid.NewGuid().ToString("N") : parent?.RequestId ?? Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Current.Value = context;
        scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = context.RequestId,
            ["OperationId"] = context.OperationId,
            ["ParentOperationId"] = root ? null : parent?.OperationId
        });
        BusinessLog.Started(logger, operation);
    }

    /// <summary>成功仅代表本次调用完成, 不代表真实外部能力验收.</summary>
    public void Complete() => End("Completed", LogLevel.Information);

    /// <summary>保留最多六层类型和 HRESULT, 不序列化异常对象、Message 或 Data.</summary>
    public void Fail(Exception error, bool fatal = false)
    {
        var code = error is AppException app ? SafeCode(app.Code) : error is OperationCanceledException ? "CANCELLED" : "EXECUTION_FAILED";
        End(code == "CANCELLED" ? "Cancelled" : "Failed", fatal ? LogLevel.Critical : code == "CANCELLED" ? LogLevel.Information : error is AppException ? LogLevel.Warning : LogLevel.Error, code, error);
    }

    /// <summary>协议边界最终取消或超时在裁决后记录.</summary>
    public void Reject(string code) => End(code == "CANCELLED" ? "Cancelled" : "Failed", code == "CANCELLED" ? LogLevel.Information : LogLevel.Warning, SafeCode(code));

    /// <summary>先裁决唯一终态; 等级关闭时跳过耗时、异常链和栈计算.</summary>
    private void End(string outcome, LogLevel level, string? code = null, Exception? error = null)
    {
        if (ended) return;
        ended = true;
        BusinessLog.Ended(logger, operation, outcome, level, started, code, error);
    }

    private static string SafeCode(string code) => code.Length is > 0 and <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "EXECUTION_FAILED";

    /// <summary>未裁决的宿主关闭记录取消, 释放范围后恢复父操作.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!ended) Reject("CANCELLED");
        scope?.Dispose();
        Current.Value = parent;
    }
}
