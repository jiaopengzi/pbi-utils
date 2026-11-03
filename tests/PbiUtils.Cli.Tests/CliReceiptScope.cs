// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\CliReceiptScope.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 精确识别本轮CLI测试新增的确定终态回执, 不清理其他客户端或未知提交记录.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Execution;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Commands;
using PbiUtils.Infrastructure.Mcp;

namespace PbiUtils.Cli.Tests;

/// <summary>只在真实测试CLI退出后清理精确请求摘要与原始记录摘要均匹配的本轮记录.</summary>
internal sealed class CliReceiptScope : IDisposable
{
    private readonly string userRoot = RuntimeEnvironment.UserDataDirectory();
    private readonly HashSet<string>? initial;
    private readonly string? digest;

    /// <summary>冻结测试自己的命令和原cwd, 复用产品现有回执摘要格式进行清理归属判断.</summary>
    internal CliReceiptScope(string[] arguments, string workingDirectory)
    {
        if (CliBusinessCommands.Method(arguments) is not { } method) return;
        var args = arguments.ToArray();
        for (var index = 1; index + 1 < args.Length; index++)
            if (args[index] is "--request" or "--output" or "--project" or "--evidence")
            {
                if (string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal)) return;
                args[index + 1] = Path.GetFullPath(args[index + 1], workingDirectory);
                index++;
            }
        var parameters = JsonSerializer.Serialize(new { args, workingDirectory });
        digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + parameters)));
        initial = new ExecutionJournal(userRoot).List().Select(item => item.Record.ExecutionId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>保持未知与原有记录, 清理前持锁再次确认完整字节未被其他进程修订.</summary>
    public void Dispose()
    {
        if (initial is null) return;
        var candidates = new ExecutionJournal(userRoot).List().Where(item => !initial.Contains(item.Record.ExecutionId)
            && item.Record.RequestDigest == digest && item.Record.State is "completed" or "not_committed" or "failed").ToArray();
        if (candidates.Length == 0) return;
        var directory = Path.Combine(userRoot, "mcp", "executions");
        using var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        foreach (var item in candidates)
        {
            var path = Path.Combine(directory, item.Record.ExecutionId + ".json");
            UserDataStore.Check(path);
            if (!File.Exists(path)) continue;
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == item.RecordDigest) File.Delete(path);
        }
    }
}
