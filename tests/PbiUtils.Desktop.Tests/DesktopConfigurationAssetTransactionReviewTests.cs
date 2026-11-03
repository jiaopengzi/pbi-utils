// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopConfigurationAssetTransactionReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以隔离文件系统验证配置资产类型漂移、计划冻结和准备失败资源释放.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopConfigurationAssetTransactionReviewTests
{
    /// <summary>缺失目标被同名目录占用时在持久准备前拒绝, 不能将目录视为不存在.</summary>
    [Fact]
    public void ExistingDirectoryCannotSatisfyMissingAssetBaseline()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target);

        var error = Assert.Throws<AppException>(() => { using var transaction = fixture.Prepare(); });

        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Empty(Directory.EnumerateDirectories(fixture.BackupRoot, "assets-*"));
        Assert.True(Directory.Exists(fixture.Target));
    }

    /// <summary>资产祖先由目录变成普通文件时明确拒绝, 不能按缺失计划准备不可达目标.</summary>
    [Fact]
    public void FileAncestorCannotSatisfyMissingAssetBaseline()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.AssetRoot);
        File.WriteAllText(Path.Combine(fixture.AssetRoot, "pbi-utils"), "external");

        var error = Assert.Throws<AppException>(() => { using var transaction = fixture.Prepare(); });

        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Empty(Directory.EnumerateDirectories(fixture.BackupRoot, "assets-*"));
    }

    /// <summary>调用方随后修改集合不改变已持久化的冻结副作用清单.</summary>
    [Fact]
    public void PreparedTransactionOwnsFrozenAssetList()
    {
        using var fixture = new Fixture();
        var plan = new List<ModelConfigurationAsset> { fixture.Asset };
        using var transaction = fixture.Prepare(plan);
        plan.Clear();

        transaction.Apply(default);

        Assert.Equal("new", File.ReadAllText(fixture.Target));
        transaction.Rollback();
        Assert.False(File.Exists(fixture.Target));
    }

    /// <summary>第二条输入错误时第一条原字节备份属于失败准备, 不应遗留无回执的临时备份目录.</summary>
    [Fact]
    public void FailedPreparationReleasesOwnedBackupDirectoryAndLocks()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        ModelConfigurationAsset[] plan = [fixture.Asset with { BeforeHash = Hash("old") },
            new("data/config/second.csv", fixture.Source, Hash("missing"), Hash("new"), 3)];

        Assert.Throws<AppException>(() => { using var transaction = fixture.Prepare(plan); });

        Assert.Empty(Directory.EnumerateDirectories(fixture.BackupRoot, "assets-*"));
        using var next = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        next.Rollback();
        Assert.Equal("old", File.ReadAllText(fixture.Target));
    }

    /// <summary>内容相同的不同文件仍是确认后替换, 不能将新身份纳入本轮既有资产.</summary>
    [Fact]
    public void SameBytesReplacementAfterPreparationIsRejected()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        using var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        var external = Path.Combine(fixture.AssetRoot, "external.json");
        File.WriteAllText(external, "old");
        File.Move(external, fixture.Target, true);

        var error = Assert.Throws<AppException>(() => transaction.Apply(default));

        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal("old", File.ReadAllText(fixture.Target));
        transaction.Rollback();
    }

    /// <summary>回滚只拥有本轮实际写出的文件身份, 外部同字节新文件不能被删除或覆盖.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackPreservesSameBytesExternalReplacement(bool recoverPersistedPrepared)
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        try
        {
            transaction.Apply(default);
            var external = Path.Combine(fixture.AssetRoot, "external.json");
            File.WriteAllText(external, "new");
            File.Move(external, fixture.Target, true);
            if (recoverPersistedPrepared) transaction.Dispose();

            var error = Assert.Throws<AppException>(() =>
            {
                if (recoverPersistedPrepared) DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot);
                else transaction.Rollback();
            });

            Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
            Assert.Equal("new", File.ReadAllText(fixture.Target));
            Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        }
        finally { transaction.Dispose(); }
    }

    /// <summary>第二条文件被外部读句柄保护导致发布失败时, 已发布第一条可安全恢复, 保护句柄不被结束.</summary>
    [Fact]
    public void PartialApplyFailureRestoresOnlyEarlierOwnedOutputs()
    {
        using var fixture = new Fixture();
        var csv = Path.Combine(fixture.AssetRoot, "data", "config", "second.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        File.WriteAllText(csv, "old");
        using var transaction = fixture.Prepare([fixture.Asset, new("data/config/second.csv", fixture.Source, Hash("old"), Hash("new"), 3)]);
        using var external = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<IOException>(() => transaction.Apply(default));
        Assert.Equal("new", File.ReadAllText(fixture.Target));
        transaction.Rollback();

        Assert.False(File.Exists(fixture.Target));
        Assert.Equal("old", File.ReadAllText(csv));
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>跨引擎共享的未决回执被同名目录替换时, 普通提交仍须核验, 不能按无记录放行.</summary>
    [Fact]
    public async Task DirectoryCannotHideAnotherEnginesPendingReceipt()
    {
        using var fixture = new Fixture();
        using (var transaction = fixture.Prepare())
        {
            transaction.Apply(default);
            transaction.MarkCommitStarted();
        }
        var shared = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BackupRoot)!, "*.assets.json"));
        File.Delete(shared);
        Directory.CreateDirectory(shared);
        var otherEngine = Path.Combine(Path.GetDirectoryName(fixture.BackupRoot)!, "other");

        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(otherEngine));
        var error = await Assert.ThrowsAsync<AppException>(async () =>
        {
            using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(otherEngine, "before",
                _ => Task.FromResult<string?>(fixture.AssetRoot), default);
        });

        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal("new", File.ReadAllText(fixture.Target));
    }

    /// <summary>prepared 的输出身份及原字节已可靠持久时, 重新读取可回滚本轮输出且不重复应用.</summary>
    [Fact]
    public void PersistedPreparedTransactionCanRestoreItsOwnedOutput()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        using (var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }])) transaction.Apply(default);

        DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot);

        Assert.Equal("old", File.ReadAllText(fixture.Target));
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>commit_started 第二份持久回执失败会阻止返回提交边界, 原输出仍可在提交前回滚.</summary>
    [Fact]
    public void CommitStartedReceiptFailureDoesNotPreventSafePrecommitRollback()
    {
        using var fixture = new Fixture();
        using var transaction = fixture.Prepare();
        transaction.Apply(default);
        var modelReceipt = Path.Combine(fixture.BackupRoot, "configuration-assets.json");
        using (var external = new FileStream(modelReceipt, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<UnauthorizedAccessException>(transaction.MarkCommitStarted);

        transaction.Rollback();

        Assert.False(File.Exists(fixture.Target));
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>用户在开始文件副作用前取消时不新增文件, prepared 可可靠完成为已回滚.</summary>
    [Fact]
    public void CancellationBeforeApplyLeavesAssetsUntouchedAndReleasesPendingState()
    {
        using var fixture = new Fixture();
        using var transaction = fixture.Prepare();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => transaction.Apply(cancellation.Token));
        transaction.Rollback();

        Assert.False(File.Exists(fixture.Target));
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>missing 祖先在准备后被目录联接占用时拒绝跟随链接, 外部目录完全保留.</summary>
    [Fact]
    public void JunctionAddedAfterPreparationCannotRedirectAssetWrite()
    {
        using var fixture = new Fixture();
        using var transaction = fixture.Prepare();
        var link = Path.GetDirectoryName(fixture.Target)!;
        var outside = Path.Combine(Path.GetDirectoryName(fixture.AssetRoot)!, "outside");
        Directory.CreateDirectory(fixture.AssetRoot);
        Directory.CreateDirectory(outside);
        var externalFile = Path.Combine(outside, "config.json");
        File.WriteAllText(externalFile, "external");
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var error = Assert.Throws<AppException>(() => transaction.Apply(default));
            Assert.Equal("UNSAFE_USER_DATA_PATH", error.Code);
            Assert.Equal("external", File.ReadAllText(externalFile));
            transaction.Rollback();
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>提交与完成回执已确定后保留槽被锁只报告清理责任, 不把模型重新标为未知或重复应用.</summary>
    [Fact]
    public void CompletedTransactionCleanupFailureKeepsKnownCommitAndCanRetryCleanupOnly()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        using var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        transaction.Apply(default);
        transaction.MarkCommitStarted();
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.BackupRoot, "configuration-assets.json")));
        var files = new DesktopConfigurationAssetFile(fixture.Target, receipt.RootElement.GetProperty("Entries")[0].GetProperty("Slot").GetString()!);
        using (var external = new FileStream(files.HeldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var cleanup = transaction.Complete();
            Assert.NotNull(cleanup);
            Assert.Equal(true, cleanup.Data["DesktopConfigurationAssetCleanupPending"]);
            Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
            transaction.Rollback();
            Assert.Equal("new", File.ReadAllText(fixture.Target));
            Assert.Equal("old", File.ReadAllText(files.HeldPath));
        }
        transaction.Dispose();

        Assert.Null(DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", fixture.AssetRoot));

        Assert.False(File.Exists(files.HeldPath));
        Assert.Equal("new", File.ReadAllText(fixture.Target));
    }

    /// <summary>真实文件步骤后模拟进程中断, 只按持久责任恢复; 未记录输出身份的槽保守待核验.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void PreparedCrashStateRestoresOnlyProvenFiles(int phase)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        using (var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }])) { }
        var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.BackupRoot, "configuration-assets.json")))!;
        var entry = receipt["Entries"]![0]!;
        var files = new DesktopConfigurationAssetFile(fixture.Target, entry["Slot"]!.GetValue<string>());
        DesktopConfigurationAssetState? output = null;
        DesktopConfigurationAssetState? held = null;
        if (phase >= 1) output = files.Stage(Encoding.UTF8.GetBytes("new"));
        if (phase >= 2) entry["AfterIdentity"] = output!.Identity;
        if (phase >= 3) held = files.Retain();
        if (phase >= 4) entry["Held"] = JsonSerializer.SerializeToNode(held);
        if (phase >= 5) files.Publish(output!);
        if (phase >= 6) entry["Retiring"] = true;
        if (phase >= 7) files.RetireOutput();
        if (phase >= 8) files.RestoreHeld(held!);
        var serialized = receipt.ToJsonString();
        File.WriteAllText(Path.Combine(fixture.BackupRoot, "configuration-assets.json"), serialized);
        File.WriteAllText(Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BackupRoot)!, "*.assets.json")), serialized);

        if (phase == 1)
        {
            var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot));
            Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
            Assert.True(File.Exists(files.StagePath));
            Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        }
        else
        {
            Assert.Null(DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot));
            Assert.False(File.Exists(files.StagePath));
            Assert.False(File.Exists(files.HeldPath));
            Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        }
        Assert.Equal("old", File.ReadAllText(fixture.Target));
    }

    /// <summary>旧schema的completed和未知commit_started均不能冒认本轮事实或被静默丢弃.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviousReceiptSchemaCannotBeTreatedAsCurrentPlan(bool completed)
    {
        using var fixture = new Fixture();
        using (var transaction = fixture.Prepare())
        {
            transaction.Apply(default);
            transaction.MarkCommitStarted();
            if (completed) Assert.Null(transaction.Complete());
        }
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BackupRoot)!, "*.assets.json")
            .Append(Path.Combine(fixture.BackupRoot, "configuration-assets.json")))
        {
            var receipt = JsonNode.Parse(File.ReadAllBytes(path))!;
            receipt["SchemaVersion"] = 1; File.WriteAllText(path, receipt.ToJsonString());
        }

        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", fixture.AssetRoot));

        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal("new", File.ReadAllText(fixture.Target));
    }

    /// <summary>真实最终提交Guard必须消费已完成清理责任, 不能只在手动调用VerifyPending时可达.</summary>
    [Fact]
    public async Task ProductionGuardRetriesCompletedCleanupWithoutPendingModel()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        transaction.Apply(default); transaction.MarkCommitStarted();
        var files = ReadReceiptFiles(fixture);
        using (var external = new FileStream(files.HeldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(transaction.Complete());
        transaction.Dispose();
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));

        using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.BackupRoot, "after",
            _ => Task.FromResult<string?>(fixture.AssetRoot), default);

        Assert.False(File.Exists(files.HeldPath));
        Assert.Equal("new", File.ReadAllText(fixture.Target));
        Assert.False(DesktopConfigurationAssetTransaction.HasCompletedCleanupCandidates(fixture.BackupRoot));
        guard.Dispose();
        using var noCleanupGuard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.BackupRoot, "after",
            _ => throw new InvalidOperationException("Completed cleanup must not require another file capability probe"), default);
    }

    /// <summary>旧完成轮清理被锁且latest已被下一轮覆盖后, 实际Guard仍能枚举并消费旧责任.</summary>
    [Fact]
    public async Task CompletedCleanupRemainsReachableAfterNextRoundOverwritesLatest()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        var first = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        first.Apply(default); first.MarkCommitStarted();
        var oldFiles = ReadReceiptFiles(fixture);
        using (var external = new FileStream(oldFiles.HeldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(first.Complete()); first.Dispose();
            using var next = new DesktopConfigurationAssetTransaction(fixture.AssetRoot, fixture.BackupRoot, "after", "after-next",
                [fixture.Asset with { BeforeHash = Hash("new") }]);
            next.Apply(default); next.MarkCommitStarted(); Assert.Null(next.Complete());
            Assert.Equal("old", File.ReadAllText(oldFiles.HeldPath));
        }

        using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.BackupRoot, "after-next",
            _ => Task.FromResult<string?>(fixture.AssetRoot), default);

        Assert.False(File.Exists(oldFiles.HeldPath));
        Assert.Equal("new", File.ReadAllText(fixture.Target));
    }

    /// <summary>另一工程的completed清理不提供当前未保存模型的unknown事实, 缺文件绑定也允许其普通提交.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherRootsCompletedCleanupDoesNotBlockUnsavedModelGuard(bool bindingAvailable)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "old");
        var transaction = fixture.Prepare([fixture.Asset with { BeforeHash = Hash("old") }]);
        transaction.Apply(default); transaction.MarkCommitStarted();
        var files = ReadReceiptFiles(fixture);
        using var external = new FileStream(files.HeldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.NotNull(transaction.Complete()); transaction.Dispose();
        var other = Path.Combine(Path.GetDirectoryName(fixture.BackupRoot)!, "unsaved-engine");
        var otherRoot = fixture.AssetRoot + "-other";
        Directory.CreateDirectory(otherRoot);

        using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(other, "different-model",
            _ => Task.FromResult<string?>(bindingAvailable ? otherRoot : null), default);

        Assert.Equal("old", File.ReadAllText(files.HeldPath));
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(other));
        Assert.Null(guard.CleanupFailure);
    }

    /// <summary>从产品真实持久回执取得固定槽, 不改写产品状态或注入暂停钩子.</summary>
    private static DesktopConfigurationAssetFile ReadReceiptFiles(Fixture fixture)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.BackupRoot, "configuration-assets.json")));
        return new(fixture.Target, receipt.RootElement.GetProperty("Entries")[0].GetProperty("Slot").GetString()!);
    }

    /// <summary>测试哈希使用精确 UTF-8 原字节.</summary>
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>本例仅拥有随机临时根, 不连接真实 Desktop 或访问用户模型.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-assets-review-" + Guid.NewGuid().ToString("N"));
        public string AssetRoot => Path.Combine(root, "project");
        public string BackupRoot => Path.Combine(root, "transactions", "engine");
        public string Target => Path.Combine(AssetRoot, "pbi-utils", "config.json");
        public string Source => Path.Combine(root, "source.json");
        public ModelConfigurationAsset Asset => new("pbi-utils/config.json", Source, null, Hash("new"), 3);
        /// <summary>准备仅供本例使用的冻结源.</summary>
        public Fixture() { Directory.CreateDirectory(root); File.WriteAllText(Source, "new"); }
        /// <summary>建立受测事务; 失败时测试要求所有本轮准备资源释放.</summary>
        public DesktopConfigurationAssetTransaction Prepare(IReadOnlyList<ModelConfigurationAsset>? assets = null) =>
            new(AssetRoot, BackupRoot, "before", "after", assets ?? [Asset]);
        /// <summary>清理本例随机临时根; 测试创建的句柄必须已释放.</summary>
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
