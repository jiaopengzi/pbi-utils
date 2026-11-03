// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopConfigurationAssetFileReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实分步文件操作重现末检后外部写入, 不在产品注入暂停钩子.

using PbiUtils.Windows.Models.Editing;
using System.Text;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopConfigurationAssetFileReviewTests
{
    /// <summary>输出槽已核实后外部抢先创建固定目标, 发布必须拒绝覆盖并保留双方文件.</summary>
    [Fact]
    public void PublishCannotOverwriteLateExternalFile()
    {
        using var fixture = new Fixture();
        var output = fixture.Files.Stage(Encoding.UTF8.GetBytes("ours"));
        File.WriteAllText(fixture.Target, "external");

        Assert.Throws<IOException>(() => fixture.Files.Publish(output));

        Assert.Equal("external", File.ReadAllText(fixture.Target));
        Assert.Equal("ours", File.ReadAllText(fixture.Files.StagePath));
    }

    /// <summary>末检后外部替换原目标, 原子移出返回真实新身份, 可在空目标下完整归还它.</summary>
    [Theory]
    [InlineData("external")]
    [InlineData("old")]
    public void RetainingAfterExternalReplacementPreservesActualFile(string externalContent)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Target, "old");
        var expected = DesktopConfigurationAssetFile.Read(fixture.Target)!;
        var external = fixture.ReplaceTarget(externalContent);

        var captured = fixture.Files.Retain();

        Assert.NotEqual(expected.Identity, captured.Identity);
        Assert.Equal(external, captured);
        fixture.Files.RestoreHeld(captured);
        Assert.Equal(external, DesktopConfigurationAssetFile.Read(fixture.Target));
        Assert.Equal(externalContent, File.ReadAllText(fixture.Target));
    }

    /// <summary>保留原文件后外部抢先占用目标, 发布与归还都禁止覆盖, 三份内容全部保留.</summary>
    [Fact]
    public void ExternalCreationBetweenRetainAndPublishPreservesAllThreeFiles()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Target, "old");
        var output = fixture.Files.Stage(Encoding.UTF8.GetBytes("ours"));
        var held = fixture.Files.Retain();
        File.WriteAllText(fixture.Target, "external");

        Assert.Throws<IOException>(() => fixture.Files.Publish(output));
        Assert.Throws<IOException>(() => fixture.Files.RestoreHeld(held));

        Assert.Equal("external", File.ReadAllText(fixture.Target));
        Assert.Equal("ours", File.ReadAllText(fixture.Files.StagePath));
        Assert.Equal("old", File.ReadAllText(fixture.Files.HeldPath));
    }

    /// <summary>回滚末检后固定目标同字节换身份时, 实际移出文件必须归还, 不能当作本轮输出删除.</summary>
    [Fact]
    public void RetirementCapturesSameBytesExternalReplacementWithoutDeletingIt()
    {
        using var fixture = new Fixture();
        var output = fixture.Files.Stage(Encoding.UTF8.GetBytes("ours"));
        fixture.Files.Publish(output);
        Assert.Equal(output, DesktopConfigurationAssetFile.Read(fixture.Target));
        var external = fixture.ReplaceTarget("ours");

        var captured = fixture.Files.RetireOutput();

        Assert.NotEqual(output.Identity, captured.Identity);
        fixture.Files.ReturnStage(captured);
        Assert.Equal(external, DesktopConfigurationAssetFile.Read(fixture.Target));
        Assert.False(File.Exists(fixture.Files.StagePath));
    }

    /// <summary>私有槽也必须以卷/文件身份清理, 同字节新文件不能被删除.</summary>
    [Fact]
    public void SlotCleanupPreservesSameBytesExternalReplacement()
    {
        using var fixture = new Fixture();
        var output = fixture.Files.Stage(Encoding.UTF8.GetBytes("ours"));
        var external = Path.Combine(Path.GetDirectoryName(fixture.Target)!, "external");
        File.WriteAllText(external, "ours");
        File.Move(external, fixture.Files.StagePath, true);

        Assert.Throws<PbiUtils.Core.AppException>(() => fixture.Files.DeleteStage(output));

        Assert.Equal("ours", File.ReadAllText(fixture.Files.StagePath));
    }

    /// <summary>每例只创建并删除自身随机临时根, 不接触真实工程.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-asset-file-review-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(root, "config.json");
        internal DesktopConfigurationAssetFile Files { get; }
        internal Fixture() { Directory.CreateDirectory(root); Files = new(Target, Guid.NewGuid().ToString("N")); }
        /// <summary>原子放入另一文件身份, 真实复现同字节ABA和不同字节外部修改.</summary>
        internal DesktopConfigurationAssetState ReplaceTarget(string content)
        {
            var external = Path.Combine(root, "external");
            File.WriteAllText(external, content); File.Move(external, Target, true);
            return DesktopConfigurationAssetFile.Read(Target)!;
        }
        public void Dispose() { Directory.Delete(root, true); }
    }
}
