// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteScaffoldTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 MCP 入口正向核验脚手架、示例与日历的文件副作用、回读和可靠回执.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.TemplateMeasures;
using System.Globalization;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using Xunit;
using static PbiUtils.UnitTests.McpWriteScaffoldFixture;

namespace PbiUtils.UnitTests;

/// <summary>全部写入隔离临时工程; 系统目录选择和打开回调受控, 不启动或修改真实 Desktop 模型.</summary>
public sealed class McpWriteScaffoldTests
{
    private static readonly string[] CalendarFields = ["Dates", "Year", "Status"];

    /// <summary>八个新建工具进入真实分发和文件事务, 创建后独立回读并核验原入口身份传递.</summary>
    [Fact]
    public async Task ScaffoldToolsCreateReadBackAndOpenOnlyVerifiedProject()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var selected = await fixture.WriteAsync("scaffold.chooseOutput", new { }, commitStarted: true);
        Assert.Equal(fixture.Output, selected.GetProperty("directory").GetString());
        Assert.False(string.IsNullOrWhiteSpace(selected.GetProperty("selectionId").GetString()));

        var templates = await fixture.WriteAsync("scaffold.openTemplates", new { }, commitStarted: true);
        Assert.True(templates.GetProperty("opened").GetBoolean());
        var templateDirectory = Assert.Single(fixture.OpenedTemplates);
        Assert.Equal(Path.Combine(fixture.UserRoot, "templates", "pbip"), templateDirectory);
        Assert.Contains("Custom PBIP templates", await File.ReadAllTextAsync(Path.Combine(templateDirectory, "readme.txt")), StringComparison.Ordinal);

