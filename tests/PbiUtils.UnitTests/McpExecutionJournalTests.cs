// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpExecutionJournalTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以隔离文件验证 MCP 提交账本、未知阻塞、人工核销修订与重置保护.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Execution;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class McpExecutionJournalTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "pbi-mcp-receipts-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temporary, ".pbi-utils");
    private static readonly JsonElement Parameters = JsonSerializer.SerializeToElement(new { level = "private-input-value" });

    /// <summary>持久阶段先于外部副作用, 完成后只保存摘要并拒绝相同运行重放.</summary>
    [Fact]
    public void CompletedExecutionIsDurableAndNeverStoresParameters()
    {
        var journal = new ExecutionJournal(Root);
        var id = Guid.NewGuid().ToString("N");
        using (var execution = journal.Prepare(id, "logging.save", Parameters, "workspace"))
        {
            var path = Path.Combine(Root, "mcp", "executions", id + ".json");
            Assert.Contains("prepared", File.ReadAllText(path));
            Assert.DoesNotContain("private-input-value", File.ReadAllText(path));
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => journal.List()).Code);
            execution.CommitStarted();
            execution.CommitStarted();
            Assert.Contains("commit_started", File.ReadAllText(path));
            execution.Finish(true);
        }
        var record = Assert.Single(new ExecutionJournal(Root).List());
        Assert.Equal("completed", record.Record.State);
        Assert.True(record.Record.CommitStarted);
        journal.RequireResetSafe();
        Assert.Equal("RUN_ALREADY_EXECUTED", Assert.Throws<AppException>(() => journal.Prepare(id, "logging.save", Parameters, "workspace")).Code);
    }

    /// <summary>中断提交跨实例阻止新副作用和重置, 首个删除前保留其他用户数据.</summary>
    [Fact]
    public async Task UnknownCommitBlocksFutureWritesAndResetAcrossInstances()
    {
        var journal = new ExecutionJournal(Root);
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "logging.save", Parameters, "workspace")) execution.CommitStarted();
        var preserved = Path.Combine(Root, "preserved.txt");
        await File.WriteAllTextAsync(preserved, "untouched");
        var restarted = new ExecutionJournal(Root);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", Assert.Throws<AppException>(() => restarted.Prepare(Guid.NewGuid().ToString("N"), "logging.save", Parameters, "other-workspace")).Code);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
        Assert.Equal("untouched", await File.ReadAllTextAsync(preserved));
    }

    /// <summary>人工核验必须匹配审阅修订和证据摘要, 保留原始未知状态且只解除阻塞.</summary>
    [Fact]
    public void ManualReviewKeepsOriginalUnknownFactsAndRejectsStaleReview()
    {
        var journal = new ExecutionJournal(Root);
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "logging.save", Parameters, "workspace"))
        { execution.CommitStarted(); execution.Finish(false, "CONTROLLED_FAILURE"); }
        var current = Assert.Single(journal.List());
        Assert.Equal("RECEIPT_CONFLICT", Assert.Throws<AppException>(() => journal.Resolve(current.Record.ExecutionId, new string('a', 64), "verified-complete", new string('b', 64))).Code);
        var reviewed = journal.Resolve(current.Record.ExecutionId, current.RecordDigest, "verified-complete", new string('b', 64));
        Assert.Equal("needs_verification", reviewed.Record.State);
        Assert.Equal("CONTROLLED_FAILURE", reviewed.Record.ErrorCode);
        Assert.Equal("verified-complete", reviewed.Record.ReviewOutcome);
        Assert.NotEqual(current.RecordDigest, reviewed.RecordDigest);
        journal.RequireResetSafe();
        Assert.Equal("RECEIPT_REVIEW_NOT_ALLOWED", Assert.Throws<AppException>(() => journal.Resolve(current.Record.ExecutionId, reviewed.RecordDigest, "verified-not-applied", new string('c', 64))).Code);
        using var next = journal.Prepare(Guid.NewGuid().ToString("N"), "logging.save", Parameters, "workspace");
        next.Finish(false, "CANCELLED");
    }

    /// <summary>取消准备未触发提交不形成未知事实; 损坏记录不得以空列表掩盖.</summary>
    [Fact]
    public void PreparedCancellationIsSafeButCorruptionBlocksTheWholeJournal()
    {
        var journal = new ExecutionJournal(Root);
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "logging.save", Parameters, "workspace")) execution.Finish(false, "CANCELLED");
        journal.RequireResetSafe();
        var path = Path.Combine(Root, "mcp", "executions", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "not-json");
        Assert.Equal("INVALID_RECEIPT", Assert.Throws<AppException>(() => journal.List()).Code);
        Assert.Equal("INVALID_RECEIPT", Assert.Throws<AppException>(() => journal.RequireResetSafe()).Code);
        Assert.Equal("not-json", File.ReadAllText(path));
    }

    /// <summary>业务回读既有未知结果时本次没有提交回调, 仍须跨实例阻止写入和首次重置删除.</summary>
    [Fact]
    public async Task RecoveredPendingResultWithoutNewCommitStillBlocksReset()
    {
        var journal = new ExecutionJournal(Root);
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "scaffold_create", Parameters, "workspace"))
            execution.FinishNeedsVerification("GIT_INIT_NEEDS_VERIFICATION");
        var record = Assert.Single(journal.List()).Record;
        Assert.Equal("needs_verification", record.State);
        Assert.False(record.CommitStarted);
        Assert.Equal("GIT_INIT_NEEDS_VERIFICATION", record.ErrorCode);
        var preserved = Path.Combine(Root, "preserved.txt");
        await File.WriteAllTextAsync(preserved, "untouched");
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
        Assert.Equal("untouched", await File.ReadAllTextAsync(preserved));
    }

    /// <summary>确定失败保留提交事实及稳定原因, 无需将已知 Git 故障误当未知提交或人工核销.</summary>
    [Fact]
    public void KnownFailurePreservesCommitFactsWithoutBlockingOrAllowingManualReview()
    {
        var journal = new ExecutionJournal(Root);
        using (var execution = journal.Prepare(Guid.NewGuid().ToString("N"), "scaffold_create", Parameters, "workspace"))
        { execution.CommitStarted(); execution.FinishKnownFailure("GIT_INIT_FAILED"); }
        var current = Assert.Single(journal.List());
        Assert.Equal("failed", current.Record.State);
        Assert.True(current.Record.CommitStarted);
        Assert.Equal("GIT_INIT_FAILED", current.Record.ErrorCode);
        journal.RequireResetSafe();
        Assert.Equal("RECEIPT_REVIEW_NOT_ALLOWED", Assert.Throws<AppException>(() => journal.Resolve(current.Record.ExecutionId,
            current.RecordDigest, "verified-complete", new string('a', 64))).Code);
        using var next = journal.Prepare(Guid.NewGuid().ToString("N"), "scaffold_create", Parameters, "workspace");
        next.Finish(false, "CANCELLED");
    }

    /// <summary>只释放本测试创建的随机隔离目录, 不操作实际应用用户数据.</summary>
    public void Dispose() { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
}
