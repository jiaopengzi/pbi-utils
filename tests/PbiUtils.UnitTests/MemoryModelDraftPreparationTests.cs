// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\MemoryModelDraftPreparationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 当前日期预览与失败配置只在私有候选中执行, 保留权威模型与草稿边界.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

public sealed class MemoryModelDraftPreparationTests
{
    /// <summary>只读日期预览不改变现有业务草稿或权威模型, 并保留已修改的当前快照.</summary>
    [Fact]
    public async Task CurrentCalendarPreviewKeepsPrivateDraftAndAuthorityUntouched()
    {
        using var database = Create();
        var before = ModelConfigurationReadback.Fingerprint(database);
        using var draft = Draft(database.Clone());
        await draft.ApplyAsync(new("table.set", "Existing", "description", "changed"), default);
        var snapshot = draft.Snapshot;
        var program = await draft.PreviewCalendarAsync(new("simple", 2026, 2026, "NewDates", Fields: ["Dates"]), "zh-cn", default);
        Assert.Equal(ScaffoldCalendarM.FunctionName, program.FunctionName);
        Assert.Same(snapshot, draft.Snapshot);
        Assert.True(snapshot.HasPendingChanges);
        Assert.Equal("changed", snapshot.Model.Tables.Single().Description);
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(database));
    }

    /// <summary>日期预览失败或取消不建立待保存状态, 原始模型完整指纹保持.</summary>
    [Fact]
    public async Task FailedOrCancelledCalendarPreviewDoesNotChangeDraft()
    {
        using var database = Create();
        var before = ModelConfigurationReadback.Fingerprint(database);
        using var draft = Draft(database.Clone());
        var snapshot = draft.Snapshot;
        await Assert.ThrowsAsync<PbiUtils.Core.AppException>(() => draft.PreviewCalendarAsync(new("simple", 2027, 2026, "NewDates"), "zh-cn", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draft.PreviewCalendarAsync(new("simple", 2026, 2026, "NewDates"), "zh-cn", new(true)));
        Assert.Same(snapshot, draft.Snapshot);
        Assert.False(draft.Snapshot.HasPendingChanges);
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(database));
    }

    /// <summary>夹具只创建不含真实数据源的内存模型.</summary>
    private static TOM.Database Create() => TOM.JsonSerializer.DeserializeDatabase("""{"name":"isolated","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Existing","description":"before","columns":[{"name":"Value","sourceColumn":"Value","dataType":"int64"}]}]}}""");

    /// <summary>草稿只持有独占克隆, 意外保存必须使测试失败.</summary>
    private static MemoryModelDraft Draft(TOM.Database database)
    {
        var baseline = new ProjectSnapshot("model://isolated", "identity", ModelConfigurationReadback.Fingerprint(database), "online", "", "", null,
            ModelSnapshotMapper.Map(database), [], [], [], new("desktop", "Power BI Desktop"));
        return new(baseline, database, (_, _, _) => throw new InvalidOperationException("unexpected save"));
    }
}
