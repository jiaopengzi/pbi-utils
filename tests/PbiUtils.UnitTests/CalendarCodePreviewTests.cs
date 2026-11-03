// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarCodePreviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证可信日期代码预览与真实模板写入一致、只读和严格协议边界.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Host.Bridge.Calendar;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarCodePreviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-preview-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前空白模板的预览与最终函数/调用逐字一致, 预览自身不创建临时目录.</summary>
    [Theory]
    [InlineData("blank", "zh-cn", true)]
    [InlineData("blank", "en-us", true)]
    [InlineData("blank", "zh-cn", false)]
    [InlineData("blank", "en-us", false)]
    public async Task FrozenTemplatePreviewMatchesWrittenProgramWithoutCreatingFiles(string kind, string locale, bool explicitFields)
    {
        var user = Path.Combine(root, "user");
        var catalog = new ScaffoldTemplateCatalog(user);
        var template = await catalog.GetAsync("builtin:" + kind, default);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, Fields: explicitFields ? ["Dates", "YearMonthM", "YearMonth"] : null, NumberedColumns: false);
        var inspector = new ScaffoldTemplateInspector(catalog, user);
        var preview = await inspector.PreviewCalendarAsync(template.Info.Id, template.Info.Baseline, options, locale, default);
        Assert.False(Directory.Exists(root));
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, new(1, template.Info.Id, template.Info.Baseline, "preview", locale,
            null, null, options, null, null), stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single(), "definition"));
        var function = database.Model.Expressions[preview.FunctionName];
        Assert.Equal(preview.FunctionExpression, function.Expression);
        Assert.Equal("functions", function.QueryGroup?.Name);
        Assert.Contains("Documentation.Examples", function.Expression, StringComparison.Ordinal);
        Assert.Equal(preview.InvocationExpression, ((TOM.MPartitionSource)database.Model.Tables["Calendar"].Partitions[0].Source).Expression);
        await Assert.ThrowsAsync<AppException>(() => inspector.PreviewCalendarAsync(template.Info.Id, new string('0', 64), options, locale, default));
    }

    /// <summary>仅受信模板副本的显式日期角色可首次转换并保留其他原始查询; 当前模型不继承模板转换资格.</summary>
    [Fact]
    public async Task ExplicitUserTemplateRolePreviewMatchesCreatedFunction()
    {
        using var source = BlankTemplateFixture.CalendarModel();
        var originalFunction = source.Model.Expressions["UserFunction"].Expression;
        var template = await BlankTemplateFixture.WithModelAsync(root, source, calendarRole: true);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024);
        var inspector = new ScaffoldTemplateInspector(new FrozenCatalog(template), Path.Combine(root, "user"));
        var preview = await inspector.PreviewCalendarAsync(template.Info.Id, template.Info.Baseline, options, "en-us", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, new(1, template.Info.Id, template.Info.Baseline, "preview", "en-us",
            null, null, options, null, null), stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single(), "definition"));
        Assert.Equal(preview.FunctionExpression, database.Model.Expressions[preview.FunctionName].Expression);
        Assert.Equal(preview.InvocationExpression, ((TOM.MPartitionSource)database.Model.Tables[options.TableName].Partitions[0].Source).Expression);
        Assert.Equal(originalFunction, database.Model.Expressions["UserFunction"].Expression);
    }

    /// <summary>已有生成日期表的用户模板请求另一张独立表时, 预览和组合共同拒绝且保留原定义.</summary>
    [Fact]
    public async Task ExistingGeneratedCalendarRejectsSecondBindingBeforeWriting()
    {
        using var source = BlankTemplateFixture.CalendarModel();
        var calendar = source.Model.Tables["Calendar"];
        ScaffoldCalendarWriter.Write(calendar, new("simple", 2024, 2024), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(calendar, calendar.Annotations);
        var template = await BlankTemplateFixture.WithModelAsync(root, source);
        var before = template.Files.Select(file => file.Content.ToArray()).ToArray();
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "CustomDates");
        var inspector = new ScaffoldTemplateInspector(new FrozenCatalog(template), root);
        var preview = await Assert.ThrowsAsync<AppException>(() => inspector.PreviewCalendarAsync(template.Info.Id, template.Info.Baseline, options, "zh-cn", default));
        Assert.Equal("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", preview.Code);
        var stage = Path.Combine(root, "stage");
        var compose = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "preview", "zh-cn", null, null, options, null, null), stage, Path.Combine(root, "output"), null, default));
        Assert.Equal(preview.Code, compose.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
        for (var index = 0; index < before.Length; index++) Assert.Equal(before[index], template.Files[index].Content);
        using var original = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single(), "definition"));
        Assert.Null(original.Model.Tables.Find("CustomDates"));
        Assert.NotNull(original.Model.Tables.Find("Calendar"));
    }

    private sealed class FrozenCatalog(ResolvedPbipTemplate template) : IPbipTemplateCatalog
    {
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PbipTemplateInfo>>([template.Info]);
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(template);
    }

    /// <summary>完整私有TOM支持真实源列别名预览, 只读调用不追加编辑或保存.</summary>
    [Fact]
    public async Task ModelPreviewUsesRealSourceNamesWithoutChangingDraft()
    {
        var entry = PbipFixture.Create(root);
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var database = new TOM.Database { Name = "preview", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        table.Columns.Add(new TOM.DataColumn { Name = "日期", SourceColumn = "Real date", DataType = TOM.DataType.DateTime });
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates", Fields: ["Dates", "Year"], NumberedColumns: false);
        // 首次转换按已知模型列语义解释, 真实源列名保持独立.
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        using var draft = new MemoryModelDraft(snapshot, database.Clone(), (_, _, _) => throw new InvalidOperationException("预览不得保存"));
        var before = draft.Snapshot;
        var program = await draft.PreviewCalendarAsync(options, "en-us", default);
        Assert.Contains("\"Real date\"", program.FunctionExpression, StringComparison.Ordinal);
        Assert.Same(before, draft.Snapshot);
        Assert.False(draft.Snapshot.HasPendingChanges);
    }

    /// <summary>原连接对象保留引擎已经推导的实际日期类型, 只读预览不能先复制为 Unknown 类型再误拒绝.</summary>
    [Fact]
    public void OriginalModelPreviewKeepsInferredTypesAndObjectIdentity()
    {
        using var database = new TOM.Database { Name = "preview-original", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates", Fields: ["Dates", "Year"], NumberedColumns: false);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        table.Columns["Dates"].IsDataTypeInferred = true;
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var originalColumn = table.Columns["Dates"];
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var program = CalendarProgramPlanner.Preview(database, options, "en-us", default);
        Assert.Equal(ScaffoldCalendarM.FunctionName, program.FunctionName);
        Assert.Same(table, database.Model.Tables["Dates"]);
        Assert.Same(originalColumn, table.Columns["Dates"]);
        Assert.Equal(TOM.DataType.DateTime, originalColumn.DataType);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>预览输入拒绝路径、任意映射、上下文混用、未知字段及非法语言.</summary>
    [Theory]
    [InlineData("{\"kind\":\"model\",\"targetId\":\"id\",\"baselineId\":\"BASELINE\",\"path\":\"secret\"}", "zh-cn")]
    [InlineData("{\"kind\":\"template\",\"templateId\":\"builtin:blank\",\"templateBaseline\":\"BASELINE\",\"targetId\":\"id\"}", "zh-cn")]
    [InlineData("{\"kind\":\"model\",\"targetId\":\"id\",\"baselineId\":\"BASELINE\"}", "invalid")]
    public void StrictPreviewInputRejectsUntrustedCapabilities(string context, string locale)
    {
        using var parsed = JsonDocument.Parse("{\"context\":" + context.Replace("BASELINE", new string('0', 64), StringComparison.Ordinal)
            + ",\"calendar\":{\"kind\":\"simple\",\"startYear\":2024,\"endYear\":2024},\"locale\":\"" + locale + "\"}");
        Assert.Throws<AppException>(() => CalendarPreviewInput.Parse(parsed.RootElement));
    }

    /// <summary>只清理本测试独占目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
