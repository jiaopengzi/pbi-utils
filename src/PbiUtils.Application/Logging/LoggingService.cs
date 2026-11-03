// FilePath    : pbi-utils\src\PbiUtils.Application\Logging\LoggingService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享日志偏好用例与固定 DTO, 不接受网页指定日志路径.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;

namespace PbiUtils.Application.Logging;

/// <summary>安全日志偏好和宿主确定的输出目录.</summary>
public sealed record LoggingView(string Level, string Directory, string? Warning = null, bool IsSimulation = false);

/// <summary>日志库和用户存储由 Infrastructure 提供.</summary>
public interface ILoggingSettings
{
    LoggingView Read();
    LoggingView Save(string level, CancellationToken token, Action? committing = null);
}

/// <summary>桌面和后续入口共用的日志级别白名单.</summary>
public sealed class LoggingService(ILoggingSettings settings, ILogger<LoggingService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<LoggingService>.Instance;
    public static IReadOnlyList<string> Levels { get; } = Array.AsReadOnly(["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]);

    public LoggingView Read() => BusinessLog.Run<LoggingView>(log, "Logging.Read", settings.Read);

    /// <summary>先持久化再切换; 提交前取消不改设置, 失败保留当前级别.</summary>
    public LoggingView Save(string level, CancellationToken token, Action? committing = null) => BusinessLog.Run<LoggingView>(log, "Logging.Save", () =>
    {
        if (!Levels.Contains(level, StringComparer.Ordinal)) throw new AppException("INVALID_LOG_LEVEL", "请选择有效的日志级别");
        token.ThrowIfCancellationRequested();
        return settings.Save(level, token, committing);
    });
}
