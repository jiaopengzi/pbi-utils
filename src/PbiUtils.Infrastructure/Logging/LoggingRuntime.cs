// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Logging\LoggingRuntime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 组合 Serilog、动态级别与有界滚动 JSON 文件, 不输出到产品 stdout.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Logging;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace PbiUtils.Infrastructure.Logging;

/// <summary>由宿主独占并在全部业务结束后释放的日志运行时.</summary>
public sealed class LoggingRuntime : ILoggingSettings, IDisposable
{
    private readonly Lock sync = new();
    private readonly string settingsPath;
    private readonly string lockPath;
    private readonly LoggingLevelSwitch levelSwitch;
    private readonly Logger logger;
    private string? warning;
    private bool diagnostics;
    private readonly FileStream sessionLease;
    public string DirectoryPath { get; }
    public ILoggerFactory Factory { get; }

    /// <summary>同一宿主类型共享滚动文件, 多进程通过 File sink 的 shared 协议写入.</summary>
    public LoggingRuntime(string root, string host)
    {
        if (host is not ("desktop" or "cli" or "host" or "test")) throw new ArgumentOutOfRangeException(nameof(host));
        sessionLease = UserDataSessionLease.Acquire(root, exclusive: false);
        try
        {
            UserDataStore.Check(root);
            Directory.CreateDirectory(root);
            DirectoryPath = Path.Combine(root, "logs");
            UserDataStore.Check(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            settingsPath = Path.Combine(root, "logging-settings.json");
            lockPath = Path.Combine(root, "logging-settings.lock");
            var level = ReadLevel();
            levelSwitch = new(Enum.Parse<LogEventLevel>(level));
            // 明确检查可写性; Serilog 自身的诊断原文可能带路径, 不输出原文.
            var probe = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
            try { using (File.Create(probe)) { } }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            var logPath = Path.Combine(DirectoryPath, host + "-.clef");
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, host + "-*.clef")) UserDataStore.Check(file);
            logger = new LoggerConfiguration().MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext().Enrich.WithProperty("Application", "pbi-utils").Enrich.WithProperty("Host", host)
                .Enrich.WithProperty("Version", Product.Version).Enrich.WithProperty("ProcessId", Environment.ProcessId)
                .WriteTo.File(new CompactJsonFormatter(), logPath, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 31,
                    retainedFileTimeLimit: TimeSpan.FromDays(14), shared: true, buffered: false)
                .CreateLogger();
            Factory = new SerilogLoggerFactory(logger, dispose: false);
        }
        catch { sessionLease.Dispose(); throw; }
    }

    /// <summary>仅宿主启用全局 SelfLog, 从不保存其可能含敏感信息的原文.</summary>
    public void EnableDiagnostics()
    {
        diagnostics = true;
        Serilog.Debugging.SelfLog.Enable(_ => Volatile.Write(ref warning, "日志写入发生异常，请检查目录权限和磁盘空间。"));
    }

    public LoggingView Read() { lock (sync) return new(levelSwitch.MinimumLevel.ToString(), DirectoryPath, warning); }

    public LoggingView Save(string level, CancellationToken token, Action? committing = null)
    {
        if (!LoggingService.Levels.Contains(level, StringComparer.Ordinal)) throw new AppException("INVALID_LOG_LEVEL", "请选择有效的日志级别");
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var fileLock = UserDataStore.Lock(lockPath);
            token.ThrowIfCancellationRequested();
            committing?.Invoke();
            UserDataStore.Write(settingsPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, level }));
            levelSwitch.MinimumLevel = Enum.Parse<LogEventLevel>(level);
            return Read();
        }
    }

    private string ReadLevel()
    {
        UserDataStore.Check(settingsPath);
        if (!File.Exists(settingsPath)) return "Information";
        try
        {
            if (new FileInfo(settingsPath).Length > 1024) throw new JsonException();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
                || !value.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 1
                || !value.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.String
                || !LoggingService.Levels.Contains(level.GetString()!, StringComparer.Ordinal)) throw new JsonException();
            return level.GetString()!;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warning = "日志配置无法读取，当前使用 Information；原文件已保留。";
            return "Information";
        }
    }

    /// <summary>Factory 不拥有 sink, 最后显式关闭并刷盘.</summary>
    public void Dispose()
    {
        try { Factory.Dispose(); logger.Dispose(); if (diagnostics) Serilog.Debugging.SelfLog.Disable(); }
        finally { sessionLease.Dispose(); }
    }
}
