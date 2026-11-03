// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\TemplateMeasureDesktopPathTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证官方 PBIR 绑定的 PBIP 根解析保持配置目录与 pbip 平级.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>只读核实本测试生成的实际 PBIP/PBIR, 不连接或控制任何 Desktop 进程.</summary>
public sealed class TemplateMeasureDesktopPathTests
{
    /// <summary>盘符根可准备安全资产计划, 本测试只在随机临时备份目录写回执, 从不应用或写入盘符根资产.</summary>
    [Fact]
    public void DriveRootCanPrepareConfigurationTransactionWithoutApplyingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-template-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.csv"); File.WriteAllBytes(source, [1]);
            var relative = "data/config/" + Guid.NewGuid().ToString("N") + ".csv";
            var volume = Path.GetPathRoot(root)!;
            using var transaction = new DesktopConfigurationAssetTransaction(volume, Path.Combine(root, "transactions", "model"), "before", "after",
                [new(relative, source, null, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1])), 1)]);
            transaction.Rollback();
            Assert.False(File.Exists(Path.Combine(volume, relative)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>官方报告文件精确匹配 pbip 内入口, 资产根提升到同级目录而不落入报表或 pbip 内.</summary>
    [Fact]
    public async Task BoundReportKeepsTemplateConfigurationBesidePbip()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-template-desktop-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
            var result = await new ScaffoldModelWriter().ComposeAsync(template,
                new(1, template.Info.Id, template.Info.Baseline, "Bound", "zh-cn", [new("bound_value", "v", "Uncategorized", true, "", "")],
                    null, null, null, null, MeasuresMode: "config"), root, root, null, default);
            var entry = Path.Combine(root, result.EntryFile);
            var report = Path.Combine(root, "pbip", "Bound.Report", "definition.pbir");

            await DesktopPendingQueries.RequireNoneAsync(new(42, DateTime.UnixEpoch, report, false), default);

            Assert.Equal(report, DesktopModelFileIdentity.Read(entry).ReportPath);
            Assert.Equal(root, PbipProjectLayout.ProjectRoot(entry));
            Assert.True(File.Exists(Path.Combine(root, "pbi-utils", "config.json")));
            Assert.False(Directory.Exists(Path.Combine(root, "pbip", "pbi-utils")));
        }
        finally { Directory.Delete(root, true); }
    }
}
