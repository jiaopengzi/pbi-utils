// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldCalendarMIntegrationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证实际脚手架的日期分区自包含, 不再依赖静态日期文件.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只操作测试独占目录, 元数据回读不等于真实 M 执行.</summary>
public sealed class ScaffoldCalendarMIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-m-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] ExpectedAliasNames = ["C01_Dates", "Year", "日期"];
    private static readonly string[] ExpectedAliasFields = ["Dates", "Dates", "Year"];

    /// <summary>空白模板生成的日期分区均是可复制的 M, 没有文件或网络输入.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task BuiltinDatePartitionGeneratesDatesWithoutStaticCsv(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "calendar", "zh-cn", null, null,
            new("full", 2024, 2024), null, null);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", System.IO.SearchOption.AllDirectories).Single() + "/definition");
        var table = database.Model.Tables["Calendar"];
        var invocation = Assert.IsType<TOM.MPartitionSource>(Assert.Single(table.Partitions).Source).Expression;
        var function = database.Model.Expressions[ScaffoldCalendarWriter.FunctionBinding(table)!.FunctionName];
        var expression = function.Expression;
        Assert.Contains(function.Name, invocation, StringComparison.Ordinal);
        Assert.DoesNotContain("List.Dates", invocation, StringComparison.Ordinal);
        Assert.Contains("List.Dates", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Contents", expression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Web.Contents", expression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DateTime.LocalNow", expression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PbiUtilsProjectRoot", expression, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(stage, "data", "Calendar.csv")));
        Assert.Contains("function-m", table.Annotations.Single(annotation => annotation.Name == "PbiUtils.Configuration.Calendar.v1").Value, StringComparison.Ordinal);
    }

    /// <summary>新日期表按精确选择与编号偏好输出字段, 排序及源模板字节保持.</summary>
    [Theory]
    [InlineData("blank", false)]
    [InlineData("blank", true)]
    public async Task FreshBuiltinUsesSelectedFieldsAndKnownReferences(string kind, bool numbered)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var sourceHash = template.Files.Select(file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Content))).ToArray();
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "calendar", "zh-cn", null, null,
            new("full", 2024, 2024, Fields: ["Dates", "YearMonthM", "YearMonth"], NumberedColumns: numbered), null, null);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", System.IO.SearchOption.AllDirectories).Single() + "/definition");
        var table = database.Model.Tables["Calendar"];
        string[] expected = ["Dates", "YearMonthM", "YearMonth"];
        Assert.Equal(expected.Select(field => ScaffoldCalendarFields.ColumnName(field, numbered)).Order(), table.Columns.Select(column => column.Name).Order());
        Assert.Equal(ScaffoldCalendarFields.ColumnName("YearMonth", numbered), table.Columns[ScaffoldCalendarFields.ColumnName("YearMonthM", numbered)].SortByColumn.Name);
        Assert.All(table.Columns.OfType<TOM.DataColumn>().Where(column => column.DataType == TOM.DataType.DateTime), column => Assert.Equal("Date", column.Annotations["UnderlyingDateTimeDataType"].Value));
        Assert.Equal(sourceHash, template.Files.Select(file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Content))));
    }

    /// <summary>已有非标准语义别名保持实际输出名, 标准生成列随本轮编号覆盖.</summary>
    [Fact]
    public void ExistingAliasesAreKeptAndStandardFieldsFollowNumberingChoice()
    {
        using var database = new TOM.Database { Name = "aliases", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        foreach (var name in new[] { "日期", "C01_Dates" }) table.Columns.Add(new TOM.DataColumn { Name = name, SourceColumn = name, DataType = TOM.DataType.DateTime });
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, "Dates", Fields: ["Dates", "Year"], NumberedColumns: false);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        Assert.Equal(ExpectedAliasNames.Order(), table.Columns.Select(column => column.Name).Order());
        Assert.Equal(ExpectedAliasFields, ScaffoldCalendarWriter.ReadMapping(table).Select(value => value.Field));
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
        ScaffoldCalendarWriter.Write(table, options with { Fields = ["Dates", "Year", "Month"], NumberedColumns = true }, true, null, [], [], default);
        Assert.False(table.Columns.ContainsName("Year")); Assert.True(table.Columns.ContainsName("C13_Month")); Assert.True(table.Columns.ContainsName("C28_Year"));
        Assert.True(table.Columns.ContainsName("日期"));
    }

    /// <summary>两位年标签跨百年重复时不建立非法的多值排序.</summary>
    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    public void YearMonthUsSortRequiresUniqueCentury(int endYear, bool sorted)
    {
        using var database = new TOM.Database { Name = "sort", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" };
        database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("full", 1900, endYear, "Dates", Fields: ["Dates", "YearMonthUS", "YearMonth"]);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        Assert.Equal(sorted, table.Columns["C19_YearMonthUS"].SortByColumn is not null);
    }

    /// <summary>独立日期表也记录角色, 后续追加模拟数据能找到同一表.</summary>
    [Fact]
    public async Task IndependentCalendarHasPersistentRole()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with { Info = builtin.Info with { Id = "user:independent-calendar", Source = "user" } };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, new(1, template.Info.Id, template.Info.Baseline, "calendar", "zh-cn", null, null,
            new("full", 2024, 2024, "Independent", Fields: ["Dates"]), null, null), stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", System.IO.SearchOption.AllDirectories).Single() + "/definition");
        Assert.Equal("calendar", ModelConfigurationOwnership.ReadRoles(database.Model).Roles["Independent"]);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(database.Model.Tables["Independent"]));
        Assert.Equal("PbiUtilsCalendar", ScaffoldCalendarWriter.FunctionBinding(database.Model.Tables["Independent"])!.FunctionName);
        Assert.Single(database.Model.Expressions, expression => expression.Annotations.Find("PbiUtils.Configuration.CalendarFunction.v1") is not null);
    }

    /// <summary>只清理本测试生成的目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
