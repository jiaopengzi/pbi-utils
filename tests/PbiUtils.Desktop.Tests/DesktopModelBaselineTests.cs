// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopModelBaselineTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : Desktop工作区基线与配置落盘证明忽略服务端推导信息但拒绝真实定义变化.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopModelBaselineTests
{
    /// <summary>所选表缺失仍需完整定义一致, 不能漏掉关系或保留表的外部更改后宣布落盘完成.</summary>
    [Fact]
    public void SelectedTableDeletionReadbackRequiresAbsenceAndFullDefinition()
    {
        using var original = Create();
        using var expected = Create();
        expected.Model.Tables.Remove(expected.Model.Tables["T"]);
        ModelEditRequest[] edits = [new("table.deleteBatch", "tables", "items", JsonSerializer.Serialize(new TableDeleteBatch("p", "b", ["T"]), JsonSerializerOptions.Web))];
        Assert.False(DesktopFileSync.EditsPersisted(ModelSnapshotMapper.Map(expected), ModelSnapshotMapper.Map(original), edits));
        original.Model.Tables.Remove(original.Model.Tables["T"]);
        var fingerprint = ModelConfigurationReadback.Fingerprint(expected);
        Assert.True(DesktopFileSync.EditsPersistedWithDefinition(ModelSnapshotMapper.Map(expected), ModelSnapshotMapper.Map(original), edits, fingerprint, ModelConfigurationReadback.Fingerprint(original)));
        original.Model.Annotations.Add(new TOM.Annotation { Name = "ExternalChange", Value = "must-not-ignore" });
        Assert.True(DesktopFileSync.EditsPersisted(ModelSnapshotMapper.Map(expected), ModelSnapshotMapper.Map(original), edits));
        Assert.False(DesktopFileSync.EditsPersistedWithDefinition(ModelSnapshotMapper.Map(expected), ModelSnapshotMapper.Map(original), edits, fingerprint, ModelConfigurationReadback.Fingerprint(original)));
    }

    [Fact]
    public void WorkspaceBaselineRemainsStableAcrossServerRuntimeNormalization()
    {
        using var disk = Create(); using var live = Runtime(disk);
        var method = typeof(ConnectedModelReader).GetMethod("DesktopDefinitionFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(method.Invoke(null, [disk]), method.Invoke(null, [live]));
        ((TOM.MPartitionSource)live.Model.Tables["T"].Partitions[0].Source).Expression = "#table({\"V\"}, {{99}})";
        Assert.NotEqual(method.Invoke(null, [disk]), method.Invoke(null, [live]));
    }

    [Fact]
    public void ExactConfigurationDefinitionOverridesOnlyInferredDtoShapeDifferences()
    {
        using var disk = Create(); using var live = Runtime(disk);
        var fingerprint = ModelConfigurationReadback.Fingerprint(disk); var liveFingerprint = ModelConfigurationReadback.Fingerprint(live);
        Assert.Equal(fingerprint, liveFingerprint);
        var expected = ModelSnapshotMapper.Map(live); var actual = ModelSnapshotMapper.Map(disk);
        ModelEditRequest[] edits = [new("configuration.apply", "configuration", "config", "digest")];
        Assert.False(DesktopFileSync.EditsPersisted(expected, actual, edits));
        Assert.True(DesktopFileSync.EditsPersistedWithDefinition(expected, actual, edits, liveFingerprint, fingerprint));
        Assert.False(DesktopFileSync.EditsPersistedWithDefinition(expected, actual, edits, null, fingerprint));
        ((TOM.MPartitionSource)disk.Model.Tables["T"].Partitions[0].Source).Expression = "#table({\"V\"}, {{99}})";
        Assert.False(DesktopFileSync.EditsPersistedWithDefinition(expected, actual, edits, liveFingerprint, ModelConfigurationReadback.Fingerprint(disk)));
    }

    [Fact]
    public void MixedEditsStillVerifyOrdinaryPropertiesAfterFullConfigurationProof()
    {
        using var disk = Create(); using var expected = Create(); expected.Model.Tables["T"].Description = "required";
        ModelEditRequest[] edits = [new("configuration.apply", "configuration", "config", "digest"), new("table.set", "T", "description", "required")];
        var fingerprint = ModelConfigurationReadback.Fingerprint(disk);
        Assert.False(DesktopFileSync.EditsPersistedWithDefinition(ModelSnapshotMapper.Map(expected), ModelSnapshotMapper.Map(disk), edits, fingerprint, fingerprint));
    }

    /// <summary>即使函数缺失已回读, lint 的精确锁删除历史也必须写入同一完整文件定义.</summary>
    [Fact]
    public void UdfLintReadbackRequiresExactRemovalHistory()
    {
        using var expected = Create(); using var stale = Create();
        expected.Model.Annotations.Add(new TOM.Annotation { Name = "PbiUtils.Configuration.UdfLintRemoved.v1", Value = "accurate-history" });
        ModelEditRequest[] edits = [new("udf.deleteUnused", "UDF", "items", JsonSerializer.Serialize(new UdfDeleteBatch("p", "b", ["Local.Removed"]), JsonSerializerOptions.Web))];
        var expectedModel = ModelSnapshotMapper.Map(expected); var staleModel = ModelSnapshotMapper.Map(stale);
        Assert.True(DesktopFileSync.EditsPersisted(expectedModel, staleModel, edits));
        Assert.False(DesktopFileSync.EditsPersistedWithDefinition(expectedModel, staleModel, edits, ModelConfigurationReadback.Fingerprint(expected), ModelConfigurationReadback.Fingerprint(stale)));
        stale.Model.Annotations.Add(new TOM.Annotation { Name = "PbiUtils.Configuration.UdfLintRemoved.v1", Value = "accurate-history" });
        Assert.True(DesktopFileSync.EditsPersistedWithDefinition(expectedModel, ModelSnapshotMapper.Map(stale), edits, ModelConfigurationReadback.Fingerprint(expected), ModelConfigurationReadback.Fingerprint(stale)));
    }

    private static TOM.Database Create() => TOM.JsonSerializer.DeserializeDatabase("""{"name":"isolated","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"T","columns":[{"name":"V","sourceColumn":"V","dataType":"int64"}],"partitions":[{"name":"T","mode":"import","source":{"type":"m","expression":"#table({\"V\"}, {{42}})"}}]}]}}""");

    private static TOM.Database Runtime(TOM.Database source)
    {
        var json = JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(source))!;
        var table = json["model"]!["tables"]![0]!; table["modifiedTime"] = "2026-10-17T11:19:32";
        table["columns"]![0]!["attributeHierarchy"] = JsonNode.Parse("{\"state\":\"ready\",\"modifiedTime\":\"2026-10-17T11:19:32\"}");
        ((JsonArray)table["columns"]!).Add(JsonNode.Parse("{\"type\":\"rowNumber\",\"name\":\"RowNumber-owned\",\"dataType\":\"int64\",\"isHidden\":true,\"isUnique\":true,\"isNullable\":false}"));
        return TOM.JsonSerializer.DeserializeDatabase(json.ToJsonString());
    }
}
