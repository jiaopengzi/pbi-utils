// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureBridgeReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用可控来源门闩验证目标撤销后的模板请求不能进入操作上下文, 无变化请求也遵守身份边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅以受控只读快照和无变化草稿验证桥接编排, 不连接真实引擎.</summary>
public sealed class TemplateMeasureBridgeReviewTests
{
    /// <summary>共享服务在无变化准备结束时取消, 仍须取消并排空上下文, 不能只由桥接补充取消检查.</summary>
    [Fact]
    public async Task SharedServiceCancelsAndDisposesUnchangedPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-service-cancel-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new ProjectSnapshot("model://service-cancel-review", "project", "baseline", "TMDL", "", "", null,
                new("test", 1606, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"));
            using var cancellation = new CancellationTokenSource();
            var workspace = new Workspace(root) { CancelDuringApply = cancellation };
            using var edits = new ModelEditService(new Reader(snapshot), workspace); edits.Select(snapshot);
            using var compiler = new ModelConfigurationCompiler();
            var prepared = await compiler.PrepareAsync(new(Measures: []), snapshot, default);
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.EditConfigurationAndSaveAsync(prepared, cancellation.Token,
                    () => throw new InvalidOperationException("已取消无变化准备不能提交")));
                Assert.Equal(1, workspace.Disposals);
                Assert.False(edits.HasPendingChanges);
            }
            finally { compiler.Release(prepared); }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>只规范化 JSON 的真实资产提交可能不改变模型指纹, 提交后取消或撤销不能被错报为无变化失败.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetOnlyCommitDrainsWhenModelFingerprintIsUnchanged(bool revoke)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-bridge-assets-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pbi-utils"));
        var path = Path.Combine(root, "pbi-utils", "config.json");
        File.WriteAllText(path, " { \"schemaVersion\" : 1, \"MeasureTemplates\" : [] } ");
        try
        {
            var snapshot = new ProjectSnapshot("model://bridge-assets-review", "project", "baseline", "TMDL", "", "", null,
                new("test", 1606, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"));
            var reader = new Reader(snapshot);
            var workspace = new Workspace(root) { AssetOnly = true };
            using var compiler = new ModelConfigurationCompiler();
            using var cancellation = new CancellationTokenSource();
            using var bridge = new ModelConfigurationBridge(reader, workspace, compiler, currentWorkspace: workspace);
            using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);
            var context = await bridge.ContextAsync("current", snapshot, default);
            workspace.AfterCommit = () => { if (revoke) bridge.Invalidate(); else cancellation.Cancel(); };

            var input = JsonSerializer.SerializeToElement(new
            {
                context.TargetId,
                snapshot.BaselineId,
                Section = "measures",
                Request = new ModelConfigurationRequest(Measures: [], MeasuresMode: "config", MeasureConfigurationRevision: context.MeasureConfigurationRevision)
            }, JsonSerializerOptions.Web);

            var saved = await bridge.ApplyAsync(input, snapshot, edits, cancellation.Token);

            Assert.False(saved.NoChanges);
            Assert.Equal(snapshot.BaselineId, saved.Project.BaselineId);
            Assert.Equal("{\"schemaVersion\":1,\"MeasureTemplates\":[]}", File.ReadAllText(path));
            Assert.Equal(1, workspace.Saves);
            Assert.False(edits.HasPendingChanges);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>来源读取或无变化暂存期间撤销目标后均拒绝旧结果; 读取阶段撤销还必须避免创建操作上下文.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedTargetDoesNotReturnNoChangeSuccess(bool duringDraft)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-bridge-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new ProjectSnapshot("model://bridge-review", "project", "baseline", "TMDL", "", "", null,
                new("test", 1606, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"));
            var reader = new Reader(snapshot);
            var workspace = new Workspace(root);
            using var compiler = new ModelConfigurationCompiler();
            using var bridge = new ModelConfigurationBridge(reader, workspace, compiler, currentWorkspace: workspace);
            using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);
            var context = await bridge.ContextAsync("current", snapshot, default);
            workspace.Block = !duringDraft;
            workspace.BlockDraft = duringDraft;
            var input = JsonSerializer.SerializeToElement(new
            {
                context.TargetId,
                snapshot.BaselineId,
                Section = "measures",
                Request = new ModelConfigurationRequest(Measures: [], MeasureConfigurationRevision: "missing")
            }, JsonSerializerOptions.Web);
            var pending = bridge.ApplyAsync(input, snapshot, edits, default,
                () => throw new InvalidOperationException("撤销目标不能提交"));
            await workspace.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bridge.Invalidate();
            workspace.Resume.TrySetResult();

            var error = await Assert.ThrowsAsync<AppException>(() => pending);

            Assert.Equal("STALE_CONFIGURATION_TARGET", error.Code);
            Assert.Equal(duringDraft ? 1 : 0, workspace.DraftCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>仅返回给定权威快照, 不访问文件或执行模型操作.</summary>
    private sealed class Reader(ProjectSnapshot snapshot) : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    /// <summary>第二轮来源读取使用显式门闩, 草稿模拟真实无变化路径且永不保存.</summary>
    private sealed class Workspace(string root) : IModelEditWorkspace, IModelConfigurationAssetWorkspace
    {
        public bool Block { get; set; }
        public bool BlockDraft { get; set; }
        public bool AssetOnly { get; set; }
        public Action? AfterCommit { get; set; }
        public CancellationTokenSource? CancelDuringApply { get; set; }
        public int DraftCalls { get; private set; }
        public int Saves { get; private set; }
        public int Disposals { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>返回可创建 missing 配置, 等待时不持有文件句柄且支持取消.</summary>
        public async Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token)
        {
            if (Block) { Entered.TrySetResult(); await Resume.Task.WaitAsync(token); }
            var path = Path.Combine(root, "pbi-utils", "config.json");
            return new(root, new Dictionary<string, string>(), _ => Task.CompletedTask,
                ExistingConfiguration: File.Exists(path) ? TemplateMeasureConfigurationFile.Parse(File.ReadAllBytes(path)) : null,
                CanUseMeasureConfig: true, ConfigurationRevision: File.Exists(path) ? ModelConfigurationOwnership.FileHash(path, token) : "missing");
        }

        /// <summary>计数已进入的操作上下文, 返回不会修改或提交模型的草稿.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        {
            DraftCalls++;
            return Task.FromResult<IModelDraft>(AssetOnly ? new AssetOnlyDraft(baseline, this) : new NoChangeDraft(baseline, this));
        }

        /// <summary>只发布本测试的冻结资产, 随后触发提交后取消/撤销, 不执行模型写入.</summary>
        public void CommitAssets(ModelConfigurationPlan plan)
        {
            var asset = Assert.Single(plan.Assets);
            Assert.NotEqual(asset.BeforeHash, asset.AfterHash);
            UserDataStore.Write(Path.Combine(root, asset.RelativePath), File.ReadAllBytes(asset.SourceFile!));
            Saves++;
            AfterCommit?.Invoke();
        }
    }

    /// <summary>仅文件资产发生变化的受控保存适配器, 完成后模型基线保持而实际文件已更新.</summary>
    private sealed class AssetOnlyDraft(ProjectSnapshot snapshot, Workspace workspace) : IModelDraft
    {
        private ModelConfigurationPlan? plan;
        public ProjectSnapshot Snapshot => snapshot with { HasPendingChanges = true };
        public Task ApplyAsync(ModelEditRequest request, CancellationToken token) { plan = request.Configuration; return Task.CompletedTask; }
        public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
        {
            committing?.Invoke();
            workspace.CommitAssets(plan!);
            return Task.FromResult(snapshot);
        }
        public void Dispose() { }
    }

    /// <summary>复现共享服务无变化即返回的真实分支, 任何 Save 调用都是测试失败.</summary>
    private sealed class NoChangeDraft(ProjectSnapshot snapshot, Workspace workspace) : IModelDraft
    {
        public ProjectSnapshot Snapshot => snapshot;
        public async Task ApplyAsync(ModelEditRequest request, CancellationToken token)
        {
            if (workspace.BlockDraft) { workspace.Entered.TrySetResult(); await workspace.Resume.Task.WaitAsync(token); }
            workspace.CancelDuringApply?.Cancel();
        }
        public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => throw new InvalidOperationException("无变化草稿不能保存");
        public void Dispose() { workspace.Disposals++; }
    }
}
