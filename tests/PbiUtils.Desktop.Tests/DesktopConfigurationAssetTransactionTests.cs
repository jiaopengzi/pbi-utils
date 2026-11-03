// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopConfigurationAssetTransactionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证连接模型配置资产的备份、并发保护、提交前回滚和未知提交阻断.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopConfigurationAssetTransactionTests
{
    /// <summary>配置替换和删除在模型提交前失败时只恢复本轮文件, 原始字节保持一致.</summary>
    [Fact]
    public void RollbackRestoresReplacedAndDeletedAssets()
    {
        using var fixture = new AssetFixture();
        var json = fixture.Existing("pbi-utils/config.json", "old-json");
        var csv = fixture.Existing("data/config/old.csv", "old-csv");
        var source = fixture.Source("new-json");
        using var transaction = fixture.Prepare([new("pbi-utils/config.json", source, Hash("old-json"), Hash("new-json"), 8),
            new("data/config/old.csv", null!, Hash("old-csv"), null!, 0)]);

        transaction.Apply(CancellationToken.None);
        Assert.Equal("new-json", File.ReadAllText(json));
        Assert.False(File.Exists(csv));
        transaction.Rollback();

        Assert.Equal("old-json", File.ReadAllText(json));
        Assert.Equal("old-csv", File.ReadAllText(csv));
        DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot);
    }

    /// <summary>确认后文件变化阻止覆盖, 不以已冻结参数覆盖用户新字节.</summary>
    [Fact]
    public void ConcurrentAssetChangeIsRejectedBeforeWriting()
    {
        using var fixture = new AssetFixture();
        var json = fixture.Existing("pbi-utils/config.json", "old");
        using var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]);
        File.WriteAllText(json, "external");

        var error = Assert.Throws<AppException>(() => transaction.Apply(CancellationToken.None));

        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal("external", File.ReadAllText(json));
        transaction.Rollback();
        Assert.Equal("external", File.ReadAllText(json));
    }

    /// <summary>提交开始后未知结果保留资产和持久回执, 只有完整目标与资产回读相符才解除阻断.</summary>
    [Fact]
    public void UnknownCommitBlocksWritesUntilExactReadback()
    {
        using var fixture = new AssetFixture();
        var json = fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }

        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot));
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal("new", File.ReadAllText(json));
        DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", fixture.AssetRoot);
        Assert.Equal("new", File.ReadAllText(json));
    }

    /// <summary>两个引擎打开同一工程时共享未知资产提交阻断, 不按引擎备份子目录隔离掉同一文件的风险.</summary>
    [Fact]
    public void UnknownCommitAlsoBlocksAnotherEngineUsingSameProject()
    {
        using var fixture = new AssetFixture();
        fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }
        var otherEngine = Path.Combine(Path.GetDirectoryName(fixture.BackupRoot)!, "other-engine");

        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(otherEngine));
        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(otherEngine, "before", fixture.AssetRoot));
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        DesktopConfigurationAssetTransaction.VerifyPending(otherEngine, "after", fixture.AssetRoot);
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(otherEngine));
    }

    /// <summary>同引擎未核验提交不能被另存为后的新绑定根绕过, 直接资产事务也必须遵守.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedBindingCannotBypassTheEnginesPendingReceipt(bool directTransaction)
    {
        using var fixture = new AssetFixture();
        fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }
        var otherRoot = Path.Combine(Path.GetDirectoryName(fixture.AssetRoot)!, "other-project");
        Directory.CreateDirectory(otherRoot);

        var error = Assert.Throws<AppException>(() =>
        {
            if (directTransaction)
            {
                using var unexpected = new DesktopConfigurationAssetTransaction(otherRoot, fixture.BackupRoot, "before", "other-after", []);
            }
            else DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", otherRoot);
        });

        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(otherRoot));
    }

    /// <summary>同一冻结计划完整核验后同步本引擎回执, 不留永久未知状态或误覆盖其他计划.</summary>
    [Fact]
    public void VerifiedSharedReceiptAlsoCompletesMatchingEngineReceipt()
    {
        using var fixture = new AssetFixture();
        fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }

        DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", fixture.AssetRoot);

        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>本引擎仍有未决记录时, 同根共享回执丢失不能被解释为无事务.</summary>
    [Fact]
    public void MissingSharedReceiptDoesNotClearPendingEngineReceipt()
    {
        using var fixture = new AssetFixture();
        fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }
        var shared = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.BackupRoot)!, "*.assets.json"));
        File.Delete(shared);

        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", fixture.AssetRoot));

        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>另一引擎核验后的终态不能被下一轮覆盖; 原引擎可恢复, 合法另存为也不留下假未知.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedPlanRemainsRecoverableAfterAnotherEnginesNextCommit(bool changedBinding)
    {
        using var fixture = new AssetFixture();
        var json = fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }
        var otherEngine = Path.Combine(Path.GetDirectoryName(fixture.BackupRoot)!, "other-engine");
        DesktopConfigurationAssetTransaction.VerifyPending(otherEngine, "after", fixture.AssetRoot);
        using (var next = new DesktopConfigurationAssetTransaction(fixture.AssetRoot, otherEngine, "after", "after-next",
            [new("pbi-utils/config.json", fixture.Source("next"), Hash("new"), Hash("next"), 4)]))
        {
            next.Apply(CancellationToken.None);
            next.MarkCommitStarted();
            next.Complete();
        }
        var currentRoot = changedBinding ? Path.Combine(Path.GetDirectoryName(fixture.AssetRoot)!, "saved-as") : fixture.AssetRoot;
        Directory.CreateDirectory(currentRoot);

        DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "after", currentRoot);

        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        Assert.Equal("next", File.ReadAllText(json));
    }

    /// <summary>重复新增相同文件仍是独立轮次, 第二轮未知提交不能借第一轮成功证明解除阻断.</summary>
    [Fact]
    public void IdenticalCreatePlanCannotReuseAnotherTransactionsTerminalProof()
    {
        using var fixture = new AssetFixture();
        var source = fixture.Source("new");
        ModelConfigurationAsset[] plan = [new("pbi-utils/config.json", source, null, Hash("new"), 3)];
        using (var first = fixture.Prepare(plan))
        {
            first.Apply(CancellationToken.None);
            first.MarkCommitStarted();
            first.Complete();
        }
        // 模拟已经完成的 TMDL 退休, 以相同定义重新切入 Config.
        File.Delete(Path.Combine(fixture.AssetRoot, "pbi-utils", "config.json"));
        using (var second = fixture.Prepare(plan))
        {
            second.Apply(CancellationToken.None);
            second.MarkCommitStarted();
        }

        Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot));
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
    }

    /// <summary>无资产副作用不建立文件事务, 避免没有本轮备份槽的空计划污染未知提交回执.</summary>
    [Fact]
    public void EmptyAssetPlanDoesNotCreateTransaction()
    {
        using var fixture = new AssetFixture();
        var error = Assert.Throws<AppException>(() => { using var unexpected = fixture.Prepare([]); });
        Assert.Equal("CONFIGURATION_ASSET_PATH_INVALID", error.Code);
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>正常完成或提交前已回滚的记录不让普通 CRUD 依赖官方文件 Bridge.</summary>
    [Fact]
    public void TerminalReceiptsDoNotRequireAnotherFileCapabilityProbe()
    {
        using var fixture = new AssetFixture();
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), null, Hash("new"), 3)]))
        {
            Assert.True(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
            transaction.Complete();
        }
        Assert.False(DesktopConfigurationAssetTransaction.HasPendingTransactions(fixture.BackupRoot));
    }

    /// <summary>重复阶段属性不能让损坏的未知提交回执伪装为已完成并解除写入阻断.</summary>
    [Fact]
    public void DuplicateReceiptPhaseCannotBypassVerification()
    {
        using var fixture = new AssetFixture();
        fixture.Existing("pbi-utils/config.json", "old");
        using (var transaction = fixture.Prepare([new("pbi-utils/config.json", fixture.Source("new"), Hash("old"), Hash("new"), 3)]))
        {
            transaction.Apply(CancellationToken.None);
            transaction.MarkCommitStarted();
        }
        var path = Directory.GetFiles(Path.GetDirectoryName(fixture.BackupRoot)!, "*.assets.json").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Phase\":\"commit_started\"", "\"Phase\":\"commit_started\",\"Phase\":\"completed\"", StringComparison.Ordinal));

        var error = Assert.Throws<AppException>(() => DesktopConfigurationAssetTransaction.VerifyPending(fixture.BackupRoot, "before", fixture.AssetRoot));
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
    }

    /// <summary>只接受固定配置资产目录, 越界路径在备份或文件写入前拒绝.</summary>
    [Theory]
    [InlineData("../outside.json")]
    [InlineData("pbip/model.tmdl")]
    [InlineData("config/other.json")]
    [InlineData("config/config.json")]
    [InlineData("pbi-utils/other.json")]
    public void RejectsUnrelatedAssetPaths(string relative)
    {
        using var fixture = new AssetFixture();
        Assert.Throws<AppException>(() => fixture.Prepare([new(relative, fixture.Source("new"), null, Hash("new"), 3)]));
    }

    /// <summary>为测试生成固定 UTF-8 SHA256, 不依赖产品哈希实现.</summary>
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>每例拥有隔离工程、staging和备份目录, Dispose只清理本例随机根.</summary>
    private sealed class AssetFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-assets-test-" + Guid.NewGuid().ToString("N"));
        public string AssetRoot => Path.Combine(root, "project");
        public string BackupRoot => Path.Combine(root, "transactions", "project-id");
        /// <summary>创建测试原文件并返回完整路径.</summary>
        public string Existing(string relative, string content)
        {
            var path = Path.Combine(AssetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
        }
        /// <summary>创建本例拥有的冻结源文件.</summary>
        public string Source(string content)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, Guid.NewGuid().ToString("N")); File.WriteAllText(path, content); return path;
        }
        /// <summary>从精确计划建立待提交资产事务, 不提交模型.</summary>
        public DesktopConfigurationAssetTransaction Prepare(ModelConfigurationAsset[] assets) =>
            new(AssetRoot, BackupRoot, "before", "after", assets);
        /// <summary>删除本例唯一随机目录.</summary>
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
