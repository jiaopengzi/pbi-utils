// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\CliBusinessCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 后台执行固定CLI业务目录, 统一写入回执并保留进度与路径语义.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;
using PbiUtils.Infrastructure.Updates;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>无控制台和平台依赖的CLI业务边界; 只由已认证CLI上下文调用.</summary>
public static class CliBusinessCommands
{
    /// <summary>识别当前真实持久副作用, 未知命令仍由严格分发拒绝.</summary>
    public static bool IsWrite(string[] args) => Method(args) is not null;

    /// <summary>使用与共享MCP用例相同的固定回执名称, 不以外部字符串扩展白名单.</summary>
    public static string? Method(string[] args) => (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1), args.ElementAtOrDefault(2)) switch
    {
        ("new", "create", _) => "scaffold_create",
        ("change", "apply", _) => "change_apply",
        ("sample", "generate", _) => "sample_generate",
        ("sample", "append", _) => "sample_append",
        ("sample", "clear", _) => "sample_clear",
        ("udf", "catalog", "refresh") => "udf_catalog_refresh",
        ("udf", "local", "create") => "udf_local_create",
        ("udf", "local", "update") => "udf_local_update",
        ("udf", "local", "delete") => "udf_local_delete",
        _ => null
    };

    /// <summary>按原调用目录解析显式路径, 真实提交先持久化回执; 不改变后台进程工作目录.</summary>
    /// <param name="args">固定命令及独立参数, 不是shell文本.</param>
    /// <param name="userRoot">宿主选定的统一用户根.</param>
    /// <param name="logs">宿主日志工厂.</param>
    /// <param name="token">提交前取消令牌; 提交后由共享用例排空.</param>
    /// <param name="committing">宿主可选提交通知, 在可靠回执之后调用.</param>
    /// <param name="progress">仅本客户端阶段事件.</param>
    /// <param name="workingDirectory">CLI启动时的绝对目录, 不授予额外能力.</param>
    /// <returns>真实业务结果; 未知提交保持账本阻塞.</returns>
    public static async Task<object> RunAsync(string[] args, string userRoot, ILoggerFactory logs, CancellationToken token,
        Action? committing = null, Action<JsonElement>? progress = null, string? workingDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        workingDirectory ??= Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Length > 2048) throw CommandInput.Invalid();
        args = NormalizePaths(args, workingDirectory);
        using var execution = Method(args) is { } method
            ? new ExecutionJournal(userRoot).Prepare(Guid.NewGuid().ToString("N"), method,
                JsonSerializer.SerializeToElement(new { args, workingDirectory }), "local-files") : null;
        void Commit() { execution?.CommitStarted(); committing?.Invoke(); }
        try
        {
            var result = args.FirstOrDefault() switch
            {
                "new" => await ScaffoldCommands.RunAsync(args, userRoot, logs, token, Commit, progress),
                "sample" => await SampleCommands.RunAsync(args, logs, token, Commit, progress),
                "udf" => await UdfCommands.RunAsync(args, userRoot, token, Commit),
                "change" => await ChangeCommands.RunAsync(args, logs, Commit, workingDirectory, token),
                "updates" => await ProductUpdateCommands.RunAsync(args, ProductUpdateFactory.Create(userRoot), token),
                // 人工恢复自身必须能读取/核销未决记录, 其精确修订与独占锁由journal统一拥有.
                "mcp" => await McpJournalCommands.RunAsync(args, userRoot, token),
                _ => throw CommandInput.Invalid()
            };
            BusinessOutcome.From(result).Finish(execution);
            return result;
        }
        catch (Exception error)
        {
            try { execution?.Finish(false, error is AppException app ? app.Code : error is OperationCanceledException ? "CANCELLED" : "EXECUTION_FAILED"); }
            catch (AppException receiptError) { throw new AppException(receiptError.Code, "执行回执保存失败, 请核验实际结果; 不得重放", 3); }
            throw;
        }
    }

    /// <summary>只解析现有命令明确声明的文件/目录选项, 原参数数组不变.</summary>
    private static string[] NormalizePaths(string[] args, string workingDirectory)
    {
        var result = args.ToArray();
        for (var i = 1; i + 1 < result.Length; i++)
            if (result[i] is "--request" or "--output" or "--project" or "--evidence")
            {
                if (string.IsNullOrWhiteSpace(result[i + 1]) || result[i + 1].StartsWith("--", StringComparison.Ordinal)) throw CommandInput.Invalid();
                result[i + 1] = Path.GetFullPath(result[i + 1], workingDirectory);
                i++;
            }
        return result;
    }
}