        var template = await fixture.Catalog.GetAsync("builtin:blank", default);
        const string projectName = "MCP 隔离新建";
        var identity = await fixture.WriteAsync("scaffold.updateDraft", new
        {
            section = "base",
            value = new { templateId = template.Info.Id, templateBaseline = template.Info.Baseline, projectName, locale = "zh-cn", gitInitialize = false }
        });
        Assert.Equal(1, identity.GetProperty("revision").GetInt32());
        identity = await fixture.WriteAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measures",
            value = new[] { new TemplateMeasureDefinition("MCP Result", "MCP actual value", "Uncategorized", true, "MCP file readback", "Test") }
        });
        Assert.Equal(2, identity.GetProperty("revision").GetInt32());
        var plan = (await fixture.WriteAsync("scaffold.preview", identity)).Deserialize<ScaffoldPlan>(JsonSerializerOptions.Web)!;
        var stage = Path.Combine(fixture.Output, ".pbi-utils-scaffold-" + plan.Id);
        Assert.True(Directory.Exists(stage));
        Assert.False(Directory.Exists(plan.OutputPath));
        Assert.NotEmpty(plan.Files);
        Assert.Equal("MCP Result", Assert.Single(new LocalTemplateMeasurePreferencesStore(fixture.UserRoot).Read().Measures).Name);

        var created = (await fixture.WriteAsync("scaffold.create", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            digest = plan.Digest
        }, confirmation =>
        {
            Assert.Contains(plan.Digest, confirmation.GetRawText(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(plan.OutputPath));
        }, commitStarted: true)).Deserialize<ScaffoldResult>(JsonSerializerOptions.Web)!;
        Assert.True(created.ProjectCreated);
        Assert.True(created.Verified);
        Assert.False(Directory.Exists(stage));
        Assert.True(File.Exists(created.ProjectPath));
        var actual = await new ProjectReader().ReadAsync(created.ProjectPath, default);
        Assert.Equal(created.BaselineId, actual.BaselineId);
        Assert.Contains("MCP actual value", Assert.Single(actual.Model.Measures, item => item.Name == "MCP Result").Expression, StringComparison.Ordinal);
        var entryIdentity = ProjectFileIdentity.Read(created.ProjectPath);

        Assert.True((await fixture.WriteAsync("scaffold.openDirectory", new { }, commitStarted: true)).GetProperty("opened").GetBoolean());
        Assert.Equal((created.ProjectPath, entryIdentity), Assert.Single(fixture.OpenedDirectories));
        var opened = await fixture.WriteAsync("scaffold.openDesktop", new { }, commitStarted: true);
        Assert.True(opened.GetProperty("opened").GetBoolean());
        Assert.Equal(actual.BaselineId, opened.GetProperty("project").GetProperty("baselineId").GetString());
        Assert.Equal((created.ProjectPath, entryIdentity), Assert.Single(fixture.OpenedDesktop));
        Assert.Equal(created.ProjectPath, Assert.Single(new LocalRecentProjects(fixture.UserRoot).Load()).Path);
        Assert.Contains(fixture.Messages, message => message.TryGetProperty("workspaceChanged", out var change)
            && change.GetProperty("project").GetProperty("baselineId").GetString() == actual.BaselineId);
        Assert.Equal(actual.BaselineId, (await new ProjectReader().ReadAsync(created.ProjectPath, default)).BaselineId);
    }

    /// <summary>丢弃真实冻结计划只移除该计划 staging, 并持久记录 discarded, 不发布或删除其他文件.</summary>
    [Fact]
    public async Task ScaffoldDiscardRemovesOnlyPreparedFilesAndPersistsDiscardedPlan()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var sentinel = Path.Combine(fixture.Output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        await fixture.WriteAsync("scaffold.chooseOutput", new { });
        var template = await fixture.Catalog.GetAsync("builtin:blank", default);
        var identity = await fixture.WriteAsync("scaffold.updateDraft", new
        {
            section = "base",
            value = new { templateId = template.Info.Id, templateBaseline = template.Info.Baseline, projectName = "Discarded", locale = "en-us", gitInitialize = false }
        });
        var plan = (await fixture.WriteAsync("scaffold.preview", identity)).Deserialize<ScaffoldPlan>(JsonSerializerOptions.Web)!;
        var stage = Path.Combine(fixture.Output, ".pbi-utils-scaffold-" + plan.Id);
        Assert.True(Directory.Exists(stage));
        Assert.True((await fixture.WriteAsync("scaffold.discard", new { digest = plan.Digest })).GetProperty("discarded").GetBoolean());
        Assert.False(Directory.Exists(stage));
        Assert.False(Directory.Exists(plan.OutputPath));
        Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
        var receipt = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(fixture.UserRoot, "scaffold", "plans", plan.Digest + ".json")));
        Assert.Equal("discarded", receipt.GetProperty("state").GetString());
    }

    /// <summary>示例工具真正生成、追加、覆盖并清空 CSV/PBIP; 显式 MCP 操作不弹窗, 其他文件不被清空.</summary>
    [Fact]
    public async Task SampleToolsGenerateIncrementOverwriteAndClearOwnedFiles()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var chosen = await fixture.WriteAsync("sample.chooseDirectory", new { }, commitStarted: true);
        Assert.Equal(fixture.SampleOutput, chosen.GetProperty("outputPath").GetString());
        Assert.False(chosen.GetProperty("hasData").GetBoolean());
        var first = (await fixture.WriteAsync("sample.generate", SampleOptions("full", "2024-01-01", "2024-03-01", 42),
            _ => Assert.False(Directory.Exists(Path.Combine(fixture.SampleOutput, "data"))), true)).Deserialize<SampleDataResponse>(JsonSerializerOptions.Web)!;
        Assert.True(first.State.HasData);
        Assert.NotEmpty(first.State.Tables);
        Assert.All(first.State.Tables, table => Assert.True(table.Size > 0));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.SampleOutput, "data"), "*.csv"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.SampleOutput, "pbip"), "*.pbip", SearchOption.AllDirectories));
        Assert.Empty(fixture.SampleRiskConfirmations);
        Assert.NotNull(first.State.LastFactDate);
        // 增量参数描述独立追加窗口, 起点必须晚于实际订单末日且窗口仍满足至少60天.
        var nextStart = DateOnly.ParseExact(first.State.LastFactDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1);
        var increment = (await fixture.WriteAsync("sample.generate", SampleOptions("incremental",
            nextStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), nextStart.AddDays(90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 42), commitStarted: true))
            .Deserialize<SampleDataResponse>(JsonSerializerOptions.Web)!;
        Assert.True(increment.Result.Orders > 0);
        Assert.True(string.CompareOrdinal(increment.State.LastFactDate, first.State.LastFactDate) > 0);
        var overwrite = (await fixture.WriteAsync("sample.generate", SampleOptions("full", "2024-01-01", "2024-03-01", 43), commitStarted: true))
            .Deserialize<SampleDataResponse>(JsonSerializerOptions.Web)!;
        Assert.True(string.CompareOrdinal(overwrite.State.LastFactDate, increment.State.LastFactDate) < 0);
        Assert.Empty(fixture.SampleRiskConfirmations);
        var reread = await fixture.Samples.GetAsync(fixture.SampleOutput);
        Assert.Equal(overwrite.State.LastFactDate, reread.LastFactDate);
        Assert.Equal(overwrite.State.Tables, reread.Tables);
        Assert.True((await fixture.WriteAsync("sample.openDirectory", new { }, commitStarted: true)).GetProperty("opened").GetBoolean());
        Assert.Equal(fixture.SampleOutput, Assert.Single(fixture.OpenedSamples));
        var sentinel = Path.Combine(fixture.SampleOutput, "data", "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated file");
        var cleared = await fixture.WriteAsync("sample.clear", new { }, commitStarted: true);
        Assert.False(cleared.GetProperty("hasData").GetBoolean());
        Assert.False((await fixture.Samples.GetAsync(fixture.SampleOutput)).HasData);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.SampleOutput, "data"), "*.csv"));
        Assert.False(Directory.Exists(Path.Combine(fixture.SampleOutput, "pbip")));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(fixture.SampleRiskConfirmations);
    }

    /// <summary>日历偏好经 MCP 精确参数写入后持久化, 新实例与只读工具回读同一字段及编号配置.</summary>
    [Fact]
    public async Task CalendarPreferencesPersistThroughMcpAndIndependentReadback()
    {
        await using var fixture = new McpWriteScaffoldFixture();
        var result = await fixture.WriteAsync("calendar.savePreferences", new { fields = CalendarFields, numberedColumns = false },
            _ => Assert.False(File.Exists(Path.Combine(fixture.UserRoot, "calendar-preferences.json"))), true);
        var persisted = new LocalCalendarPreferencesStore(fixture.UserRoot).Read();
        Assert.Equal(["Dates", "Year", "Status"], persisted.Fields);
        Assert.False(persisted.NumberedColumns);
        var reread = Json(await fixture.Session.InvokeMcpAsync("calendar.preferences", Json(new { }), default) ?? throw new InvalidOperationException());
        Assert.True(JsonElement.DeepEquals(result, reread));
        Assert.True(File.Exists(Path.Combine(fixture.UserRoot, "calendar-preferences.json")));
    }

    /// <summary>返回小而非空的业务数据范围, 不向工具提交目录或授权字段.</summary>
    private static object SampleOptions(string mode, string startDate, string endDate, int seed) => new
    {
        mode,
        locale = "zh-cn",
        productCount = 2,
        storeCount = 2,
        inventoryCycle = 7,
        startDate,
        endDate,
        seed
    };
}
