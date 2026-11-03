// FilePath    : pbi-utils\src\PbiUtils.Cli\Program.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 组合共享服务并处理产品命令, JSON 输出, 取消与退出码.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Udf;
using PbiUtils.Cli.Mcp;
using PbiUtils.Infrastructure.Commands;

namespace PbiUtils.Cli;

/// <summary>控制台组合根与协议边界.</summary>
public static class Program
{
    /// <summary>统一 JSON 使用 camelCase 与 UTF-8.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>解析产品命令, 传播取消并映射稳定退出码.</summary>
    /// <param name="args">独立参数数组.</param>
    /// <returns>产品退出码.</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        /// <summary>取消本次命令并阻止进程立即终止.</summary>
        /// <param name="sender">事件源.</param>
        /// <param name="e">控制台事件.</param>
        void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
        Console.CancelKeyPress += Cancel;
        // stdio 服务参数无效时也只能向 stderr 诊断, 不能将普通 CLI JSON 写入协议流.
        var json = args.Contains("--json", StringComparer.Ordinal) && !(args.FirstOrDefault() == "mcp" && args.ElementAtOrDefault(1) == "serve");
        var command = args.FirstOrDefault() ?? "unknown";
        LoggingRuntime? logging = null;
        BusinessOperation? operation = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new AppException("PLATFORM_UNSUPPORTED", "pbi-utils 仅支持 Windows", 3);
            if (args.Contains("--help"))
            {
                Console.WriteLine("pbi-utils --help | --version\npbi-utils doctor [--json]\npbi-utils inspect --project <路径> [--json]\npbi-utils measure list|model inspect|udf list|report inspect --project <路径> [--json]\npbi-utils mcp serve [--allow-write]\npbi-utils format dax|m --text <代码> [--json]\npbi-utils sample generate --output <示例目录> --request <参数.json> [--overwrite] [--json]\npbi-utils sample append --output <示例目录> --request <参数.json> [--json]\npbi-utils sample inspect --output <示例目录> [--json]\npbi-utils sample clear --output <示例目录> --confirm [--json]\npbi-utils change plan --request <请求.json> [--json]\npbi-utils change apply --request <请求.json> --approve <已预览digest> [--json]\n--help > --version > --json; 元命令不执行业务. change 只生成新目录副本.");
                Console.WriteLine(ScaffoldCommands.HelpText);
                Console.WriteLine(McpJournalCommands.HelpText);
                Console.WriteLine("pbi-utils updates check [--source github|gitee] [--json]");
                operation?.Complete();
                return 0;
            }
            // 函数包查询的版本是业务输入, 其余命令仍保留既有产品版本元命令优先级.
            var packageVersion = args.Length >= 3 && args[0] == "udf" && args[1] == "catalog" && args[2] == "show";
            if (args.Contains("--version") && !packageVersion) { Console.WriteLine(Product.Version); return 0; }
            logging = new LoggingRuntime(RuntimeEnvironment.UserDataDirectory(), "cli");
            logging.EnableDiagnostics();
            var logs = logging.Factory;
            operation = BusinessLog.Begin(logs.CreateLogger("Cli.Commands"), "Cli.Execute", root: true);
            if (command == "mcp")
            {
                if (args.ElementAtOrDefault(1) is "executions" or "resolve")
                {
                    command = "mcp " + args[1];
                    var data = await CliHostCommand.RunAsync(args, cancellation.Token);
                    Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                    operation.Complete();
                    return 0;
                }
                var exitCode = await McpServerCommand.RunAsync(args, cancellation.Token);
                operation.Complete();
                return exitCode;
            }
            if (command == "new" || command == "udf" && args.ElementAtOrDefault(1) is "catalog" or "local")
            {
                command = string.Join(' ', args.Take(args[0] == "new" ? 2 : 3));
                var data = await CliHostCommand.RunAsync(args, cancellation.Token);
                Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                operation.Complete();
                return 0;
            }
            if (command is "sample" or "updates")
            {
                command += " " + (args.ElementAtOrDefault(1) ?? "unknown");
                var data = await CliHostCommand.RunAsync(args, cancellation.Token);
                Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                operation.Complete();
                return 0;
            }
            if (command == "format")
            {
                command = "format " + (args.ElementAtOrDefault(1) ?? "unknown");
                var data = FormatCommands.Run(args, cancellation.Token);
                Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                operation.Complete();
                return 0;
            }
            if (command == "change")
            {
                command += " " + (args.ElementAtOrDefault(1) ?? "unknown");
                var data = await CliHostCommand.RunAsync(args, cancellation.Token);
                Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                operation.Complete();
                return 0;
            }
            if (command is "inspect" or "measure" or "model" or "udf" or "report")
            {
                var readOffset = command == "inspect" ? 1 : 2;
                if (readOffset == 2 && (args.Length < 2 || args[1] != (command is "measure" or "udf" ? "list" : "inspect")))
                    throw new AppException("INVALID_ARGUMENT", "未知工程读取命令");
                command = string.Join(' ', args.Take(readOffset));
                var values = new Dictionary<string, string>();
                var jsonSeen = false;
                for (var i = readOffset; i < args.Length; i++)
                {
                    if (args[i] == "--json" && !jsonSeen) { jsonSeen = true; continue; }
                    if (args[i] != "--project" || values.ContainsKey("project") || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new AppException("INVALID_ARGUMENT", "仅接受唯一的 --project <路径> 和 --json");
                    values.Add("project", args[i]);
                }
                if (!values.TryGetValue("project", out var path)) throw new AppException("INVALID_ARGUMENT", "需要 --project <路径>");
                var snapshot = await new ProjectService(new ProjectReader(), logs.CreateLogger<ProjectService>()).InspectAsync(path, cancellation.Token);
                object data = command switch
                {
                    "measure list" => new { snapshot.ProjectId, snapshot.BaselineId, snapshot.Model.Measures, snapshot.Warnings },
                    "udf list" => new { snapshot.ProjectId, snapshot.BaselineId, snapshot.Model.CompatibilityLevel, snapshot.Model.Functions, snapshot.Warnings },
                    "model inspect" => new { snapshot.ProjectId, snapshot.BaselineId, snapshot.Model, snapshot.Warnings },
                    "report inspect" => new { snapshot.ProjectId, snapshot.BaselineId, snapshot.ReportFormat, snapshot.Pages, snapshot.Warnings },
                    _ => snapshot
                };
                Console.WriteLine(json ? JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data }, Json) : JsonSerializer.Serialize(data, Json));
                operation?.Complete();
                return 0;
            }
            if (command != "doctor")
                throw new AppException("INVALID_ARGUMENT", "未知命令, 请使用 --help");
            HashSet<string> seen = [];
            for (var i = 1; i < args.Length; i++)
            {
                var option = args[i];
                if (!seen.Add(option)) throw new AppException("INVALID_ARGUMENT", "选项不能重复");
                if (option == "--json") continue;
                throw new AppException("INVALID_ARGUMENT", "未知或不适用的选项");
            }
            var result = new DoctorService(new RuntimeEnvironment(), logs.CreateLogger<DoctorService>()).Execute(cancellation.Token);
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, command, data = result }, Json));
            else Console.WriteLine(JsonSerializer.Serialize(result, Json));
            operation?.Complete();
            return 0;
        }
        catch (Exception ex)
        {
            operation?.Fail(ex);
            var (code, message, exitCode) = ex switch
            {
                AppException app => (app.Code, app.Message, app.ExitCode),
                OperationCanceledException => ("CANCELLED", "操作已取消", 130),
                _ => ("EXECUTION_FAILED", "执行失败, 请重试或检查运行环境", 4)
            };
            message = UdfCleanupNotice.Decorate(ex, message);
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, ok = false, command, error = new { code, message } }, Json));
            else Console.Error.WriteLine($"{code}: {message}");
            return exitCode;
        }
        finally { Console.CancelKeyPress -= Cancel; operation?.Dispose(); logging?.Dispose(); }
    }
}
