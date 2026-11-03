// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationCoverageTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实隔离PBIP持续生成模拟数据后日期角色完整覆盖事实和目标月末.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using System.Globalization;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.SampleData;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelConfigurationCoverageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-config-coverage-" + Guid.NewGuid().ToString("N"));

    /// <summary>类型对齐只改已知Date提示, 未重写分区/未知提示/其他业务注解均保持.</summary>
    [Fact]
    public void TypedImportKeepsUnknownDateHintAndOtherAnnotations()
    {
        var table = new TOM.Table { Name = "Owned" };
        var column = new TOM.DataColumn { Name = "Date", SourceColumn = "Date", DataType = TOM.DataType.DateTime };
        column.Annotations.Add(new TOM.Annotation { Name = "UnderlyingDateTimeDataType", Value = "custom-unknown" });
        column.Annotations.Add(new TOM.Annotation { Name = "User.Note", Value = "keep" }); table.Columns.Add(column);
        ModelImportPartition.Set(table, "#table({\"Date\"}, {})");
        Assert.Equal("custom-unknown", column.Annotations["UnderlyingDateTimeDataType"].Value); Assert.Equal("keep", column.Annotations["User.Note"].Value);
        var untouched = new TOM.Table { Name = "Untouched" };
        var prior = new TOM.DataColumn { Name = "Date", SourceColumn = "Date", DataType = TOM.DataType.DateTime };
        prior.Annotations.Add(new TOM.Annotation { Name = "UnderlyingDateTimeDataType", Value = "Date" }); untouched.Columns.Add(prior);
        Assert.Equal("Date", untouched.Columns["Date"].Annotations["UnderlyingDateTimeDataType"].Value);
    }

    /// <summary>工具生成参数/日期类型应在打stamp前匹配Desktop实际保留的定义, 无需忽略业务注解.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task GeneratedParameterAndCalendarMetadataMatchDesktopImportTypes(string kind)
    {
        var (entry, project, _) = await CreateAsync(new("full", 2023, 2023, NumberedColumns: true), kind); var snapshot = await new ProjectReader().ReadAsync(entry, default);
        var modelRoot = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(entry)!, snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism").Path))!;
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"));
        var parameter = database.Model.Expressions["PbiUtilsProjectRoot"];
        Assert.Equal("Text", parameter.Annotations.Find("PBI_ResultType")?.Value);
        var calendar = database.Model.Tables["Calendar"];
        foreach (var table in database.Model.Tables.Where(table => table.Partitions.Count == 1 && table.Partitions[0].Source is TOM.MPartitionSource partition
            && partition.Expression.Contains("Csv.Document", StringComparison.Ordinal) && partition.Expression.Contains("Table.TransformColumnTypes", StringComparison.Ordinal)))
            Assert.All(table.Columns.OfType<TOM.DataColumn>().Where(column => column.DataType == TOM.DataType.DateTime), column => Assert.Null(column.Annotations.Find("UnderlyingDateTimeDataType")));
        Assert.All(calendar.Columns.OfType<TOM.DataColumn>().Where(column => column.DataType == TOM.DataType.DateTime), column => Assert.Equal("Date", column.Annotations.Find("UnderlyingDateTimeDataType")?.Value));
        ModelConfigurationOwnership.Require(calendar, calendar.Annotations); ModelConfigurationOwnership.Require(parameter, parameter.Annotations);
    }

    /// <summary>先用当前模板显式创建 2023 年日期角色, 后追加 2024 年样例须和创建路径一样覆盖所有真实日期.</summary>
    [Fact]
    public async Task AppendedSampleCoversFactsDeliveryRegistrationAndTargetMonthEnd()
    {
        var user = Path.Combine(root, "user"); var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var template = await new ScaffoldTemplateCatalog(user).GetAsync("builtin:blank", default);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template, new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, new("full", 2023, 2023), null, null), project, project, null, default);
        var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile)); var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler(); using var workspace = new ChangeWorkspace(user); using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)), snapshot, default);
        var derived = Assert.Single(prepared.Configuration!.DerivedCalendars);
        Assert.Equal("Calendar", derived.TableName); Assert.True(derived.BeforeEndYear < 2024); Assert.True(derived.Options.EndYear >= 2024);
        Assert.DoesNotContain(prepared.Configuration.Assets, asset => asset.RelativePath == "data/Calendar.csv");
        await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        var days = ScaffoldCalendar.GenerateFields(derived.Options, ["Dates"]).Rows
            .Select(row => DateOnly.ParseExact(row[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet();
        Assert.False(File.Exists(Path.Combine(project, "data", "Calendar.csv")));
        var storage = new SampleCsvStorage(Path.Combine(project, "data")); var count = 0;
        foreach (var (file, columns) in new (string, int[])[] { (SampleTables.FileOrder, [3, 4]), (SampleTables.FileInventory, [4]), (SampleTables.FileCustomer, [5]), (SampleTables.FileSaleTarget, [3]) })
            foreach (var row in storage.Read(file))
                foreach (var column in columns)
                {
                    if (row[column].Length == 0) continue;
                    var date = DateOnly.ParseExact(row[column], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (file == SampleTables.FileSaleTarget) date = new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
                    Assert.Contains(date, days); count++;
                }
        Assert.True(count > 0);
    }

    /// <summary>日期表人工修改或缺失当前选项时不可由模拟配置覆盖.</summary>
    [Theory]
    [InlineData("definition", "CONFIGURATION_OBJECT_CONFLICT")]
    [InlineData("missing-options", "CONFIGURATION_CALENDAR_OPTIONS_REQUIRED")]
    public async Task AppendedSampleRejectsUnprovedCalendar(string change, string code)
    {
        var (entry, project, _) = await CreateAsync(new("full", 2023, 2023, NumberedColumns: true)); var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        var definition = snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism").Path;
        var modelRoot = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(entry)!, definition))!;
        {
            using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"));
            var table = database.Model.Tables["Calendar"];
            if (change == "definition") ((TOM.MPartitionSource)table.Partitions[0].Source).Expression += "\n// manual";
            else
            {
                table.Annotations.Remove(table.Annotations[ScaffoldCalendarWriter.OptionsAnnotation]);
                ModelConfigurationOwnership.Mark(table, table.Annotations);
            }
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, Path.Combine(modelRoot, "definition"));
            snapshot = await reader.ReadAsync(entry, default);
        }
        var modelHash = snapshot.BaselineId;
        using var compiler = new ModelConfigurationCompiler();
        Assert.Equal(code, (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)), snapshot, default))).Code);
        Assert.Equal(modelHash, (await reader.ReadAsync(entry, default)).BaselineId);
        Assert.False(File.Exists(Path.Combine(project, "data", SampleTables.FileOrder)));
    }

    /// <summary>样例派生扩展保留简单日历选项与工作日覆盖, 当前函数无需日期 CSV 资产.</summary>
    [Fact]
    public async Task DerivedCalendarPreservesOptionsWithoutCalendarAssets()
    {
        var options = new ScaffoldCalendarOptions("simple", 2023, 2023, Overrides: [new("2023-12-25", true, "special")]);
        var (entry, project, user) = await CreateAsync(options); var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler(); using var workspace = new ChangeWorkspace(user); using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var request = new ModelConfigurationRequest(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42));
        var prepared = await compiler.PrepareAsync(request, snapshot, default);
        var change = Assert.Single(prepared.Configuration!.DerivedCalendars);
        Assert.DoesNotContain(prepared.Configuration.Assets, asset => asset.RelativePath == "data/Calendar.csv");
        Assert.Equal("simple", change.Options.Kind); Assert.Equal(options.Overrides, change.Options.Overrides);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        var next = await compiler.PrepareAsync(request, snapshot, default); Assert.Empty(next.Configuration!.DerivedCalendars);
        await compiler.VerifyAsync(next, default);
    }

    /// <summary>在测试独占目录使用显式日期选项创建可回读 PBIP; 组合或回读失败向测试传播.</summary>
    /// <param name="calendar">当前测试所需日期表的完整生成选项, 不依赖 blank 自带对象.</param>
    /// <param name="kind">待验证的内置模板目录名, 默认使用无预置表的 blank.</param>
    /// <returns>新建 PBIP 入口、工程目录和隔离用户目录.</returns>
    private async Task<(string Entry, string Project, string User)> CreateAsync(ScaffoldCalendarOptions calendar, string kind = "blank")
    {
        var user = Path.Combine(root, "user"); var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var template = await new ScaffoldTemplateCatalog(user).GetAsync("builtin:" + kind, default);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template, new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, calendar, null, null), project, project, null, default);
        return (Path.Combine(project, composition.EntryFile), project, user);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
