// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureAssetBudgetTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板配置 JSON 转义后的字节预算和非参数角色资产保护.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用测试自有 JSON 与 CSV, 不修改用户项目或连接模型.</summary>
public sealed class TemplateMeasureAssetBudgetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-budget-" + Guid.NewGuid().ToString("N"));

    /// <summary>合法字段经 JSON 转义膨胀后超限, 必须在任何 staging 文件写入前拒绝并保留原文件.</summary>
    [Fact]
    public void EscapedOutputBudgetRejectsBeforeWritingAnyAsset()
    {
        var project = Path.Combine(root, "project");
        var stage = Path.Combine(root, "stage");
        var config = Path.Combine(project, "pbi-utils", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!); Directory.CreateDirectory(stage);
        var before = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"MeasureTemplates":[],"Custom":{"keep":true}}""");
        File.WriteAllBytes(config, before);
        var context = new ModelConfigurationAssetContext(project,
            new Dictionary<string, string> { ["pbi-utils/config.json"] = ModelConfigurationOwnership.Hash(before) }, _ => Task.CompletedTask, CanUseMeasureConfig: true, ConfigurationRevision: ModelConfigurationOwnership.Hash(before));
        var definitions = Definitions(64);
        ScaffoldValidation.Validate(new(1, "fixture", new string('0', 64), "fixture", "zh-cn", definitions, null, null, null, null, MeasuresMode: "config"));
        var error = Assert.Throws<AppException>(() => TemplateMeasureConfigurationStorage.PrepareAssets(stage, context, "config", definitions, default));
        Assert.Equal("CONFIGURATION_ASSET_LIMIT", error.Code);
        Assert.Equal(before, File.ReadAllBytes(config));
        Assert.Empty(Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories));
    }

    /// <summary>预算内的转义配置可直接再读取更新和切回 TMDL, 不产生本工具无法读取的文件.</summary>
    [Fact]
    public void AcceptedEscapedJsonCanAlwaysBeReadAgain()
    {
        var definitions = Definitions(32);
        var bytes = TemplateMeasureConfigurationStorage.JsonBytes(null, "config", definitions)!;
        Assert.InRange(bytes.Length, 1, 8 * 1024 * 1024);
        Assert.Equal(bytes, TemplateMeasureConfigurationStorage.JsonBytes(bytes, "config", definitions));
        Assert.Null(TemplateMeasureConfigurationStorage.JsonBytes(bytes, "tmdl", []));
    }

    /// <summary>没有参数表角色时不按默认文件名管理其他功能的 CSV, 包括有完整通用资产摘要的文件.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedDefaultNamedCsvIsPreserved(bool owned)
    {
        var project = Path.Combine(root, "project");
        var stage = Path.Combine(root, "stage");
        const string relative = "data/config/PbiUtilsTemplateMeasures.csv";
        var path = Path.Combine(project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(stage);
        var bytes = Encoding.UTF8.GetBytes("name,value\nuser,keep\n"); File.WriteAllBytes(path, bytes);
        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (owned) assets[relative] = ModelConfigurationOwnership.Hash(bytes);
        var context = new ModelConfigurationAssetContext(project, assets, _ => Task.CompletedTask);
        var prepared = TemplateMeasureConfigurationStorage.PrepareAssets(stage, context, "tmdl", [], default);
        Assert.DoesNotContain(prepared, asset => asset.RelativePath == relative);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>明确参数角色的旧 CSV 仍按完整 hash 进入删除计划.</summary>
    [Fact]
    public void ExplicitMeasureConfigRoleRetiresItsOwnCsv()
    {
        var project = Path.Combine(root, "project");
        var stage = Path.Combine(root, "stage");
        const string relative = "data/config/Managed.csv";
        var path = Path.Combine(project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(stage);
        File.WriteAllText(path, "name,value\na,b\n");
        var hash = ModelConfigurationOwnership.FileHash(path);
        var context = new ModelConfigurationAssetContext(project, new Dictionary<string, string> { [relative] = hash }, _ => Task.CompletedTask, "Managed");
        var prepared = Assert.Single(TemplateMeasureConfigurationStorage.PrepareAssets(stage, context, "tmdl", [], default));
        Assert.Equal(relative, prepared.RelativePath);
        Assert.Equal(hash, prepared.BeforeHash);
        Assert.Null(prepared.AfterHash);
    }

    /// <summary>构造单项长度和条目数均合法、但会被 JSON 转义放大的输入.</summary>
    private static TemplateMeasureDefinition[] Definitions(int count) => [.. Enumerable.Range(0, count)
        .Select(index => new TemplateMeasureDefinition("item" + index, new string('<', 32768), "Uncategorized", true, "", ""))];

    /// <summary>仅删除本测试创建的随机根目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
