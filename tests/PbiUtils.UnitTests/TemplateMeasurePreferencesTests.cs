// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasurePreferencesTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板度量配置的增删记忆、空集、损坏保护与并发基线.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Application.TemplateMeasures;
using System.Security.Cryptography;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅操作测试独占用户目录, 不接触真实用户配置.</summary>
public sealed class TemplateMeasurePreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-preferences-" + Guid.NewGuid().ToString("N"));

    /// <summary>增删和关闭条目按完整配置回读, 空配置不会恢复为默认六项.</summary>
    [Fact]
    public void RemembersAddedRemovedDisabledAndEmptyDefinitions()
    {
        var store = new LocalTemplateMeasurePreferencesStore(root);
        var initial = store.Read();
        Assert.Equal(6, initial.Measures.Length);
        TemplateMeasureDefinition[] definitions = [new("custom", "value", "WebUrl", false, "description", "folder")];
        var saved = store.Save(definitions, initial.Baseline, default);
        Assert.Equal(definitions, new LocalTemplateMeasurePreferencesStore(root).Read().Measures);
        store.Save([], saved.Baseline, default);
        Assert.Empty(new LocalTemplateMeasurePreferencesStore(root).Read().Measures);
    }

    /// <summary>其他窗口保存后旧基线不能覆盖其配置.</summary>
    [Fact]
    public void RejectsConcurrentChangesWithoutOverwriting()
    {
        var store = new LocalTemplateMeasurePreferencesStore(root);
        var initial = store.Read();
        store.Save([], initial.Baseline, default);
        Assert.Equal("TEMPLATE_MEASURE_PREFERENCES_CHANGED", Assert.Throws<AppException>(() => store.Save(initial.Measures, initial.Baseline, default)).Code);
        Assert.Empty(store.Read().Measures);
    }

    /// <summary>损坏、未知版本及额外字段原样保留且禁止覆盖.</summary>
    [Theory]
    [InlineData("broken")]
    [InlineData("{\"schemaVersion\":2,\"measures\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"measures\":[],\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"measures\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"measures\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"measures\":[{\"name\":\"old\",\"value\":\"\",\"dataCategory\":\"RefreshTime\",\"enabled\":true,\"description\":\"\",\"displayFolder\":\"\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"measures\":[{\"name\":\"old\",\"value\":\"\",\"dataCategory\":\"RefreshTime\",\"dataCategory\":\"Uncategorized\",\"enabled\":true,\"description\":\"\",\"displayFolder\":\"\"}]}")]
    [InlineData("{\"schemaVersion\":2,\"measures\":[{\"name\":\"old\",\"value\":\"\",\"dataCategory\":\"RefreshTime\",\"enabled\":true,\"description\":\"\",\"displayFolder\":\"\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"measures\":[{\"name\":\"old\",\"value\":\"\",\"dataCategory\":\"RefreshTime\",\"enabled\":true,\"description\":\"\",\"displayFolder\":\"\"},{\"name\":\"unknown\",\"value\":\"custom\",\"dataCategory\":\"Other\",\"enabled\":true,\"description\":\"\",\"displayFolder\":\"\"}]}")]
    public void PreservesUnknownOrCorruptContent(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "template-measure-preferences.json");
        File.WriteAllText(path, content);
        var store = new LocalTemplateMeasurePreferencesStore(root);
        var value = store.Read();
        Assert.NotNull(value.Warning);
        Assert.Equal("INVALID_TEMPLATE_MEASURE_PREFERENCES", Assert.Throws<AppException>(() => store.Save([], value.Baseline, default)).Code);
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>当前配置只读时保持字段、顺序和原字节基线, 后续正常保存仍按完整基线复核.</summary>
    [Fact]
    public void ReadsCurrentCategoriesWithoutWritingUntilExplicitSave()
    {
        TemplateMeasureDefinition[] expected = [new("old_empty", "", "Uncategorized", true, "保留空值", "old"), new("custom", "https://example.org", "WebUrl", false, "保留自定义", "custom"), new("old_fixed", "固定时间文本", "Uncategorized", false, "保留关闭状态", "fixed")];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, measures = expected }, JsonSerializerOptions.Web);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "template-measure-preferences.json");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var modified = File.GetLastWriteTimeUtc(path);
        var store = new LocalTemplateMeasurePreferencesStore(root);
        var value = store.Read();
        Assert.Null(value.Warning);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), value.Baseline);
        Assert.Equal(expected, value.Measures);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var updated = value.Measures.ToArray();
        updated[1] = updated[1] with { Value = "https://updated.example.org" };
        var saved = store.Save(updated, value.Baseline, default);
        Assert.Null(saved.Warning);
        Assert.NotEqual(value.Baseline, saved.Baseline);
        Assert.Equal(updated, store.Read().Measures);
        Assert.DoesNotContain("RefreshTime", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("TEMPLATE_MEASURE_PREFERENCES_CHANGED", Assert.Throws<AppException>(() => store.Save(expected, value.Baseline, default)).Code);
        Assert.Equal(updated, store.Read().Measures);
    }

    /// <summary>当前提交的已移除类别或其变体均严格拒绝.</summary>
    [Theory]
    [InlineData("RefreshTime")]
    [InlineData("refreshtime")]
    [InlineData(" RefreshTime")]
    public void SaveRejectsRemovedCategory(string category)
    {
        var store = new LocalTemplateMeasurePreferencesStore(root);
        var initial = store.Read();
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => store.Save([new("old", "fixed", category, false, "", "")], initial.Baseline, default)).Code);
        Assert.False(File.Exists(Path.Combine(root, "template-measure-preferences.json")));
    }

    /// <summary>取消发生在提交前时不创建配置文件.</summary>
    [Fact]
    public void CancellationDoesNotPublishConfiguration()
    {
        var store = new LocalTemplateMeasurePreferencesStore(root);
        Assert.Throws<OperationCanceledException>(() => store.Save([], store.Read().Baseline, new CancellationToken(true)));
        Assert.False(File.Exists(Path.Combine(root, "template-measure-preferences.json")));
    }

    /// <summary>只释放本测试分配的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
