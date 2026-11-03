// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopConfigurationCommitGuardTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以确定性门闩验证已开启草稿的跨引擎配置资产提交阻断, 不连接真实 Desktop.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopConfigurationCommitGuardTests
{
    /// <summary>B 已完成普通 CRUD 暂存后, A 同工程未知资产提交仍必须阻止 B 在另一引擎提交.</summary>
    /// <param name="bindingAvailable">B 能否从官方文件状态核实当前 PBIP; 不能核实时也不得穿透未知事务.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyOpenedDraftCannotCommitAfterAnotherEngineUnknownAssetWrite(bool bindingAvailable)
    {
        using var fixture = new GuardFixture();
        using var database = new TOM.Database { Name = "test", ID = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        database.Model.Tables.Add(new TOM.Table { Name = "Sales" });
        var baseline = new ProjectSnapshot("model://engine-b", "engine-b", "before", "TMDL", "", "", null,
            ModelSnapshotMapper.Map(database, "test"), [], [], [], new("desktop", "Power BI Desktop"));
        var submissions = 0;
        await using var draft = new DesktopModelDraft(baseline, database, new Session(), () => { }, () => { }, async (_, _, _, token) =>
        {
            using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.OtherBackupRoot, baseline.BaselineId,
                _ => Task.FromResult<string?>(bindingAvailable ? fixture.AssetRoot : null), token);
            submissions++;
            return baseline;
        });
        await draft.ApplyAsync(new("table.set", "Sales", "description", "B pending edit"), default);
        Assert.True(draft.Snapshot.HasPendingChanges);
        using (var firstEngine = fixture.Prepare())
        {
            firstEngine.Apply(default);
            firstEngine.MarkCommitStarted();
        }

        var error = await Assert.ThrowsAsync<AppException>(() => draft.SaveAsync(null, default));

        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", error.Code);
        Assert.Equal(0, submissions);
        Assert.Equal("new", File.ReadAllText(fixture.Json));
    }

    /// <summary>B 最终检查后持有 guard 到完整提交排空, A 不能在检查和提交之间新增未知资产事务.</summary>
    [Fact]
    public async Task GuardClosesRaceBetweenFinalPendingCheckAndModelCommit()
    {
        using var fixture = new GuardFixture();
        var checkedPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootsRead = 0;
        var submissions = 0;
        var secondEngine = Task.Run(async () =>
        {
            using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.OtherBackupRoot, "before", _ =>
            {
                rootsRead++;
                return Task.FromResult<string?>(fixture.AssetRoot);
            }, default);
            checkedPending.SetResult();
            await allowCommit.Task;
            submissions++;
        });
        await checkedPending.Task;
        try
        {
            var error = Assert.Throws<AppException>(() => fixture.Prepare());
            Assert.Equal("USER_DATA_BUSY", error.Code);
            Assert.Equal(0, rootsRead);
            Assert.Equal(0, submissions);
            Assert.Equal("old", File.ReadAllText(fixture.Json));
        }
        finally { allowCommit.SetResult(); await secondEngine; }
        Assert.Equal(1, submissions);
        using var firstEngine = fixture.Prepare();
        firstEngine.Apply(default);
        firstEngine.MarkCommitStarted();
        firstEngine.Complete();
    }

    /// <summary>配置提交借用同一已持有 guard 而不重入文件锁, 回执和原子资产更新仍正常完成.</summary>
    [Fact]
    public async Task ConfigurationTransactionBorrowsOwnedGuardWithoutReenteringLock()
    {
        using var fixture = new GuardFixture();
        using var guard = await DesktopConfigurationCommitGuard.AcquireAsync(fixture.BackupRoot, "before",
            _ => throw new InvalidOperationException("No pending transaction should require a file capability"), default);
        using var transaction = fixture.Prepare(guard);

        transaction.Apply(default);
        transaction.MarkCommitStarted();
        transaction.Complete();

        Assert.Equal("new", File.ReadAllText(fixture.Json));
    }

    /// <summary>测试会话不打开网络资源, 仅满足原对象操作上下文的释放契约.</summary>
    private sealed class Session : IDisposable { public void Dispose() { } }

    /// <summary>测试独占随机根, 两个引擎共享事务父目录和真实工程资产根.</summary>
    private sealed class GuardFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-asset-guard-" + Guid.NewGuid().ToString("N"));
        public string AssetRoot => Path.Combine(root, "project");
        public string BackupRoot => Path.Combine(root, "transactions", "engine-a");
        public string OtherBackupRoot => Path.Combine(root, "transactions", "engine-b");
        public string Json => Path.Combine(AssetRoot, "pbi-utils", "config.json");
        private string Source => Path.Combine(root, "source.json");
        public GuardFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Json)!);
            File.WriteAllText(Json, "old"); File.WriteAllText(Source, "new");
        }
        /// <summary>冻结一份当前文件替换计划, 可借用产品提交 guard.</summary>
        public DesktopConfigurationAssetTransaction Prepare(DesktopConfigurationCommitGuard? guard = null) => new(AssetRoot, BackupRoot,
            "before", "after", [new ModelConfigurationAsset("pbi-utils/config.json", Source, Hash("old"), Hash("new"), 3)], guard);
        /// <summary>固定 UTF-8 哈希独立核对测试输入.</summary>
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        /// <summary>删除本例随机根, 不访问真实用户目录.</summary>
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
