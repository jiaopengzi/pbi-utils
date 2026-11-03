// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PbipProjectLayoutTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 PBIP 子目录布局的整树副本, 配置资产保存, 直接保存及并发保护.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用独立临时工程验证离线事务, 不打开 Desktop 或用户模型.</summary>
public sealed class PbipProjectLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-layout-" + Guid.NewGuid().ToString("N"));
    private string ProjectRoot => Path.Combine(root, "project");
    private string Entry => Path.Combine(ProjectRoot, "pbip", "测试.pbip");
    private string Asset => Path.Combine(ProjectRoot, "data", "fixture.csv");
    private readonly ProjectReader reader = new();

    /// <summary>创建 PBIP 子目录及同级资产, 其内容和路径均由本测试拥有.</summary>
    public PbipProjectLayoutTests()
    {
        PbipFixture.Create(Path.Combine(ProjectRoot, "pbip"));
        Directory.CreateDirectory(Path.GetDirectoryName(Asset)!);
        File.WriteAllText(Asset, "before asset");
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "config"));
        File.WriteAllText(Path.Combine(ProjectRoot, "config", "custom.json"), "{\"keep\":true}");
    }

    /// <summary>副本保留根目录全部资产和 PBIP 相对入口, 不改写原工程或资产根参数.</summary>
    [Fact]
    public async Task CopyPreservesNestedEntryAndSiblingAssets()
    {
        var before = await reader.ReadAsync(Entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        var output = Path.Combine(root, "copy");
        var plan = await workspace.PrepareAsync(new(Entry, output, "table.set", "销售", "description", "copy"), default);
        var result = await workspace.PublishAsync(plan, default);
        Assert.Equal(Path.Combine(output, "pbip", "测试.pbip"), result.ProjectPath);
        Assert.Equal("before asset", File.ReadAllText(Path.Combine(output, "data", "fixture.csv")));
        Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(output, "config", "custom.json")));
        Assert.Equal("copy", (await reader.ReadAsync(result.ProjectPath, default)).Model.Tables.Single().Description);
        Assert.Equal(before.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
        Assert.Contains(plan.Changes, change => change.Path == "pbip/model/model.bim");
    }

    /// <summary>内存草稿直接保存当前项目根并保留同级资产, 不创建历史备份, 回读清单仍相对于 PBIP 父目录.</summary>
    [Fact]
    public async Task SaveUpdatesWholeRootAndKeepsSnapshotPathsRelativeToPbipDirectory()
    {
        var before = await reader.ReadAsync(Entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(before);
        await edits.StageAsync(new("table.set", "销售", "description", "saved"), default);
        var saved = await edits.SaveAsync(default);
        Assert.Equal(Entry, saved.ProjectPath);
        Assert.Equal("saved", (await reader.ReadAsync(Entry, default)).Model.Tables.Single().Description);
        Assert.Contains(saved.Files, file => file.Path == "model/model.bim");
        Assert.DoesNotContain(saved.Files, file => file.Path.StartsWith("pbip/", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        Assert.Equal(ProjectRoot, PbipProjectLayout.ProjectRoot(saved.ProjectPath));
        Assert.Equal("before asset", File.ReadAllText(Asset));
        Assert.True(File.Exists(Entry));
        Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(ProjectRoot, "config", "custom.json")));
        Assert.False(Directory.Exists(Path.Combine(ProjectRoot, "pbip", "data")));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
    }

    /// <summary>冻结配置的资产根是项目根, 模型与同级 CSV 由一个原地事务发布.</summary>
    [Fact]
    public async Task ConfigurationPublishesSiblingAssetInSameInPlaceTransaction()
    {
        var snapshot = await reader.ReadAsync(Entry, default);
        var frozen = Path.Combine(root, "frozen.csv");
        File.WriteAllText(frozen, "after asset");
        using var configuration = new ModelConfigurationPlan(snapshot.BaselineId, "fixture", new(), new([], []),
            [new("data/fixture.csv", frozen, Hash(File.ReadAllBytes(Asset)), Hash(File.ReadAllBytes(frozen)), new FileInfo(frozen).Length)], ProjectRoot);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        var plan = await workspace.PrepareAsync(new(Entry, ProjectRoot, "configuration.apply", "configuration", "config", "fixture", InPlace: true, Configuration: configuration), default);
        Assert.Contains(plan.Changes, change => change.Path == "data/fixture.csv");
        var result = await workspace.PublishAsync(plan, default);
        Assert.Equal(Entry, result.ProjectPath);
        Assert.Equal("after asset", File.ReadAllText(Asset));
        Assert.False(Directory.Exists(Path.Combine(ProjectRoot, "pbip", "data")));
    }

    /// <summary>准备后外部仅修改同级资产也使冻结副本失效, 不能发布过期整树内容.</summary>
    [Fact]
    public async Task SiblingAssetChangedAfterPreparationRejectsCopy()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        var output = Path.Combine(root, "copy");
        var plan = await workspace.PrepareAsync(new(Entry, output, "table.set", "销售", "description", "copy"), default);
        File.WriteAllText(Asset, "external asset");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => workspace.PublishAsync(plan, default))).Code);
        Assert.False(Directory.Exists(output));
        Assert.Equal("external asset", File.ReadAllText(Asset));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
    }

    /// <summary>只提升直接名为 pbip 的父目录, 不搜索其他同名祖先或猜测用户目录用途.</summary>
    /// <param name="directoryName">入口父目录的实际名称.</param>
    /// <param name="promoted">是否应将固定父级作为项目根.</param>
    [Theory]
    [InlineData("pbip", true)]
    [InlineData("PBIP", true)]
    [InlineData("pbip-other", false)]
    [InlineData("custom", false)]
    public void RootUsesOnlyFixedImmediateParent(string directoryName, bool promoted)
    {
        var container = Path.Combine(root, "pbip", "fixed");
        var directory = Path.Combine(container, directoryName);
        var entry = PbipFixture.Create(directory);
        Assert.Equal(promoted ? container : directory, PbipProjectLayout.ProjectRoot(entry));
    }

    /// <summary>入口父级被替换为目录链接后不能据其可见路径扩大事务或打开目录范围.</summary>
    [Fact]
    public async Task RootRejectsLinkedAncestor()
    {
        var link = Path.Combine(root, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, ProjectRoot }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, ProjectRoot);
        try
        {
            var error = Assert.Throws<AppException>(() => PbipProjectLayout.ProjectRoot(Path.Combine(link, "pbip", "测试.pbip")));
            Assert.Equal("UNSAFE_PROJECT_PATH", error.Code);
        }
        finally { Directory.Delete(link); }
        Assert.True(File.Exists(Entry));
    }

    /// <summary>冻结资产使用与产品一致的完整 SHA256 文件指纹.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>仅清理本测试创建的唯一临时树.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
