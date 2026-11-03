// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopModelCommitTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原 TOM 对象的连接模型提交、权威回读与不可重放边界行为测试.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopModelCommitTests
{
    private static readonly string[] SuccessfulEvents = ["verify", "lock", "refresh", "verify", "verify", "committing", "submit", "readback"];

    /// <summary>实际编辑原连接对象时不能要求其仍等于提交前基线, 独立权威复核后提交同一对象且不替换模型.</summary>
    [Fact]
    public async Task OriginalObjectsCanBeValidatedAndSubmittedWithoutReplacement()
    {
        using var fixture = new CommitFixture();
        var model = fixture.Editing.Model;
        var table = model.Tables["Dates"];
        var column = table.Columns["Date"];
        table.Description = "direct original object";

        var saved = await fixture.SaveAsync();

        Assert.Same(model, fixture.Editing.Model);
        Assert.Same(table, fixture.Editing.Model.Tables["Dates"]);
        Assert.Same(column, table.Columns["Date"]);
        Assert.Equal("direct original object", saved.Model.Tables.Single().Description);
        Assert.Equal(1, fixture.Submissions);
    }

    /// <summary>模型含 SDK 推导列类型时表说明仍直接提交, 提交和完整回读继续执行一次.</summary>
    /// <param name="description">批准的新说明, 空字符串表示清空.</param>
    [Theory]
    [InlineData("updated description")]
    [InlineData("")]
    public async Task TableDescriptionCommitsWithInferredColumnTypes(string description)
    {
        using var fixture = new CommitFixture(inferredType: true);
        fixture.Editing.Model.Tables["Dates"].Description = description;

        var result = await fixture.SaveAsync();

        Assert.Equal(description, fixture.Live.Model.Tables["Dates"].Description);
        Assert.True(fixture.Live.Model.Tables["Dates"].Columns["Date"].IsDataTypeInferred);
        Assert.Equal(TOM.DataType.DateTime, fixture.Live.Model.Tables["Dates"].Columns["Date"].DataType);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
        Assert.Equal(SuccessfulEvents, fixture.Events);
        Assert.Equal(new ModelSaveResult(true, false, true, DesktopModelCommit.SavedMessage), result.SaveResult);
    }

    /// <summary>独立批准的 UDF 升级将数据库级别和完整函数原对象暂存一起提交, 不需要持久化历史定义.</summary>
    [Fact]
    public async Task ApprovedUdfUpgradeCommitsDatabaseLevelAndExactFunctionTogether()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true };
        fixture.Editing.CompatibilityLevel = 1702;
        fixture.Editing.Model.Functions.Add(new TOM.Function { Name = "Test.Answer", Expression = "() => 42" });
        var expected = ModelConfigurationReadback.Fingerprint(fixture.Editing);
        var result = await fixture.SaveAsync();
        Assert.Equal(1702, fixture.Live.CompatibilityLevel);
        Assert.Equal(expected, ModelConfigurationReadback.Fingerprint(fixture.Live));
        Assert.Equal("() => 42", fixture.Live.Model.Functions["Test.Answer"].Expression);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
        Assert.Equal(1, fixture.TransactionsStarted);
        Assert.Equal(1, fixture.TransactionsCommitted);
        Assert.Equal(0, fixture.Rollbacks);
        Assert.Equal(["begin", "verify", "lock", "refresh", "verify", "verify", "committing", "submit", "commit", "readback"], fixture.Events);
        Assert.Equal(1702, result.Model.CompatibilityLevel);
        Assert.Equal(new ModelSaveResult(true, false, true, DesktopModelCommit.SavedMessage), result.SaveResult);
    }

    /// <summary>没有本次批准的原对象暂存升档不得修改在线客户端对象或进入提交.</summary>
    [Fact]
    public async Task UnapprovedCompatibilityUpgradeIsRejectedBeforeSubmission()
    {
        using var fixture = new CommitFixture(1606);
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", error.Code);
        Assert.Equal(1606, fixture.Live.CompatibilityLevel);
        Assert.Equal(0, fixture.Submissions);
    }

    /// <summary>UDF 的独立批准只允许低级别提升到 1702, 不能降级或改到任意其他级别.</summary>
    /// <param name="original">权威原模型兼容级别.</param>
    /// <param name="target">私有原对象暂存请求的兼容级别.</param>
    [Theory]
    [InlineData(1606, 1701)]
    [InlineData(1606, 1703)]
    [InlineData(1702, 1606)]
    public async Task ApprovalDoesNotAuthorizeOtherCompatibilityChanges(int original, int target)
    {
        using var fixture = new CommitFixture(original) { AllowCompatibilityUpgrade = true };
        fixture.Editing.CompatibilityLevel = target;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", error.Code);
        Assert.Equal(original, fixture.Live.CompatibilityLevel);
        Assert.Equal(0, fixture.Submissions);
    }

    /// <summary>升档事务建立后目标变化或取消都回滚本次事务, 不进入数据库提交.</summary>
    /// <param name="cancel">是否在最终核验后取消, 否则模拟目标身份变化.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeFinalCheckFailureRollsBackBeforeSubmit(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CommitFixture(1606)
        {
            AllowCompatibilityUpgrade = true,
            FailFinalIdentity = !cancel,
            CancelOnFinalIdentity = cancel ? cancellation : null
        };
        fixture.Editing.CompatibilityLevel = 1702;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.SaveAsync(cancellation.Token));
        else Assert.Equal("POWER_BI_INSTANCE_CLOSED", (await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync())).Code);
        Assert.Equal(1, fixture.TransactionsStarted);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(1606, fixture.Live.CompatibilityLevel);
    }

    /// <summary>事务建立前另一客户端提交的完整定义变化必须在事务内再次核验, 拒绝覆盖并保留外部变化.</summary>
    [Fact]
    public async Task ConcurrentChangeAtUpgradeTransactionStartIsRejected()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, ChangeOnBegin = true };
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
        Assert.Equal(1606, fixture.Live.CompatibilityLevel);
        Assert.Contains("#date(2027", ((TOM.MPartitionSource)fixture.Live.Model.Tables["Dates"].Partitions[0].Source).Expression);
    }

    /// <summary>数据库 Update 或事务 Commit 失败都只尝试一次提交和一次回滚, 不自动重放未知写入.</summary>
    /// <param name="failCommit">是否在事务 Commit 阶段失败, 否则在 Update 阶段失败.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeSubmitOrCommitFailureRollsBackWithoutReplay(bool failCommit)
    {
        using var fixture = new CommitFixture(1606)
        {
            AllowCompatibilityUpgrade = true,
            FailSubmit = !failCommit,
            FailCommit = failCommit
        };
        fixture.Editing.CompatibilityLevel = 1702;
        fixture.Editing.Model.Functions.Add(new TOM.Function { Name = "Test.Answer", Expression = "() => 42" });
        if (failCommit) Assert.Equal("DESKTOP_SAVE_FAILED", (await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync())).Code);
        else await Assert.ThrowsAsync<IOException>(() => fixture.SaveAsync());
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(0, fixture.Readbacks);
        Assert.Equal(0, fixture.TransactionsCommitted);
        Assert.Equal(1606, fixture.Live.CompatibilityLevel);
        Assert.Empty(fixture.Live.Model.Functions);
        Assert.Contains("committing", fixture.Events);
    }

    /// <summary>回滚也失败时保留原提交异常和安全回滚事实, 不用回滚错误掩盖原始未知提交.</summary>
    [Fact]
    public async Task UpgradeRollbackFailurePreservesOriginalSubmitError()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, FailSubmit = true, FailRollback = true };
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.SaveAsync());
        Assert.Equal("unknown", error.Message);
        Assert.Equal(true, error.Data["DesktopTransactionRollbackFailed"]);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Contains("committing", fixture.Events);
    }

    /// <summary>Commit 结果未知并且回滚也失败时, 仍返回稳定提交错误并保留回滚失败事实, 不暴露 SDK 原文或重放.</summary>
    [Fact]
    public async Task UpgradeCommitAndRollbackFailurePreservesStableCommitError()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, FailCommit = true, FailRollback = true };
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("DESKTOP_SAVE_FAILED", error.Code);
        Assert.DoesNotContain("commit unknown", error.Message);
        Assert.Equal(true, error.Data["DesktopTransactionRollbackFailed"]);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(0, fixture.Readbacks);
        Assert.Contains("committing", fixture.Events);
    }

    /// <summary>持久回执或提交边界回调失败时仍回滚已建立的升级事务, 不执行任何数据库 Update.</summary>
    [Fact]
    public async Task UpgradeCommittingCallbackFailureRollsBackBeforeUpdate()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, FailCommitting = true };
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.SaveAsync());
        Assert.Equal("receipt failed", error.Message);
        Assert.Equal(1, fixture.TransactionsStarted);
        Assert.Equal(1, fixture.Rollbacks);
        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(1606, fixture.Live.CompatibilityLevel);
    }

    /// <summary>升级提交后原请求取消仍完成事务和独立回读, 不把已提交事实报告成取消.</summary>
    [Fact]
    public async Task UpgradeCancellationAfterSubmitStillCommitsAndReadsBack()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, CancelOnSubmit = cancellation };
        fixture.Editing.CompatibilityLevel = 1702;
        var result = await fixture.SaveAsync(cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.TransactionsCommitted);
        Assert.Equal(1, fixture.Readbacks);
        Assert.Equal(0, fixture.Rollbacks);
        Assert.True(result.SaveResult!.ModelSaved);
    }

    /// <summary>事务已提交后的权威定义不一致保持待核验, 不回滚已完成事务或再次 Update.</summary>
    [Fact]
    public async Task UpgradeReadbackMismatchRequiresVerificationWithoutRollback()
    {
        using var fixture = new CommitFixture(1606) { AllowCompatibilityUpgrade = true, StaleReadback = true };
        fixture.Editing.CompatibilityLevel = 1702;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.TransactionsCommitted);
        Assert.Equal(0, fixture.Rollbacks);
    }

    /// <summary>日期 M 和列直接进入原连接模型, 不依赖任何 Desktop 文件或文件保存入口.</summary>
    [Fact]
    public async Task CalendarDefinitionCommitsExactApprovedEditingToConnectedModel()
    {
        using var fixture = new CommitFixture();
        var originalTable = fixture.Editing.Model.Tables["Dates"];
        var originalPartition = originalTable.Partitions[0];
        fixture.Editing.Model.Tables["Dates"].Columns["Date"].Name = "01_Date";
        ((TOM.MPartitionSource)fixture.Editing.Model.Tables["Dates"].Partitions[0].Source).Expression = "#table({\"Date\"}, {{#date(2026, 2, 1)}})";
        var expected = ModelConfigurationReadback.Fingerprint(fixture.Editing);

        var result = await fixture.SaveAsync();

        Assert.Equal(expected, result.BaselineId);
        Assert.Equal(expected, ModelConfigurationReadback.Fingerprint(fixture.Live));
        Assert.Equal(expected, ModelConfigurationReadback.Fingerprint(fixture.Editing));
        Assert.Equal("original-database", fixture.Live.ID);
        Assert.Same(originalTable, fixture.Editing.Model.Tables["Dates"]);
        Assert.Same(originalPartition, fixture.Editing.Model.Tables["Dates"].Partitions[0]);
        Assert.False(originalTable.IsRemoved);
        Assert.False(originalPartition.IsRemoved);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
        Assert.Equal(SuccessfulEvents, fixture.Events);
        Assert.Equal(new ModelSaveResult(true, false, true, DesktopModelCommit.SavedMessage), result.SaveResult);
        Assert.False(result.HasPendingChanges);
    }

    /// <summary>提交锁前后出现外部完整定义变更会拒绝覆盖, 即使表列展示形状没有改变.</summary>
    [Fact]
    public async Task ConcurrentQueryChangeRejectsEditingBeforeSubmission()
    {
        using var fixture = new CommitFixture { ChangeOnRefresh = true };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
        Assert.Contains("#date(2027", ((TOM.MPartitionSource)fixture.Live.Model.Tables["Dates"].Partitions[0].Source).Expression);
    }

    /// <summary>最终目标复核失败不进入提交边界, 不因已复制客户端原对象暂存而污染实际服务器.</summary>
    [Fact]
    public async Task ChangedTargetRejectsBeforeSubmission()
    {
        using var fixture = new CommitFixture { FailFinalIdentity = true };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", error.Code);
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
    }

    /// <summary>提交前取消不会调用 SDK; 提交后的原取消不能中止独立权威回读.</summary>
    [Fact]
    public async Task CancellationBeforeCommitDoesNotSubmit()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CommitFixture { CancelOnRefresh = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.SaveAsync(cancellation.Token));
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
    }

    /// <summary>提交已经开始后排空真实回读, 不将已提交模型报告为取消或再次应用原对象暂存.</summary>
    [Fact]
    public async Task CancellationAfterCommitStillReadsAuthoritativeModel()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CommitFixture { CancelOnSubmit = cancellation };
        var result = await fixture.SaveAsync(cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
        Assert.True(result.SaveResult!.ModelSaved);
    }

    /// <summary>服务器返回成功但完整查询定义未生效时仍待核验, 不能用相同表列 DTO 宣告成功.</summary>
    [Fact]
    public async Task FullReadbackMismatchRequiresVerificationWithoutReplaying()
    {
        using var fixture = new CommitFixture { StaleReadback = true };
        ((TOM.MPartitionSource)fixture.Editing.Model.Tables["Dates"].Partitions[0].Source).Expression = "#table({\"Date\"}, {{#date(2026, 3, 1)}})";
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
        Assert.Contains("committing", fixture.Events);
    }

    /// <summary>完整定义相同但回读来自另一个模型仍拒绝成功, 不用相同名称或形状追认身份.</summary>
    [Fact]
    public async Task ReadbackFromAnotherModelRequiresVerification()
    {
        using var fixture = new CommitFixture { ReadbackFromAnotherTarget = true };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, fixture.Readbacks);
    }

    /// <summary>SDK 结果未知时保留已进入提交的事实, 协作者不自动再次 SaveChanges.</summary>
    [Fact]
    public async Task UnknownSubmitFailureIsNeverRetried()
    {
        using var fixture = new CommitFixture { FailSubmit = true };
        await Assert.ThrowsAsync<IOException>(() => fixture.SaveAsync());
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(0, fixture.Readbacks);
        Assert.Contains("committing", fixture.Events);
    }

    /// <summary>最终提交回调中的独立客户端不能先发布同字段修改再被旧原对象覆盖.</summary>
    [Fact]
    public async Task CommitLockProtectsFinalAuthorityCheckThroughSubmission()
    {
        using var fixture = new CommitFixture { ExternalWriteOnCommitting = true };
        fixture.Editing.Model.Tables["Dates"].Description = "local approved";

        var saved = await fixture.SaveAsync();

        Assert.False(fixture.ExternalWriteCommitted);
        Assert.Equal("local approved", saved.Model.Tables.Single().Description);
        Assert.Equal("local approved", fixture.Live.Model.Tables["Dates"].Description);
        Assert.Equal(1, fixture.Submissions);
    }

    /// <summary>外部定义在取得锁之前已变化, 必须由锁内最终权威复核拒绝旧原对象提交.</summary>
    [Fact]
    public async Task ChangeWhileAcquiringCommitLockIsRejectedByFinalAuthorityRead()
    {
        using var fixture = new CommitFixture { ChangeBeforeLock = true };
        fixture.Editing.Model.Tables["Dates"].Description = "local stale";

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.SaveAsync());

        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal("external before lock", fixture.Live.Model.Tables["Dates"].Description);
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
    }

    /// <summary>原事务的提交锁未确认取得时 fail closed, 不进入回执提交边界或 SDK 保存.</summary>
    [Fact]
    public async Task FailedCommitLockDoesNotEnterSubmission()
    {
        using var fixture = new CommitFixture { FailCommitLock = true };

        await Assert.ThrowsAsync<IOException>(() => fixture.SaveAsync());

        Assert.Equal("original description", fixture.Live.Model.Tables["Dates"].Description);
        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
    }

    /// <summary>同步锁等待结束后立即观察原取消, 不能因锁已取得而忽略提交前取消.</summary>
    [Fact]
    public async Task CancellationAfterCommitLockDoesNotSubmit()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CommitFixture { CancelOnCommitLock = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.SaveAsync(cancellation.Token));

        Assert.Equal(0, fixture.Submissions);
        Assert.DoesNotContain("committing", fixture.Events);
    }

    /// <summary>夹具提供真实 TOM 对象, 仅替换不可在单元测试中访问的服务器和持久化边界.</summary>
    private sealed class CommitFixture : IDisposable
    {
        public TOM.Database Live { get; private set; } = TOM.JsonSerializer.DeserializeDatabase("""{"id":"original-database","name":"original-database","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Dates","columns":[{"name":"Date","sourceColumn":"Date","dataType":"dateTime"}],"partitions":[{"name":"Dates","mode":"import","source":{"type":"m","expression":"#table({\"Date\"}, {{#date(2026, 1, 1)}})"}}]}]}}""");
        public TOM.Database Editing { get; }
        public ProjectSnapshot Baseline { get; }
        public byte[] Original { get; }
        public List<string> Events { get; } = [];
        public int Submissions { get; private set; }
        public int Readbacks { get; private set; }
        public int TransactionsStarted { get; private set; }
        public int TransactionsCommitted { get; private set; }
        public int Rollbacks { get; private set; }
        public bool ChangeOnRefresh { get; init; }
        public bool ChangeOnBegin { get; init; }
        public bool FailFinalIdentity { get; init; }
        public bool StaleReadback { get; init; }
        public bool ReadbackFromAnotherTarget { get; init; }
        public bool FailSubmit { get; init; }
        public bool FailCommit { get; init; }
        public bool FailRollback { get; init; }
        public bool FailCommitting { get; init; }
        public bool AllowCompatibilityUpgrade { get; init; }
        public bool ExternalWriteOnCommitting { get; init; }
        public bool ExternalWriteCommitted { get; private set; }
        public bool ChangeBeforeLock { get; init; }
        public bool FailCommitLock { get; init; }
        public CancellationTokenSource? CancelOnCommitLock { get; init; }
        public CancellationTokenSource? CancelOnRefresh { get; init; }
        public CancellationTokenSource? CancelOnSubmit { get; init; }
        public CancellationTokenSource? CancelOnFinalIdentity { get; init; }
        private int verifications;
        private byte[]? transactionOriginal;
        private bool commitLockHeld;

        /// <summary>只替代实际引擎锁边界; 独立原对象和权威回读仍使用真实 TOM 定义.</summary>
        public void AcquireCommitLock()
        {
            Events.Add("lock");
            if (ChangeBeforeLock) Live.Model.Tables["Dates"].Description = "external before lock";
            if (FailCommitLock) throw new IOException("commit lock failed");
            commitLockHeld = true;
            CancelOnCommitLock?.Cancel();
        }

        /// <summary>保留原始完整定义和独立原对象暂存, 不创建 PBIP 或访问真实 Desktop.</summary>
        /// <param name="compatibilityLevel">本轮原模型实际级别, 升档测试使用低级别基线.</param>
        /// <param name="inferredType">模拟在线模型已推导类型, 克隆仍由真实 SDK 执行.</param>
        public CommitFixture(int compatibilityLevel = 1702, bool inferredType = false)
        {
            Live.CompatibilityLevel = compatibilityLevel;
            Live.Model.Tables["Dates"].Description = "original description";
            Live.Model.Tables["Dates"].Columns["Date"].IsDataTypeInferred = inferredType;
            Original = System.Text.Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(Live));
            Editing = TOM.JsonSerializer.DeserializeDatabase(System.Text.Encoding.UTF8.GetString(Original));
            Baseline = new("model://isolated", "identity", ModelConfigurationReadback.Fingerprint(Live), "online", "", "", null,
                ModelSnapshotMapper.Map(Live), [], [], [], new("desktop", "Power BI Desktop"));
        }

        /// <summary>执行产品事务, 通过边界委托观察提交和真实完整模型回读.</summary>
        public async Task<ProjectSnapshot> SaveAsync(CancellationToken token = default)
        {
            // 两个 TOM 对象仅模拟服务器与一个连接的缓存, 产品代码只编辑 Editing 这个原连接对象.
            var active = AllowCompatibilityUpgrade && Editing.CompatibilityLevel != Baseline.Model.CompatibilityLevel;
            if (active)
            {
                Events.Add("begin"); TransactionsStarted++;
                if (ChangeOnBegin) ((TOM.MPartitionSource)Live.Model.Tables["Dates"].Partitions[0].Source).Expression = "#table({\"Date\"}, {{#date(2027, 1, 1)}})";
                transactionOriginal = System.Text.Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(Live));
            }
            var reads = 0;
            try
            {
                return await new DesktopModelCommit(Editing,
                    ct =>
                    {
                        Events.Add("verify"); ct.ThrowIfCancellationRequested();
                        if (++verifications == 3 && FailFinalIdentity) throw new AppException("POWER_BI_INSTANCE_CLOSED", "changed", 3);
                        if (verifications == 3) CancelOnFinalIdentity?.Cancel();
                        return Task.CompletedTask;
                    },
                    ct =>
                    {
                        if (++reads == 2)
                        {
                            Events.Add("refresh");
                            if (ChangeOnRefresh) ((TOM.MPartitionSource)Live.Model.Tables["Dates"].Partitions[0].Source).Expression = "#table({\"Date\"}, {{#date(2027, 1, 1)}})";
                            CancelOnRefresh?.Cancel();
                        }
                        ct.ThrowIfCancellationRequested();
                        return Task.FromResult(new DesktopModelAuthority(Baseline.ProjectId, ModelConfigurationReadback.Fingerprint(Live)));
                    },
                    AcquireCommitLock,
                    () =>
                    {
                        Events.Add("submit"); Submissions++; CancelOnSubmit?.Cancel();
                        if (FailSubmit) throw new IOException("unknown");
                        // 模拟外部引擎接收一次原会话的变更, 不作为产品中的模型复制通道.
                        var previous = Live;
                        Live = TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(Editing));
                        previous.Dispose();
                        if (active)
                        {
                            Events.Add("commit");
                            if (FailCommit) throw new AppException("DESKTOP_SAVE_FAILED", "模型提交结果未确认", 4);
                            TransactionsCommitted++; active = false;
                        }
                    },
                    ct =>
                    {
                        Events.Add("readback"); Readbacks++; ct.ThrowIfCancellationRequested();
                        var observed = Baseline with { Model = ModelSnapshotMapper.Map(Live), BaselineId = ModelConfigurationReadback.Fingerprint(Live) };
                        if (ReadbackFromAnotherTarget) observed = observed with { ProjectId = "another-model" };
                        return Task.FromResult(StaleReadback ? Baseline : observed);
                    }).SaveAsync(Baseline, () =>
                    {
                        Events.Add("committing");
                        if (FailCommitting) throw new IOException("receipt failed");
                        if (ExternalWriteOnCommitting && !commitLockHeld)
                        {
                            Live.Model.Tables["Dates"].Description = "external at commit boundary";
                            ExternalWriteCommitted = true;
                        }
                    }, token, AllowCompatibilityUpgrade);
            }
            catch (Exception error)
            {
                if (active)
                {
                    Events.Add("rollback"); Rollbacks++;
                    if (FailRollback) error.Data["DesktopTransactionRollbackFailed"] = true;
                    else
                    {
                        var previous = Live;
                        Live = TOM.JsonSerializer.DeserializeDatabase(System.Text.Encoding.UTF8.GetString(transactionOriginal ?? Original));
                        previous.Dispose();
                    }
                }
                throw;
            }
        }

        /// <summary>释放夹具拥有的完整模型, 不影响外部实例或文件.</summary>
        public void Dispose() { Editing.Dispose(); Live.Dispose(); }
    }
}
