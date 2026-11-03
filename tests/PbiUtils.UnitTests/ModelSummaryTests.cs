// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelSummaryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共用模型摘要的来源能力、实际零计数和安全序列化边界.

using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只构造内存快照, 不读取用户文件或连接外部服务.</summary>
public sealed class ModelSummaryTests
{
    /// <summary>Desktop、Service 和 SSAS/XMLA 的空页面集合不被标成已读取零页面.</summary>
    /// <param name="kind">当前在线模型来源.</param>
    [Theory]
    [InlineData("desktop")]
    [InlineData("powerbi-service")]
    [InlineData("ssas-xmla")]
    [InlineData("future-online-source")]
    public void OnlineReportPagesRemainUnavailable(string kind)
    {
        var snapshot = Snapshot(new(kind, kind));
        Assert.Null(snapshot.Summary.ReportPageCount);
        Assert.Equal(1600, snapshot.Summary.CompatibilityLevel);
        Assert.Equal(2, snapshot.Summary.TableCount);
        Assert.Equal(2, snapshot.Summary.MeasureCount);
    }

    /// <summary>已实际读取的离线 PBIP 页数区分零页面与多个页面, 旧空来源也保持兼容.</summary>
    /// <param name="explicitSource">是否提供显式 PBIP 来源.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOfflineReportPreservesActualPageCounts(bool explicitSource)
    {
        var snapshot = Snapshot(explicitSource ? new("pbip", "PBIP") : null);
        Assert.Equal(0, snapshot.Summary.ReportPageCount);
        Assert.Equal(2, (snapshot with { Pages = [new("one", "第一页", null, null, []), new("two", "第二页", null, null, [])] }).Summary.ReportPageCount);
    }

    /// <summary>线上旧快照即使缺少来源说明也不能通过空集合宣布零页面.</summary>
    [Fact]
    public void LegacyOnlineHandleDoesNotImplyLoadedReport()
        => Assert.Null((Snapshot() with { ProjectPath = "model://legacy" }).Summary.ReportPageCount);

    /// <summary>架构只读连接的对象计数保留实际返回值, 兼容级别保持未知.</summary>
    [Fact]
    public void LimitedSchemaRetainsReturnedCountsAndUnknownCompatibility()
    {
        var snapshot = Snapshot(new("powerbi-service", "Power BI Service", true));
        Assert.Null(snapshot.Summary.CompatibilityLevel);
        Assert.Equal(2, snapshot.Summary.TableCount);
        Assert.Equal(2, snapshot.Summary.MeasureCount);
        var empty = snapshot with { Model = snapshot.Model with { Tables = [], Measures = [] } };
        Assert.Equal(0, empty.Summary.TableCount);
        Assert.Equal(0, empty.Summary.MeasureCount);
    }

    /// <summary>旧扫描字段保留, 共用摘要把无效兼容级别与未知页面转为 null.</summary>
    /// <param name="compatibilityLevel">扫描返回的占位兼容级别.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ScanSummaryKeepsActualZeroCountsAndUnknownCapabilities(int compatibilityLevel)
    {
        var model = new PowerBiModel("扫描模型", compatibilityLevel, 0, 0);
        var json = JsonSerializer.SerializeToElement(model, JsonSerializerOptions.Web);
        Assert.Equal(compatibilityLevel, json.GetProperty("compatibilityLevel").GetInt32());
        var summary = json.GetProperty("summary");
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("compatibilityLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("reportPageCount").ValueKind);
        Assert.Equal(0, summary.GetProperty("tableCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("measureCount").GetInt32());
    }

    /// <summary>权威快照刷新后摘要随内容变化, 只序列化固定计数字段而不带用户内容.</summary>
    [Fact]
    public void SnapshotSummaryTracksReadbackAndContainsOnlyDisplayCounts()
    {
        var snapshot = Snapshot(new("desktop", "Power BI Desktop"));
        var refreshed = snapshot with { Model = snapshot.Model with { Measures = [] } };
        Assert.Equal(2, snapshot.Summary.MeasureCount);
        Assert.Equal(0, refreshed.Summary.MeasureCount);
        var json = JsonSerializer.SerializeToElement(refreshed, JsonSerializerOptions.Web).GetProperty("summary");
        Assert.Equal(4, json.EnumerateObject().Count());
        Assert.Equal(1600, json.GetProperty("compatibilityLevel").GetInt32());
        Assert.Equal(2, json.GetProperty("tableCount").GetInt32());
        Assert.Equal(0, json.GetProperty("measureCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reportPageCount").ValueKind);
    }

    /// <summary>建立包含隐藏对象的已读取模型快照, 不以对象可见性减少实际数量.</summary>
    /// <param name="connection">当前来源说明; 空值表示既有离线 PBIP 快照.</param>
    /// <returns>无文件访问的固定内存快照.</returns>
    private static ProjectSnapshot Snapshot(ModelConnectionInfo? connection = null)
        => new("fixture.pbip", "project", "baseline", "TMDL", "PBIR", "1.0", "4.0",
            new("模型", 1600, "zh-CN", [new("可见表", null, false, [], []), new("隐藏表", null, true, [], [])],
                [new("可见表", "可见度量值", "1", null, null, null, false), new("隐藏表", "隐藏度量值", "2", null, null, null, true)], [], 0, 0),
            [], [], [], connection);
}
