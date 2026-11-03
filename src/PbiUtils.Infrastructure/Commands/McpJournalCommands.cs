// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\McpJournalCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅供人工 CLI 检视与核销 MCP 未知执行, 保留原始事实并登记外部证据摘要.

using PbiUtils.Infrastructure.Storage;
using System.Security.Cryptography;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Commands;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>不注册为 MCP 工具, 第三方工具参数不能解除待核验阻塞.</summary>
public static class McpJournalCommands
{
    public const string HelpText = """
        pbi-utils mcp executions [--json]
        pbi-utils mcp resolve --execution <ID> --expected-record-digest <digest> --outcome verified-complete|verified-not-applied --evidence <人工核验文件> --confirm [--json]
        resolve 仅在人工核对真实模型/文件后使用. 它登记证据 SHA256 并解除 MCP 阻塞, 不重放操作, 不代表已计算、已保存或自动恢复模型.
        """;

    /// <summary>读取真实账本或登记人工结论; 核销必须完整绑定已审阅修订和稳定证据文件.</summary>
    /// <param name="args">仅接受 executions 或 resolve 及其明确参数.</param>
    /// <param name="token">在证据读取和最终核销前生效的取消令牌.</param>
    /// <returns>实际账本视图; 不返回或保存证据文件正文.</returns>
    /// <exception cref="AppException">参数缺失、证据无效、修订冲突或账本不允许核销.</exception>
    internal static async Task<object> RunAsync(string[] args, string userRoot, CancellationToken token)
    {
        if (args.Length < 2 || args[1] is not ("executions" or "resolve")) throw CommandInput.Invalid();
        var journal = new ExecutionJournal(userRoot);
        if (args[1] == "executions")
        {
            CommandInput.Options(args, 2, [], []);
            token.ThrowIfCancellationRequested();
            return new { executions = journal.List() };
        }
        var options = CommandInput.Options(args, 2, ["--execution", "--expected-record-digest", "--outcome", "--evidence"], ["--confirm"]);
        if (!options.ContainsKey("--confirm")) throw new AppException("CONFIRMATION_REQUIRED", "人工核销前必须核对真实结果并提供 --confirm", 2);
        var execution = CommandInput.Required(options, "--execution");
        var expectedDigest = CommandInput.Required(options, "--expected-record-digest");
        var outcome = CommandInput.Required(options, "--outcome");
        if (execution.Length != 32 || !execution.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || expectedDigest.Length != 64 || !expectedDigest.All(char.IsAsciiHexDigit)
            || outcome is not ("verified-complete" or "verified-not-applied")) throw CommandInput.Invalid();
        var evidence = CommandInput.Required(options, "--evidence");
        var evidenceSha256 = await HashEvidenceAsync(evidence, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return journal.Resolve(execution, expectedDigest, outcome, evidenceSha256);
    }

    /// <summary>用拒绝并发写入/删除的固定句柄读取完整证据, 限制 16 MiB 并复查安全路径.</summary>
    private static async Task<string> HashEvidenceAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048) throw new AppException("INVALID_EVIDENCE", "需要有效的人工核验证据文件", 2);
        UserDataStore.Check(path);
        if (!File.Exists(path)) throw new AppException("INVALID_EVIDENCE", "人工核验证据文件不存在", 2);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 1 or > 16 * 1024 * 1024) throw new AppException("INVALID_EVIDENCE", "人工核验证据文件必须非空且不超过 16 MiB", 2);
            UserDataStore.Check(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        }
        catch (IOException) { throw new AppException("INVALID_EVIDENCE", "无法稳定读取人工核验证据文件, 请关闭正在写入它的程序后重试", 3); }
        catch (UnauthorizedAccessException) { throw new AppException("INVALID_EVIDENCE", "没有读取人工核验证据文件的权限", 3); }
    }
}
