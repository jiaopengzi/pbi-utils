// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Execution\ExecutionJournal.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为全客户端业务副作用保存唯一准备、提交与确定结果, 不保存聊天、参数正文或凭据.

using PbiUtils.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Execution;

/// <summary>本机业务回执只存身份、参数摘要和业务阶段; 它不代替模型或文件的权威回读.</summary>
public sealed record ExecutionRecord(int SchemaVersion, string ExecutionId, string Method, string RequestDigest,
    string WorkspaceId, string State, bool CommitStarted, string? ErrorCode, DateTimeOffset RecordedAt,
    string? ReviewOutcome = null, string? EvidenceSha256 = null, DateTimeOffset? ReviewedAt = null);

/// <summary>人工核验列表附带当前原始记录摘要, 后续核销不能覆盖并发修订.</summary>
public sealed record ExecutionView(ExecutionRecord Record, string RecordDigest);

/// <summary>同一用户数据根的副作用串行, 中断提交阻止新工具写入与数据重置.</summary>
public sealed class ExecutionJournal
{
    private const int MaximumBytes = 16 * 1024;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4
    };

    /// <summary>绑定宿主统一用户根; 不创建目录或读取现有凭据.</summary>
    /// <param name="userRoot">宿主指定的本机用户数据根, 不接受工具参数.</param>
    public ExecutionJournal(string userRoot)
    {
        directory = Path.Combine(Path.GetFullPath(userRoot), "mcp", "executions");
        UserDataStore.Check(directory);
    }

    /// <summary>可靠写入 prepared 后返回唯一执行租约; 未决账本或并发调用均在副作用前失败.</summary>
    /// <param name="executionId">宿主生成的32位小写十六进制身份.</param>
    /// <param name="method">已由宿主目录核验的固定业务方法.</param>
    /// <param name="parameters">已冻结参数, 只将其摘要落盘.</param>
    /// <param name="workspaceId">宿主当前模型身份, 无连接时为固定 application.</param>
    /// <returns>拥有跨进程独占锁的执行上下文; 调用者必须排空并释放.</returns>
    /// <exception cref="AppException">参数非法、并发、账本损坏或未知提交时拒绝准备.</exception>
    public Execution Prepare(string executionId, string method, JsonElement parameters, string workspaceId)
    {
        if (!ValidId(executionId) || !AllowedMethod(method)
            || string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 1024)
            throw new AppException("INVALID_RECEIPT", "MCP 执行身份无效", 3);
        UserDataStore.Check(directory);
        Directory.CreateDirectory(directory);
        UserDataStore.Check(directory);
        var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        try
        {
            RequireSafeCore();
            var file = Path.Combine(directory, executionId + ".json");
            if (File.Exists(file)) throw new AppException("RUN_ALREADY_EXECUTED", "此 MCP 运行已记录, 不得重放", 3);
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + parameters.GetRawText())));
            var record = new ExecutionRecord(1, executionId, method, digest, workspaceId, "prepared", false, null, DateTimeOffset.UtcNow);
            Write(file, record);
            return new(file, record, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>在重置或新工具写入前检查全部当前账本; 枚举、读取与格式失败不能降级为空.</summary>
    /// <exception cref="AppException">活跃调用、损坏回执或未知提交阻止清理.</exception>
    public void RequireResetSafe()
    {
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) return;
        using var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        RequireSafeCore();
    }

    /// <summary>只读列出完整当前账本与精确修订摘要; 活跃调用不允许旁路查看不完整状态.</summary>
    /// <returns>按记录时间排列的当前执行事实, 不包含模型/聊天/参数正文.</returns>
    /// <exception cref="AppException">并发、损坏或不安全路径时明确失败.</exception>
    public IReadOnlyList<ExecutionView> List()
    {
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) return [];
        using var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Select(ReadView)
            .OrderBy(value => value.Record.RecordedAt).ToArray();
    }

    /// <summary>仅供人工 CLI 记录明确核验结论; 保留原始未知状态, 不代替权威模型恢复或重放业务.</summary>
    /// <param name="executionId">人工列表中的精确宿主执行身份.</param>
    /// <param name="expectedRecordDigest">人工审阅的完整记录摘要, 并发变化时拒绝覆盖.</param>
    /// <param name="outcome">verified-complete 或 verified-not-applied, 明确为人工结论.</param>
    /// <param name="evidenceSha256">CLI 对人工核验证据文件完整内容计算的 SHA256, 不接收文件正文.</param>
    /// <returns>原始未知事实和新增人工核验信息, 不将其改写为自动成功.</returns>
    /// <exception cref="AppException">活跃调用、修订冲突、重复核销或非法状态时拒绝写入.</exception>
    public ExecutionView Resolve(string executionId, string expectedRecordDigest, string outcome, string evidenceSha256)
    {
        if (!ValidId(executionId) || !ValidHash(expectedRecordDigest) || !ValidHash(evidenceSha256)
            || outcome is not ("verified-complete" or "verified-not-applied")) throw Invalid();
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) throw Invalid();
        using var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        var file = Path.Combine(directory, executionId + ".json");
        var current = ReadView(file);
        if (!string.Equals(current.RecordDigest, expectedRecordDigest, StringComparison.OrdinalIgnoreCase))
            throw new AppException("RECEIPT_CONFLICT", "MCP 回执已变化, 请重新读取后人工核验", 3);
        if (current.Record.State is not ("commit_started" or "needs_verification") || current.Record.ReviewOutcome is not null)
            throw new AppException("RECEIPT_REVIEW_NOT_ALLOWED", "此执行不需要核销或已记录人工结论", 3);
        var next = current.Record with { ReviewOutcome = outcome, EvidenceSha256 = evidenceSha256.ToLowerInvariant(), ReviewedAt = DateTimeOffset.UtcNow };
        Write(file, next);
        return ReadView(file);
    }

    /// <summary>持有全局租约时逐文件核实状态; 不按当前模型过滤未知执行.</summary>
    private void RequireSafeCore()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            UserDataStore.Check(file);
            if (new FileInfo(file).Length > MaximumBytes) throw Invalid();
            var bytes = File.ReadAllBytes(file);
            ValidateResetReceipt(bytes, Path.GetFileNameWithoutExtension(file));
        }
    }

    /// <summary>重置树可从已固定的文件句柄传入内容, 防止校验后换路径.</summary>
    /// <param name="bytes">同一已固定回执文件的完整有界内容.</param>
    /// <param name="executionId">文件名绑定的宿主执行身份.</param>
    /// <exception cref="AppException">损坏、跨身份或未决提交时拒绝重置.</exception>
    internal static void ValidateResetReceipt(byte[] bytes, string executionId)
    {
        try
        {
            if (bytes.Length > MaximumBytes) throw Invalid();
            var record = Parse(bytes, executionId);
            if (record.State is "commit_started" or "needs_verification" && record.ReviewOutcome is null)
                throw new AppException("EXECUTION_NEEDS_VERIFICATION", "已有 MCP 执行结果待核验, 不得重放、新建写入或重置用户数据", 3);
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>读取单份有界账本和原始字节摘要, 不推定未知提交已经完成.</summary>
    private static ExecutionView ReadView(string file)
    {
        UserDataStore.Check(file);
        if (!File.Exists(file) || new FileInfo(file).Length > MaximumBytes) throw Invalid();
        var bytes = File.ReadAllBytes(file);
        return new(Parse(bytes, Path.GetFileNameWithoutExtension(file)), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>严格校验当前格式和人工核验元数据, 未知或部分字段不降级为可写状态.</summary>
    private static ExecutionRecord Parse(byte[] bytes, string executionId)
    {
        try
        {
            if (bytes.Length > MaximumBytes) throw Invalid();
            var record = JsonSerializer.Deserialize<ExecutionRecord>(bytes, Json) ?? throw Invalid();
            if (record.SchemaVersion != 1 || !ValidId(record.ExecutionId) || record.ExecutionId != executionId
                || !AllowedMethod(record.Method) || string.IsNullOrWhiteSpace(record.WorkspaceId)
                || record.WorkspaceId.Length > 1024 || !ValidHash(record.RequestDigest)
                || record.State is not ("prepared" or "commit_started" or "completed" or "failed" or "not_committed" or "needs_verification")
                || record.State is "prepared" or "not_committed" && record.CommitStarted
                || record.State == "failed" && string.IsNullOrWhiteSpace(record.ErrorCode)
                || record.State == "commit_started" && !record.CommitStarted) throw Invalid();
            if (record.ReviewOutcome is null ? record.EvidenceSha256 is not null || record.ReviewedAt is not null
                : record.ReviewOutcome is not ("verified-complete" or "verified-not-applied") || !ValidHash(record.EvidenceSha256)
                    || record.ReviewedAt is null || record.State is not ("commit_started" or "needs_verification")) throw Invalid();
            return record;
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>全客户端沿用同一本机账本, 仅固定业务方法或文件用例名称可以记录.</summary>
    private static bool AllowedMethod(string method) => BusinessOperationCatalog.IsRecordedMethod(method);

    /// <summary>摘要必须完整, 不接受空值或未经哈希的证据文本.</summary>
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>只接受安全文件名身份, 拒绝路径片段或大小写别名.</summary>
    private static bool ValidId(string value) => value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>同步写穿并原子替换; 返回前完整回执已经持久化, 失败不放行提交.</summary>
    private static void Write(string file, ExecutionRecord record)
    {
        try { UserDataStore.Write(file, JsonSerializer.SerializeToUtf8Bytes(record, Json)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("RECEIPT_SAVE_FAILED", "MCP 执行回执保存失败, 请核验实际业务状态", 3); }
    }

    /// <summary>固定格式错误不包含文件路径或账本正文.</summary>
    private static AppException Invalid() => new("INVALID_RECEIPT", "MCP 执行回执无效, 原文件保留", 3);

    /// <summary>单次执行持有原子账本锁, 提交开始只能前进; 关闭不会猜测未决结果.</summary>
    public sealed class Execution : IDisposable
    {
        private readonly string file;
        private readonly FileStream lease;
        private ExecutionRecord record;
        private bool finished;

        internal Execution(string file, ExecutionRecord record, FileStream lease)
        { this.file = file; this.record = record; this.lease = lease; }

        /// <summary>真实提交边界是否已经可靠记录; 不代表模型或文件已提交成功.</summary>
        public bool HasCommitted => record.CommitStarted;

        /// <summary>在业务副作用前同步可靠记录提交开始; 同次调用重复通知幂等.</summary>
        public void CommitStarted()
        {
            if (finished) throw new AppException("RUN_ALREADY_EXECUTED", "MCP 执行已终结", 3);
            if (record.CommitStarted) return;
            var next = record with { State = "commit_started", CommitStarted = true, RecordedAt = DateTimeOffset.UtcNow };
            Write(file, next);
            record = next;
        }

        /// <summary>业务结果先可靠落盘再交给第三方; 不确定保存或提交后异常保持待核验.</summary>
        /// <param name="succeeded">实际共享业务已返回确定成功.</param>
        /// <param name="errorCode">仅稳定错误码, 不接受异常正文.</param>
        public void Finish(bool succeeded, string? errorCode = null)
        {
            if (finished) return;
            var next = record with
            {
                State = succeeded ? "completed" : record.CommitStarted ? "needs_verification" : "not_committed",
                ErrorCode = errorCode,
                RecordedAt = DateTimeOffset.UtcNow
            };
            Write(file, next);
            record = next;
            finished = true;
        }

        /// <summary>业务已确定其副作用和失败阶段时记录失败, 不抹去提交事实或误报需要核验.</summary>
        /// <param name="errorCode">经过业务分类核实的稳定失败码, 不接受未知提交或异常正文.</param>
        /// <exception cref="AppException">空错误码或回执保存失败时拒绝终结.</exception>
        public void FinishKnownFailure(string errorCode)
        {
            if (finished) return;
            if (string.IsNullOrWhiteSpace(errorCode)) throw Invalid();
            var next = record with { State = "failed", ErrorCode = errorCode, RecordedAt = DateTimeOffset.UtcNow };
            Write(file, next);
            record = next;
            finished = true;
        }

        /// <summary>业务返回既有未知提交或未完成回读时强制待核验, 本次未再次提交也不能降为未提交.</summary>
        /// <param name="errorCode">共享业务提供的稳定待核验原因.</param>
        /// <exception cref="AppException">空错误码或回执保存失败时保持原持久状态.</exception>
        public void FinishNeedsVerification(string errorCode)
        {
            if (finished) return;
            if (string.IsNullOrWhiteSpace(errorCode)) throw Invalid();
            var next = record with { State = "needs_verification", ErrorCode = errorCode, RecordedAt = DateTimeOffset.UtcNow };
            Write(file, next);
            record = next;
            finished = true;
        }

        /// <summary>仅释放本执行的跨进程租约; 中断 commit_started 原样保留供核验.</summary>
        public void Dispose() => lease.Dispose();
    }
}
