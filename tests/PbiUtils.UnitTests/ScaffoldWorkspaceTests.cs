// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldWorkspaceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实临时 PBIP 文件验证脚手架预览, 跨实例提交和取消边界.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Udf.Sources;
using System.Security.Cryptography;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>临时工程内运行真实清单和发布, 不触碰用户工程.</summary>
public sealed class ScaffoldWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-" + Guid.NewGuid().ToString("N"));

    /// <summary>可靠回执尚未完成时不得发布, 完成后只提交一次且重读不再调用钩子.</summary>
    [Fact]
    public async Task AwaitsBeforeCommitHookBeforePublishing()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var service = new ScaffoldService(Workspace(catalog));
        var plan = await service.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var creating = service.CreateAsync(plan.Digest, request, plan.OutputPath, null, default,
            () => commits++, async token => { entered.SetResult(); await release.Task.WaitAsync(token); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(creating.IsCompleted);
        Assert.False(Directory.Exists(plan.OutputPath));
        Assert.Equal(0, commits);
        release.SetResult();
        Assert.True((await creating).Verified);
        var replay = await service.CreateAsync(plan.Digest, request, plan.OutputPath, null, default,
            beforeCommit: _ => throw new InvalidOperationException("must not replay"));
        Assert.True(replay.ProjectCreated);
        Assert.Equal(1, commits);
    }

    /// <summary>回执失败、取消和等待期间模板变化均不进入发布边界.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("baseline")]
    public async Task BeforeCommitHookFailureCancellationOrStaleTemplateDoesNotPublish(string scenario)
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var service = new ScaffoldService(Workspace(catalog));
        var plan = await service.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        using var cancellation = new CancellationTokenSource();
        var commits = 0;
        var error = await Record.ExceptionAsync(() => service.CreateAsync(plan.Digest, request, plan.OutputPath, null, cancellation.Token,
            () => commits++, async token =>
            {
                Assert.Equal(cancellation.Token, token);
                await Task.Yield();
                if (scenario == "failure") throw new AppException("RECEIPT_FAILED", "回执失败");
                if (scenario == "cancel") cancellation.Cancel();
                else catalog.Baseline = new string('b', 64);
            }));
        if (scenario == "cancel") Assert.IsType<OperationCanceledException>(error, exactMatch: false);
        else Assert.Equal(scenario == "failure" ? "RECEIPT_FAILED" : "STALE_TEMPLATE", Assert.IsType<AppException>(error).Code);
        Assert.Equal(0, commits);
        Assert.False(Directory.Exists(plan.OutputPath));
        Assert.True(Directory.Exists(Stage(plan)));
    }

    /// <summary>跨两个适配实例消费同一持久计划, 最终模型真实回读.</summary>
    [Fact]
    public async Task CreatesFrozenPlanAcrossInstancesAndRefusesReplay()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var first = Workspace(catalog);
        var plan = await first.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.False(Directory.Exists(plan.OutputPath));
        Assert.NotEmpty(plan.Files);
        using var second = Workspace(catalog);
        var result = await second.CreateAsync(plan.Digest, request, plan.OutputPath, null, CancellationToken.None);
        Assert.True(result.ProjectCreated);
        Assert.True(result.Verified);
        Assert.True(File.Exists(result.ProjectPath));
        var replay = await second.CreateAsync(plan.Digest, request, plan.OutputPath, null, CancellationToken.None);
        Assert.Equal(result.PlanId, replay.PlanId);
        Assert.Equal(result.ProjectPath, replay.ProjectPath);
        Assert.Equal(result.BaselineId, replay.BaselineId);
    }

    /// <summary>输出已存在和批准后参数改变均不得发布任何新工程.</summary>
    [Fact]
    public async Task RejectsExistingOutputAndChangedApproval()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var workspace = Workspace(catalog);
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        Assert.Equal("OUTPUT_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => workspace.PreviewAsync(request, output, null, CancellationToken.None))).Code);
        Directory.Delete(output);
        var plan = await workspace.PreviewAsync(request, output, null, CancellationToken.None);
        Assert.Equal("SCAFFOLD_APPROVAL_MISMATCH", (await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request with { GitInitialize = true }, output, null, CancellationToken.None))).Code);
        Assert.False(Directory.Exists(output));
    }

    /// <summary>模板在预览后被外部改变, 不能使用旧批准.</summary>
    [Fact]
    public async Task RejectsTemplateChangedAfterPreview()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var workspace = Workspace(catalog);
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        catalog.Baseline = new string('b', 64);
        Assert.Equal("STALE_TEMPLATE", (await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, CancellationToken.None))).Code);
        Assert.False(Directory.Exists(plan.OutputPath));
    }

    /// <summary>即使自定义来源只由间接依赖引入, 跨进程创建仍须复核冻结的来源基线.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsChangedOrDeletedCustomSourceBeforeCommit(bool delete)
    {
        Directory.CreateDirectory(root);
        var catalog = new FixtureCatalog(); var request = Request(catalog);
        var sourceRoot = Path.Combine(root, "user"); var sources = new UdfSourceStore(sourceRoot);
        var source = await sources.SaveAsync("Team", "https://github.com/example/functions", null, null, default);
        var functions = new[] { new ScaffoldUdfPreview("Team.One", source.Id, "Team.Library", "1.0.0", true, new string('c', 64), source.Baseline) };
        using var first = new ScaffoldWorkspace(sourceRoot, catalog, new FixtureComposer(functions), udfSources: sources);
        var plan = await first.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        if (delete) await sources.DeleteAsync(source.Id, source.Baseline!, default);
        else await sources.SaveAsync("Team", "https://github.com/example/other", source.Id, source.Baseline, default);
        using var second = new ScaffoldWorkspace(sourceRoot, catalog, new FixtureComposer(), udfSources: new UdfSourceStore(sourceRoot));
        var committed = false;
        Assert.Equal("STALE_UDF_SOURCE", (await Assert.ThrowsAsync<AppException>(() => second.CreateAsync(plan.Digest, request, plan.OutputPath, null, default, () => committed = true))).Code);
        Assert.False(committed); Assert.False(Directory.Exists(plan.OutputPath)); Assert.True(Directory.Exists(Stage(plan)));
    }

    /// <summary>来源后续删除不改变已发布事实, 再次读取回执不重放工程创建.</summary>
    [Fact]
    public async Task PublishedReceiptSurvivesSourceDeletion()
    {
        Directory.CreateDirectory(root);
        var catalog = new FixtureCatalog(); var request = Request(catalog);
        var sourceRoot = Path.Combine(root, "user"); var sources = new UdfSourceStore(sourceRoot);
        var source = await sources.SaveAsync("Team", "https://github.com/example/functions", null, null, default);
        var functions = new[] { new ScaffoldUdfPreview("Team.One", source.Id, "Team.Library", "1.0.0", true, new string('c', 64), source.Baseline) };
        using var workspace = new ScaffoldWorkspace(sourceRoot, catalog, new FixtureComposer(functions), udfSources: sources);
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        await sources.DeleteAsync(source.Id, source.Baseline!, default);
        var replay = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.Equal(created.PlanId, replay.PlanId); Assert.True(replay.ProjectCreated); Assert.True(replay.Verified);
    }

    /// <summary>移动提交后取消不能把已经存在的项目改报为取消或失败.</summary>
    [Fact]
    public async Task DrainsAfterCommitEvenWhenCancellationArrives()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var workspace = Workspace(catalog);
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, cancellation.Token, cancellation.Cancel);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.ProjectCreated);
        Assert.True(result.Verified);
    }

    /// <summary>Git 后处理失败仍保留项目已创建的权威事实.</summary>
    [Fact]
    public async Task PreservesProjectWhenGitInitializationFails()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog) with { GitInitialize = true };
        using var workspace = Workspace(catalog, git: (_, _) => Task.FromResult(false));
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, CancellationToken.None);
        Assert.True(result.ProjectCreated);
        Assert.True(result.Verified);
        Assert.False(result.GitInitialized);
        Assert.Equal("GIT_INIT_FAILED", result.ErrorCode);
    }

    /// <summary>显式 Git 请求在缺失, 失败或超时后保留, 原因进入冻结计划并保留发布事实.</summary>
    [Theory]
    [InlineData("GIT_NOT_FOUND")]
    [InlineData("GIT_PROBE_FAILED")]
    [InlineData("GIT_PROBE_TIMEOUT")]
    public async Task GitPreflightPreservesExplicitRequestAndReportsPartialFacts(string reason)
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog) with { GitInitialize = true };
        var initializations = 0;
        var probes = 0;
        using var workspace = Workspace(catalog,
            git: (_, _) => { initializations++; return Task.FromResult(true); },
            probe: _ => { probes++; return Task.FromResult(new GitCapability(false, reason)); });
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        Assert.True(plan.Request.GitInitialize);
        Assert.Contains(reason, plan.Warnings);
        Assert.Contains(plan.Warnings, warning => warning.Contains("安装", StringComparison.Ordinal));
        var digest = plan.Digest;
        var result = await workspace.CreateAsync(digest, request, plan.OutputPath, null, default);
        Assert.True(result.ProjectCreated);
        Assert.True(result.Verified);
        Assert.False(result.GitInitialized);
        Assert.Equal(reason, result.ErrorCode);
        Assert.Contains(reason, result.Warnings);
        Assert.Contains(result.Warnings, warning => warning.Contains("不要重复创建", StringComparison.Ordinal));
        Assert.Equal(digest, plan.Digest);
        Assert.True(File.Exists(result.ProjectPath));
        Assert.Equal(2, probes);
        Assert.Equal(0, initializations);
        var replay = await workspace.CreateAsync(digest, request, plan.OutputPath, null, default);
        Assert.Equal(result.PlanId, replay.PlanId);
        Assert.Equal(reason, replay.ErrorCode);
        Assert.Equal(2, probes);
        Assert.Equal(0, initializations);
    }

    /// <summary>冻结后 Git 消失不重新发布, 不调用初始化或修改冻结参数.</summary>
    [Fact]
    public async Task GitDisappearingAfterFreezeDoesNotReplayProject()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog) with { GitInitialize = true };
        var probes = 0;
        var initializations = 0;
        using var workspace = Workspace(catalog,
            git: (_, _) => { initializations++; return Task.FromResult(true); },
            probe: _ => Task.FromResult(++probes == 1 ? new GitCapability(true) : new GitCapability(false, "GIT_NOT_FOUND")));
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, default);
        Assert.DoesNotContain("GIT_NOT_FOUND", plan.Warnings);
        var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(result.ProjectCreated);
        Assert.True(result.Verified);
        Assert.Equal("GIT_NOT_FOUND", result.ErrorCode);
        Assert.Equal(0, initializations);
        Assert.Equal(2, probes);
        Assert.True(plan.Request.GitInitialize);
        Assert.Equal(plan.Digest, ScaffoldPlanStore.Digest(plan));
    }

    /// <summary>Git 探测取消先排空, 不创建 staging 或目标工程.</summary>
    [Fact]
    public async Task CancellingGitPreflightLeavesNoProjectOrStage()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog) with { GitInitialize = true };
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var workspace = Workspace(catalog, probe: async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitCapability(true);
        });
        var running = workspace.PreviewAsync(request, Path.Combine(root, "output"), null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
        Assert.Empty(Directory.EnumerateDirectories(root, ".pbi-utils-scaffold-*"));
    }

    /// <summary>实际流式清单超预算时清理未提交的 staging, 不出现最终目录.</summary>
    [Fact]
    public async Task RejectsOutputBudgetAndCleansStage()
    {
        var catalog = new FixtureCatalog();
        using var workspace = Workspace(catalog, maximumBytes: 20);
        await Assert.ThrowsAsync<AppException>(() => workspace.PreviewAsync(Request(catalog), Path.Combine(root, "output"), null, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-scaffold-*"));
    }

    /// <summary>预览后注入的 Git 内容也属于清单外部变化, 不得随工程发布.</summary>
    /// <param name="relative">项目根或固定 PBIP 目录中未经批准的 Git 元数据位置.</param>
    [Theory]
    [InlineData(".git")]
    [InlineData("pbip/.git")]
    public async Task RejectsGitMetadataInjectedIntoStage(string relative)
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var workspace = Workspace(catalog);
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        var git = Path.Combine(root, ".pbi-utils-scaffold-" + plan.Id, relative, "hooks");
        Directory.CreateDirectory(git);
        await File.WriteAllTextAsync(Path.Combine(git, "post-checkout"), "not executed");
        Assert.Equal("SCAFFOLD_FILES_CHANGED", (await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, CancellationToken.None))).Code);
        Assert.False(Directory.Exists(plan.OutputPath));
    }

    /// <summary>共享实例替换预览也须回收旧树, 不只依赖桌面主动丢弃.</summary>
    [Fact]
    public async Task ReplacesOwnPreparedPlan()
    {
        var catalog = new FixtureCatalog();
        var request = Request(catalog);
        using var workspace = Workspace(catalog);
        var first = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        var second = await workspace.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.Single(Directory.GetDirectories(root, ".pbi-utils-scaffold-*"));
        Assert.False(Directory.Exists(Stage(first)));
        Assert.True(Directory.Exists(Stage(second)));
        Assert.Equal("SCAFFOLD_PLAN_EXPIRED", (await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(first.Digest, request, first.OutputPath, null, CancellationToken.None))).Code);
    }

    /// <summary>新进程预览只回收已过期的可验证 prepared 树, 保留活动和未知提交.</summary>
    [Fact]
    public async Task SweepsExpiredPlansButPreservesActiveAndCommitting()
    {
        var catalog = new FixtureCatalog();
        var clock = new FixtureClock();
        var request = Request(catalog);
        using var first = Workspace(catalog, clock: clock);
        var expired = await first.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        using var second = Workspace(catalog, clock: clock);
        var unknown = await second.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        var receiptPath = Path.Combine(root, "user", "scaffold", "plans", unknown.Digest + ".json");
        await File.WriteAllTextAsync(receiptPath, (await File.ReadAllTextAsync(receiptPath)).Replace("\"state\":\"prepared\"", "\"state\":\"committing\"", StringComparison.Ordinal));
        clock.Now = clock.Now.AddMinutes(16);
        using var third = Workspace(catalog, clock: clock);
        var active = await third.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.False(Directory.Exists(Stage(expired)));
        Assert.True(Directory.Exists(Stage(unknown)));
        Assert.True(Directory.Exists(Stage(active)));
        using var fourth = Workspace(catalog, clock: clock);
        _ = await fourth.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.True(Directory.Exists(Stage(active)));
    }

    /// <summary>跨进程计划正被消费时跳过清理, 解锁后仍可根据回执重试.</summary>
    [Fact]
    public async Task SweepSkipsLockedPlanAndRetriesLater()
    {
        var catalog = new FixtureCatalog();
        var clock = new FixtureClock();
        var request = Request(catalog);
        using var first = Workspace(catalog, clock: clock);
        var expired = await first.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(16);
        var lockPath = Path.Combine(root, "user", "scaffold", "plans", expired.Digest + ".lock");
        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var second = Workspace(catalog, clock: clock);
            _ = await second.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
            Assert.True(Directory.Exists(Stage(expired)));
        }
        using var third = Workspace(catalog, clock: clock);
        _ = await third.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.False(Directory.Exists(Stage(expired)));
    }

    /// <summary>清理遇到文件锁时保留 prepared 归属记录, 后续才能安全重试.</summary>
    [Fact]
    public async Task SweepRetainsReceiptWhenStageCleanupFails()
    {
        var catalog = new FixtureCatalog();
        var clock = new FixtureClock();
        var request = Request(catalog);
        using var first = Workspace(catalog, clock: clock);
        var expired = await first.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(16);
        var receipt = Path.Combine(root, "user", "scaffold", "plans", expired.Digest + ".json");
        using (var held = new FileStream(Path.Combine(Stage(expired), expired.EntryFile), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var second = Workspace(catalog, clock: clock);
            var plan = await second.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
            Assert.Contains("SCAFFOLD_CLEANUP_PENDING", plan.Warnings);
            Assert.Contains("\"state\":\"prepared\"", await File.ReadAllTextAsync(receipt), StringComparison.Ordinal);
        }
        using var third = Workspace(catalog, clock: clock);
        _ = await third.PreviewAsync(request, Path.Combine(root, "output"), null, CancellationToken.None);
        Assert.False(Directory.Exists(Stage(expired)));
        Assert.Contains("\"state\":\"discarded\"", await File.ReadAllTextAsync(receipt), StringComparison.Ordinal);
    }

    /// <summary>派生 staging 位置仅用于断言测试自己的随机目录.</summary>
    private static string Stage(ScaffoldPlan plan) => Path.Combine(Path.GetDirectoryName(plan.OutputPath)!, ".pbi-utils-scaffold-" + plan.Id);

    /// <summary>删除测试独占目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    /// <summary>构造隔离工作区, 测试可注入 Git 与预算.</summary>
    private ScaffoldWorkspace Workspace(FixtureCatalog catalog, long maximumBytes = 8L * 1024 * 1024 * 1024, Func<string, CancellationToken, Task<bool>>? git = null, TimeProvider? clock = null,
        Func<CancellationToken, Task<GitCapability>>? probe = null)
    {
        Directory.CreateDirectory(root);
        return new(Path.Combine(root, "user"), catalog, new FixtureComposer(), timeProvider: clock, maximumBytes: maximumBytes, initializeGit: git, probeGit: probe);
    }

    /// <summary>推进到计划截止后, 不以真实等待代替时钟边界断言.</summary>
    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>构造绑定当前目录基线的最小请求.</summary>
    private static ScaffoldRequest Request(FixtureCatalog catalog) => new(1, "test:fixture", catalog.Baseline, "output", "zh-cn", null, null, null, null, null);

    /// <summary>以可改变摘要模拟真实模板外部修改.</summary>
    private sealed class FixtureCatalog : IPbipTemplateCatalog
    {
        internal string Baseline { get; set; } = Convert.ToHexStringLower(SHA256.HashData([1]));
        /// <summary>返回唯一模板显示条目.</summary>
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PbipTemplateInfo>>([Info()]);
        /// <summary>冻结当前模板身份.</summary>
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(new ResolvedPbipTemplate(Info(), "pbip/测试.pbip", []));
        /// <summary>创建当前身份显示信息.</summary>
        private PbipTemplateInfo Info() => new("test:fixture", "fixture", "test", Baseline, "", []);
    }

    /// <summary>真实写入最小 PBIP, 让发布回读经过正式 ProjectReader.</summary>
    private sealed class FixtureComposer(IReadOnlyList<ScaffoldUdfPreview>? functions = null) : IScaffoldProjectComposer
    {
        /// <summary>只写测试拥有的 staging.</summary>
        public Task<ScaffoldComposition> ComposeAsync(ResolvedPbipTemplate template, ScaffoldRequest request, string staging, string output,
            IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken)
        {
            PbipFixture.Create(Path.Combine(staging, "pbip"));
            return Task.FromResult(new ScaffoldComposition("pbip/测试.pbip", [], [], 1606, functions));
        }
    }
}
