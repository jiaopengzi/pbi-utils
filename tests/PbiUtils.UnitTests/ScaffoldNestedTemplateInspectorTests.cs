// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldNestedTemplateInspectorTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证实际新建工程复用为用户模板后, 检查和日期预览按嵌套 PBIP 入口解析绑定.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用本轮生成的独立工程和真实模板目录, 不模拟报表或模型路径解析.</summary>
public sealed class ScaffoldNestedTemplateInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-nested-template-inspector-" + Guid.NewGuid().ToString("N"));
    private string UserRoot => Path.Combine(root, "user");

    /// <summary>目录接受的生成工程必须也能读取模型字段, 不能把 PBIP 内相对报表路径解释为模板根路径.</summary>
    [Fact]
    public async Task InspectGeneratedUserTemplateResolvesReportFromNestedEntry()
    {
        var (catalog, template) = await CreateGeneratedUserTemplateAsync();
        var inspector = new ScaffoldTemplateInspector(catalog, UserRoot);
        var details = await inspector.InspectAsync(template.Info.Id, template.Info.Baseline, default);
        Assert.Equal("Calendar", details.DefaultDateTable);
        Assert.Equal("Dates", details.DefaultDateColumn);
        var table = Assert.Single(details.Tables);
        Assert.Equal("Calendar", table.Name);
        Assert.Equal(["Dates", "Year"], table.Columns.Select(column => column.Name));
        Assert.Empty(Directory.GetDirectories(Path.Combine(UserRoot, "scaffold", "inspect")));
        Assert.Equal(template.Info.Baseline, (await catalog.GetAsync(template.Info.Id, default)).Info.Baseline);
    }

    /// <summary>嵌套用户模板的纯内存预览读取同一真实模型, 不产生检查目录或修改源文件.</summary>
    [Fact]
    public async Task PreviewGeneratedUserTemplateReadsNestedFrozenModelWithoutWritingFiles()
    {
        var (catalog, template) = await CreateGeneratedUserTemplateAsync();
        var inspector = new ScaffoldTemplateInspector(catalog, UserRoot);
        var preview = await inspector.PreviewCalendarAsync(template.Info.Id, template.Info.Baseline, Options(), "zh-cn", default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(UserRoot, "templates", "pbip", "Generated", "pbip", "Generated.SemanticModel", "definition"));
        Assert.Equal(database.Model.Expressions["PbiUtilsCalendar"].Expression, preview.FunctionExpression);
        Assert.Equal(Assert.IsType<TOM.MPartitionSource>(Assert.Single(database.Model.Tables["Calendar"].Partitions).Source).Expression, preview.InvocationExpression);
        Assert.False(Directory.Exists(Path.Combine(UserRoot, "scaffold", "inspect")));
        Assert.Equal(template.Info.Baseline, (await catalog.GetAsync(template.Info.Id, default)).Info.Baseline);
    }

    /// <summary>把真实创建结果直接放入独立用户模板目录, 返回同一目录重新发现并冻结的模板.</summary>
    private async Task<(ScaffoldTemplateCatalog Catalog, ResolvedPbipTemplate Template)> CreateGeneratedUserTemplateAsync()
    {
        var catalog = new ScaffoldTemplateCatalog(UserRoot);
        var source = await catalog.GetAsync("builtin:blank", default);
        var project = Path.Combine(UserRoot, "templates", "pbip", "Generated");
        Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(source,
            new(1, source.Info.Id, source.Info.Baseline, "Generated", "zh-cn", null, null, Options(), null, null), project, project, null, default);
        Assert.Equal("pbip/Generated.pbip", composition.EntryFile);
        var info = (await catalog.ListAsync(default)).Single(entry => entry.Source == "user");
        Assert.NotEmpty(info.Baseline);
        return (catalog, await catalog.GetAsync(info.Id, default));
    }

    /// <summary>固定两个日期字段和无编号名称, 让断言来自独立且明确的生成选项.</summary>
    private static ScaffoldCalendarOptions Options() => new("simple", 2024, 2024, Fields: ["Dates", "Year"], NumberedColumns: false);

    /// <summary>仅移除本测试随机创建的临时工程.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
