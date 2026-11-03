// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarPreferencesTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证日期字段偏好的严格持久化、损坏保护和提交边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>独立日期方案必须经过共享服务及宿主存储.</summary>
public sealed class CalendarPreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-preferences-" + Guid.NewGuid().ToString("N"));

    /// <summary>新草稿默认推荐方案, 只持久化字段与编号并按目录顺序回读.</summary>
    [Fact]
    public void OnlyUserFieldSchemePersistsAcrossStoreInstances()
    {
        var service = new CalendarPreferencesService(new LocalCalendarPreferencesStore(root));
        Assert.Equal(ScaffoldCalendarFields.Recommended, service.Read().Fields);
        Assert.True(service.Read().NumberedColumns);
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(root);
        var unrelated = Path.Combine(root, "other.json");
        File.WriteAllText(unrelated, "unchanged");
        var saved = service.Save(["Status", "Dates", "Year"], false, default);
        Assert.Equal(["Dates", "Year", "Status"], saved.Fields);
        Assert.False(saved.NumberedColumns);
        var reread = new LocalCalendarPreferencesStore(root).Read();
        Assert.Equal(saved.Fields, reread.Fields);
        Assert.False(reread.NumberedColumns);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "calendar-preferences.json")));
        Assert.Equal(["schemaVersion", "fields", "numberedColumns"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("unchanged", File.ReadAllText(unrelated));
    }

    /// <summary>损坏、未来版本、重复属性/字段及未知字段不得被默认值覆盖.</summary>
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":2,\"fields\":[\"Dates\"],\"numberedColumns\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"fields\":[\"Dates\"],\"numberedColumns\":true}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[\"Dates\",\"Dates\"],\"numberedColumns\":true}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[\"Dates\",\"FutureField\"],\"numberedColumns\":true}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[\"Year\"],\"numberedColumns\":true}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[\"Dates\"],\"numberedColumns\":\"true\"}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[\"Dates\"],\"numberedColumns\":true,\"target\":\"anything\"}")]
    public void InvalidStoredConfigurationIsPreserved(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "calendar-preferences.json");
        File.WriteAllText(path, content);
        var store = new LocalCalendarPreferencesStore(root);
        Assert.NotNull(store.Read().Warning);
        Assert.Equal("INVALID_CALENDAR_PREFERENCES", Assert.Throws<AppException>(() => store.Save(["Dates"], false, default)).Code);
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>非法输入、提交前取消、锁冲突和原子替换故障保留旧文件.</summary>
    [Fact]
    public void FailedSaveAndCancellationPreserveBaseline()
    {
        var service = new CalendarPreferencesService(new LocalCalendarPreferencesStore(root));
        service.Save(["Dates", "Year"], true, default);
        var path = Path.Combine(root, "calendar-preferences.json");
        var baseline = File.ReadAllBytes(path);
        foreach (string[] invalid in new string[][] { [], ["Year"], ["Dates", "Dates"], ["Dates", " dates"], ["Dates", "unknown"] })
            Assert.Throws<AppException>(() => service.Save(invalid, false, default));
        Assert.ThrowsAny<OperationCanceledException>(() => service.Save(["Dates"], false, new CancellationToken(true)));
        using (UserDataStore.Lock(Path.Combine(root, "calendar-preferences.lock")))
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => service.Save(["Dates"], false, default)).Code);
        Assert.Throws<InvalidOperationException>(() => service.Save(["Dates"], false, default, () => throw new InvalidOperationException("before-write")));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => service.Save(["Dates"], false, default)) is IOException or UnauthorizedAccessException);
        Assert.Equal(baseline, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>提交阶段触发取消仍返回实际持久化结果, 不报告未写入.</summary>
    [Fact]
    public void CommitDrainsAfterCancellation()
    {
        var store = new LocalCalendarPreferencesStore(root);
        using var cancellation = new CancellationTokenSource();
        var actual = store.Save(["Dates", "Year"], false, cancellation.Token, cancellation.Cancel);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(actual.Fields, store.Read().Fields);
        Assert.False(store.Read().NumberedColumns);
    }

    /// <summary>超长偏好文件有界拒读且拒写, 原文件保持不变.</summary>
    [Fact]
    public void OversizedPreferenceFileIsPreserved()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "calendar-preferences.json");
        var content = new string(' ', 8193);
        File.WriteAllText(path, content);
        var store = new LocalCalendarPreferencesStore(root);
        Assert.NotNull(store.Read().Warning);
        Assert.Throws<AppException>(() => store.Save(["Dates"], true, default));
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>只删除本测试明确创建的临时目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
