// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DesktopUdfConfigurationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证高兼容级别直接通过, 低兼容级别才核验 Desktop 能力和独立升级确认的 TOM 保存边界.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

/// <summary>实际执行编译器、桥接、共享用例和原对象操作上下文; 仅替代引擎能力、连接和持久化边界.</summary>
public sealed class DesktopUdfConfigurationTests
{
    /// <summary>静默 MCP 以精确升级字段代替页面确认; 未明确请求时不进入模型暂存或提交.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentMcpUpgradeUsesExplicitRequestWithoutDialog(bool allowUpgrade)
    {
        await using var fixture = new Fixture();
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);
        if (allowUpgrade)
        {
            var saved = await fixture.Bridge.ApplyAsync(Request(context, true), current, fixture.Edits, default, silentMcp: true);
            Assert.Equal(1702, saved.Project.Model.CompatibilityLevel);
            Assert.Equal(1, fixture.Saves);
            Assert.True(fixture.SavedUpgradeApproval);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, default, silentMcp: true));
            Assert.Equal("COMPATIBILITY_UPGRADE_REQUIRED", error.Code);
            Assert.Equal(0, fixture.Saves);
            Assert.Equal(0, fixture.SessionsOpened);
            Assert.Equal(current.BaselineId, fixture.Read().BaselineId);
        }
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(0, fixture.UpgradeConfirmations);
        Assert.False(fixture.Edits.HasPendingChanges);
    }

    /// <summary>支持 1702 的引擎可在独立确认后提升 1606 模型并安装 UDF, 不创建 PBIP 文件资产.</summary>
    [Fact]
    public async Task SupportedEngineUpgrades1606AndInstallsUdfAfterSeparateConfirmation()
    {
        await using var fixture = new Fixture();
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var saved = await fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, default);

        Assert.Equal(1702, saved.Project.Model.CompatibilityLevel);
        Assert.Equal(1702, fixture.Live.CompatibilityLevel);
        Assert.Equal("() => 42", Assert.Single(fixture.Live.Model.Functions).Expression);
        Assert.Contains(saved.Project.Model.Functions, function => function.Name == "Fixture.Answer");
        Assert.Equal(ModelConfigurationReadback.Fingerprint(fixture.Live), saved.Project.BaselineId);
        Assert.Equal(current.ProjectId, saved.Project.ProjectId);
        Assert.Equal(current.ProjectPath, saved.Project.ProjectPath);
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(1, fixture.UpgradeConfirmations);

        Assert.Equal(1, fixture.Saves);
        Assert.True(fixture.SavedUpgradeApproval);
        Assert.Equal(0, fixture.SavedAssetCount);
        Assert.Equal(["capability", "upgrade", "commit"], fixture.Events);
        Assert.False(fixture.Edits.HasPendingChanges);
        Assert.False(fixture.Bridge.HasPendingChanges);
        Assert.Equal(1, fixture.SessionsOpened);
        Assert.Equal(1, fixture.SessionsClosed);
        Assert.Equal(0, fixture.Rollbacks);
    }

    /// <summary>模型已达到 1702 时直接安装, 不因缺失能力接口、版本读取错误或升级探测失败而阻止.</summary>
    [Theory]
    [InlineData(1702, "missing")]
    [InlineData(1702, "error")]
    [InlineData(1702, "unexpected")]
    [InlineData(1703, "missing")]
    [InlineData(1703, "error")]
    [InlineData(1703, "unexpected")]
    public async Task CompatibleModelInstallsWithoutVersionOrEngineCapabilityChecks(int compatibility, string capabilityState)
    {
        await using var fixture = new Fixture(compatibility: compatibility, unknownCapability: capabilityState == "missing",
            capabilityError: capabilityState == "error" ? "UDF_MODEL_WRITE_UNSUPPORTED" : null,
            capabilityAction: capabilityState == "unexpected" ? () => throw new InvalidOperationException("高兼容级别不应读取 Desktop 版本或引擎升级能力") : null);
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var saved = await fixture.Bridge.ApplyAsync(Request(context, allowUpgrade: true), current, fixture.Edits, default);

        Assert.Equal(compatibility, saved.Project.Model.CompatibilityLevel);
        Assert.Equal(compatibility, fixture.Live.CompatibilityLevel);
        Assert.Single(fixture.Live.Model.Functions);
        Assert.Equal(0, fixture.CapabilityChecks);
        Assert.Equal(0, fixture.UpgradeConfirmations);
        Assert.Equal(1, fixture.Saves);
        Assert.False(fixture.SavedUpgradeApproval);
        Assert.False(fixture.Edits.HasPendingChanges);
    }

    /// <summary>首次 1606 安装核验并升级后, 已达到 1702 的重复应用不再探测版本或升级能力, 无变化不保存.</summary>
    [Fact]
    public async Task ReapplyingInstalledUdfSkipsUpgradeCapabilityChecksAndAnotherSave()
    {
        await using var fixture = new Fixture();
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);
        var first = await fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, default);
        fixture.CapabilityAction = () => throw new InvalidOperationException("已升级至 1702 的模型不应重复探测升级能力");
        context = await fixture.Bridge.ContextAsync("current", first.Project, default);

        var repeated = await fixture.Bridge.ApplyAsync(Request(context), first.Project, fixture.Edits, default);

        Assert.Equal(first.Project.BaselineId, repeated.Project.BaselineId);
        Assert.True(repeated.NoChanges);
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(1, fixture.UpgradeConfirmations);

        Assert.Equal(1, fixture.Saves);
        Assert.False(fixture.Edits.HasPendingChanges);
        Assert.Equal(2, fixture.SessionsOpened);
        Assert.Equal(2, fixture.SessionsClosed);
        Assert.Equal(1, fixture.Rollbacks);
    }

    /// <summary>普通保存直接执行, 网页升级标记仍不能代替独立兼容升级确认.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclinedUpgradeLeavesModelUnchangedDespiteWebApproval(bool webApproval)
    {
        await using var fixture = new Fixture(upgradeApproved: false);
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context, webApproval), current, fixture.Edits, default));

        Assert.Equal("CANCELLED", error.Code);
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(1, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
    }

    /// <summary>高低模型在原对象事务准备时取消均结束事务和连接, 实际模型和待保存状态保持原样.</summary>
    [Theory]
    [InlineData(1606)]
    [InlineData(1702)]
    public async Task CancelledPreparationEndsOriginalSessionWithoutPendingChanges(int compatibility)
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new Fixture(compatibility: compatibility);
        fixture.PreparationAction = cancellation.Cancel;
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, cancellation.Token));
        Assert.Equal(compatibility < 1702 ? 1 : 0, fixture.CapabilityChecks);
        Assert.Equal(compatibility < 1702 ? 1 : 0, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
        Assert.Equal(1, fixture.SessionsOpened);
        Assert.Equal(1, fixture.SessionsClosed);
        Assert.Equal(1, fixture.Rollbacks);
    }

    /// <summary>1606 模型缺少受信核验接口或环境不支持升级时拒绝, 不写入且不弹升级确认.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LowCompatibilityModelRejectsUnsupportedOrUnknownUpgradeCapability(bool unknownCapability)
    {
        await using var fixture = new Fixture(compatibility: 1606, unknownCapability: unknownCapability,
            capabilityError: unknownCapability ? null : "UDF_MODEL_WRITE_UNSUPPORTED");
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context, allowUpgrade: true), current, fixture.Edits, default));

        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", error.Code);
        Assert.Equal(unknownCapability ? 0 : 1, fixture.CapabilityChecks);
        Assert.Equal(0, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
    }

    /// <summary>1606 模型的版本与升级能力只读核验期间取消必须终止, 不弹确认、不保存也不留草稿.</summary>
    [Fact]
    public async Task CancelledLowCompatibilityCheckLeavesModelUnchanged()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new Fixture(compatibility: 1606, capabilityAction: cancellation.Cancel);
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, cancellation.Token));

        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(0, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
    }

    /// <summary>1606 模型能力核验或升级确认完成前窗口身份失效, 迟到的同意不能继续建立或提交候选.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedLowCompatibilitySessionRejectsLateCapabilityOrUpgradeApproval(bool duringUpgrade)
    {
        await using var fixture = new Fixture(compatibility: 1606);
        if (duringUpgrade) fixture.UpgradeAction = fixture.Bridge.Invalidate;
        else fixture.CapabilityAction = fixture.Bridge.Invalidate;
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, default));

        Assert.Equal("STALE_CONFIGURATION_TARGET", error.Code);
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(duringUpgrade ? 1 : 0, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
    }

    /// <summary>高兼容级别跳过能力探测后仍要求至少一张表; 低级别的能力核验和升级同意也不能绕过.</summary>
    [Theory]
    [InlineData(1606)]
    [InlineData(1702)]
    [InlineData(1703)]
    public async Task EmptyModelStillRejectsUdfWithoutChangingCompatibility(int compatibility)
    {
        await using var fixture = new Fixture(compatibility: compatibility, hasTables: false,
            capabilityAction: compatibility >= 1702 ? () => throw new InvalidOperationException("高兼容级别空模型也不应探测版本或升级能力") : null);
        var current = fixture.Read();
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context), current, fixture.Edits, default));

        Assert.Equal("UDF_MODEL_EMPTY", error.Code);
        Assert.Equal(compatibility < 1702 ? 1 : 0, fixture.CapabilityChecks);

        AssertUnchanged(fixture, current);
    }

    /// <summary>Service 和 SSAS/XMLA 的 UDF 请求维持只读, 即使网页提供升级允许标记.</summary>
    [Theory]
    [InlineData("powerbi-service")]
    [InlineData("ssas-xmla")]
    public async Task RemoteModelRejectsUdfBeforeCapabilityOrConfirmation(string kind)
    {
        await using var fixture = new Fixture();
        var current = fixture.Read() with { Connection = new(kind, "Remote") };
        fixture.Edits.Select(current);
        var context = await fixture.Bridge.ContextAsync("current", current, default);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Bridge.ApplyAsync(Request(context, allowUpgrade: true), current, fixture.Edits, default));

        Assert.Equal("READ_ONLY_MODEL", error.Code);
        Assert.Equal(0, fixture.CapabilityChecks);
        Assert.Equal(0, fixture.UpgradeConfirmations);

        AssertUnchanged(fixture, current);
    }

    /// <summary>完整 TOM 指纹、兼容级别、保存次数和两个共享服务状态共同证明失败前未提交.</summary>
    private static void AssertUnchanged(Fixture fixture, ProjectSnapshot original)
    {
        Assert.Equal(original.BaselineId, ModelConfigurationReadback.Fingerprint(fixture.Live));
        Assert.Equal(original.Model.CompatibilityLevel, fixture.Live.CompatibilityLevel);
        Assert.Empty(fixture.Live.Model.Functions);
        Assert.Equal(0, fixture.Saves);
        Assert.False(fixture.Edits.HasPendingChanges);
        Assert.False(fixture.Bridge.HasPendingChanges);
    }

    /// <summary>网页请求仅提交函数选择; 即使携带升级允许标记, 宿主也必须重新独立确认.</summary>
    private static JsonElement Request(ModelConfigurationContext context, bool allowUpgrade = false) => JsonSerializer.SerializeToElement(new
    {
        targetId = context.TargetId,
        baselineId = context.Project.BaselineId,
        section = "udfs",
        request = new ModelConfigurationRequest(Udfs: [new("daxlib", "Fixture", "1.0.0", ["Fixture.Answer"])], AllowCompatibilityUpgrade: allowUpgrade)
    }, JsonSerializerOptions.Web);

    /// <summary>夹具实时模型只存在本实例内存, 用户存储仅使用随机临时目录, 不访问用户模型.</summary>
    private sealed class Fixture : IProjectReader, IModelEditWorkspace, IModelCompatibilityUpgradeWorkspace, IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-desktop-udf-" + Guid.NewGuid().ToString("N"));
        private readonly ScaffoldRuntime runtime;
        private readonly ModelConfigurationCompiler compiler;
        private string serverDefinition;
        public TOM.Database Live { get; private set; }
        public ModelConfigurationBridge Bridge { get; }
        public ModelEditService Edits { get; }
        public List<string> Events { get; } = [];
        public int CapabilityChecks { get; private set; }
        public int UpgradeConfirmations { get; private set; }
        public int Saves { get; private set; }
        public int SessionsOpened { get; private set; }
        public int SessionsClosed { get; private set; }
        public int Rollbacks { get; private set; }
        public bool SavedUpgradeApproval { get; private set; }
        public int SavedAssetCount { get; private set; }
        public Action? CapabilityAction { get; set; }
        public Action? UpgradeAction { get; set; }
        public Action? PreparationAction { get; set; }

        /// <summary>建立真实 TOM 空白测试模型, 升级确认必须发生在实际模型修改前, 准备钩子仅用于事务取消验证.</summary>
        public Fixture(int compatibility = 1606, bool hasTables = true, bool upgradeApproved = true,
            bool unknownCapability = false, string? capabilityError = null, Action? capabilityAction = null)
        {
            this.capabilityError = capabilityError;
            CapabilityAction = capabilityAction;
            object[] tables = hasTables ? [new { name = "Existing" }] : [];
            serverDefinition = JsonSerializer.Serialize(new
            {
                id = "desktop-udf",
                name = "DesktopUdf",
                compatibilityLevel = compatibility,
                model = new { culture = "en-US", tables }
            });
            Live = TOM.JsonSerializer.DeserializeDatabase(serverDefinition);
            runtime = new ScaffoldRuntime(root);
            compiler = new(new UdfDependencyResolver(new Packages(), runtime.Local));
            Bridge = new(this, new UnavailableWorkspace(), compiler, confirmUpgrade: (baseline, level, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(Read().BaselineId, baseline.BaselineId);
                Assert.Equal(compatibility, Live.CompatibilityLevel);
                Assert.Empty(Live.Model.Functions);
                Assert.Equal(1702, level);
                Events.Add("upgrade"); UpgradeConfirmations++;
                UpgradeAction?.Invoke();
                return Task.FromResult(upgradeApproved);
            }, currentWorkspace: unknownCapability ? new DraftOnlyWorkspace(this) : this);
            Edits = new(this, this);

        }

        /// <summary>每次读取映射实际完整 TOM; 基线没有替换为候选或网页提供的值.</summary>
        public ProjectSnapshot Read() => new("model://desktop-udf", "desktop-udf", ModelConfigurationReadback.Fingerprint(Live), "online", "", "", null,
            ModelSnapshotMapper.Map(Live), [], [], [], new("desktop", "Power BI Desktop"));

        /// <summary>读取本夹具实际模型, 不读取磁盘模型或返回预设成功快照.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read());
        }

        /// <summary>模拟原连接的只读能力响应, 核对实际基线与要求级别, 不改变模型.</summary>
        public Task VerifyCompatibilityUpgradeAsync(ProjectSnapshot baseline, int compatibilityLevel, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Read().ProjectId, baseline.ProjectId);
            Assert.Equal(Read().ProjectPath, baseline.ProjectPath);
            Assert.Equal(Read().BaselineId, baseline.BaselineId);
            Assert.Equal(1702, compatibilityLevel);
            Events.Add("capability"); CapabilityChecks++;
            CapabilityAction?.Invoke();
            token.ThrowIfCancellationRequested();
            if (capabilityError is not null) throw new AppException(capabilityError, "测试引擎不支持要求级别", 3);
            return Task.CompletedTask;
        }

        /// <summary>固定服务器文本分别提供读取视图和本次连接原对象, 真实上下文在原对象校验, 唯一提交边界更新模拟服务器.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (baseline.BaselineId != Read().BaselineId) throw new AppException("STALE_BASELINE", "测试模型已变化", 3);
            var original = TOM.JsonSerializer.DeserializeDatabase(serverDefinition);
            SessionsOpened++;
            return Task.FromResult<IModelDraft>(new DesktopModelDraft(baseline, original,
                new OriginalConnection(original, () => SessionsClosed++), () => PreparationAction?.Invoke(), () => Rollbacks++,
                (edits, committing, committed, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Assert.NotSame(Live, original);
                    Assert.Equal(baseline.BaselineId, ModelConfigurationReadback.Fingerprint(Live));
                    var plan = Assert.Single(edits).Configuration!;
                    SavedUpgradeApproval = plan.Request.AllowCompatibilityUpgrade;
                    SavedAssetCount = plan.Assets.Count;
                    committing?.Invoke();
                    Events.Add("commit"); Saves++;
                    serverDefinition = TOM.JsonSerializer.SerializeDatabase(original);
                    var old = Live; Live = TOM.JsonSerializer.DeserializeDatabase(serverDefinition); old.Dispose();
                    committed();
                    return Task.FromResult(Read() with { SaveResult = new(true, false, true, "测试模型已保存") });
                }));
        }

        /// <summary>关闭全部自有草稿、计划和 TOM, 只删除本夹具随机临时目录.</summary>
        public async ValueTask DisposeAsync()
        {
            await Edits.DisposeAsync(); Bridge.Dispose(); compiler.Dispose(); runtime.Dispose(); Live.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private readonly string? capabilityError;
    }

    /// <summary>只模拟连接资源所有权, 结束连接不改变独立模拟服务器的定义.</summary>
    private sealed class OriginalConnection(TOM.Database original, Action closed) : IDisposable
    {
        /// <summary>释放连接读取的原对象并记录实际关闭, 不提交或恢复服务器内容.</summary>
        public void Dispose() { original.Dispose(); closed(); }
    }

    /// <summary>仅提供原对象操作接口时高兼容级别可写, 低级别升级不能绕过缺失的能力接口.</summary>
    private sealed class DraftOnlyWorkspace(IModelEditWorkspace inner) : IModelEditWorkspace
    {
        /// <summary>只转发草稿创建, 不提供模拟引擎能力.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => inner.CreateDraftAsync(baseline, token);
    }

    /// <summary>文件工作区不能替代当前 Desktop 能力和草稿通道.</summary>
    private sealed class UnavailableWorkspace : IModelEditWorkspace
    {
        /// <summary>错误使用文件工作区立即失败, 不创建任何文件或草稿.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new InvalidOperationException("不得使用文件工作区");
    }

    /// <summary>固定来源只返回合法函数包, 真实解析器仍负责冻结和提交前来源复核.</summary>
    private sealed class Packages : IUdfPackageProvider
    {
        /// <summary>本用例没有目录搜索; 返回空目录.</summary>
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<UdfPackageSummary>>([]);
        /// <summary>返回本夹具唯一可冻结版本.</summary>
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["1.0.0"]);
        /// <summary>提供固定哈希、许可和实际函数定义, 不访问网络或用户库.</summary>
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => Task.FromResult(new UdfPackage(packageId, version,
            new string('a', 64), "fixture", "fixture", new Dictionary<string, string> { ["LICENSE.txt"] = "Fixture license" }, [],
            [new("Fixture.Answer", "() => 42", "answer", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch));
    }
}
