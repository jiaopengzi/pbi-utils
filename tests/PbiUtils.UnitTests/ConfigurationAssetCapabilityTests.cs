// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ConfigurationAssetCapabilityTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 模板度量资产能力探测不阻断无关配置浏览, 真实目标变化仍拒绝.

using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ConfigurationAssetCapabilityTests
{
    /// <summary>模板度量被人工修改时仅撤销 Config 能力, 日期和 UDF 所需的中性上下文仍可读.</summary>
    [Theory]
    [InlineData("CONFIGURATION_OBJECT_CONFLICT")]
    [InlineData("CONFIGURATION_OWNERSHIP_INVALID")]
    public async Task InvalidMeasureOwnershipDoesNotBlockOtherConfigurationSections(string code)
    {
        var workspace = new Workspace(code);
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(), workspace, compiler, currentWorkspace: workspace);

        var context = await bridge.ContextAsync("current", Snapshot(), default);

        Assert.True(context.CanWrite);
        Assert.False(context.CanUseMeasureConfig);
        Assert.False(context.CanGenerateSample);
    }

    /// <summary>真实身份或完整基线变化不是能力缺失, 必须原样失败而非显示可写旧目标.</summary>
    [Theory]
    [InlineData("STALE_BASELINE")]
    [InlineData("DESKTOP_FILE_CHANGED")]
    public async Task IdentityOrBaselineChangesStillFailContext(string code)
    {
        var workspace = new Workspace(code);
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(new Reader(), workspace, compiler, currentWorkspace: workspace);

        var error = await Assert.ThrowsAsync<AppException>(() => bridge.ContextAsync("current", Snapshot(), default));

        Assert.Equal(code, error.Code);
    }

    /// <summary>构造没有磁盘路径的宿主 Desktop 展示快照, 不接入真实模型.</summary>
    private static ProjectSnapshot Snapshot() => new("model://test", "project", "baseline", "test", "", "", null,
        new("model", 1606, "zh-CN", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"));

    /// <summary>只读上下文测试不允许意外读取产品文件.</summary>
    private sealed class Reader : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>用精确业务错误模拟资产能力探测, 意外创建编辑草稿立即失败.</summary>
    private sealed class Workspace(string code) : IModelEditWorkspace, IModelConfigurationAssetWorkspace
    {
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new NotSupportedException();
        public Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token) =>
            throw new AppException(code, "expected capability boundary", 3);
    }
}
