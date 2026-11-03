// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldModelTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前空白与最小用户模板真实组合, 独立本地源和模型保留边界.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using System.Text;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅在测试自有目录组合, 不向引擎执行公式.</summary>
public sealed class ScaffoldModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-model-" + Guid.NewGuid().ToString("N"));

    /// <summary>跳过可选配置保留模板结构, 空白模型不隐式生成日期表, 报表字节保持且排除本机缓存.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task BuiltinWithoutSampleIsSelfContainedAndPreservesReport(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var stage = Path.Combine(root, "stage");
        var output = Path.Combine(root, "final#project");
        Directory.CreateDirectory(stage);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, output, null, default);
        var snapshot = await new ProjectReader().ReadAsync(Path.Combine(stage, result.EntryFile), default);
        Assert.Equal(1606, result.CompatibilityLevel);
        Assert.Empty(snapshot.Model.Tables);
        Assert.Empty(result.Tables);
        Assert.DoesNotContain(template.Files, file => file.RelativePath.EndsWith("/.pbi/cache.abf", StringComparison.Ordinal)
            || file.RelativePath.EndsWith("/.pbi/localSettings.json", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(stage, "data", "sample")));
        Assert.All(SampleTables.AllFiles, file => Assert.False(File.Exists(Path.Combine(stage, "data", file))));
        Assert.False(File.Exists(Path.Combine(stage, "pbi-utils.template.json")));
        foreach (var file in template.Files.Where(f => f.RelativePath.Contains(".Report/", StringComparison.Ordinal)
            && !f.RelativePath.EndsWith("/definition.pbir", StringComparison.Ordinal)))
            Assert.Equal(file.Content, await File.ReadAllBytesAsync(Path.Combine(stage, "pbip", "project.Report",
                file.RelativePath[(file.RelativePath.IndexOf(".Report/", StringComparison.Ordinal) + ".Report/".Length)..])));
        using var db = ReadDatabase(stage);
        var sources = string.Join('\n', db.Model.Expressions.Select(e => e.Expression)
            .Concat(db.Model.Tables.SelectMany(t => t.Partitions).Select(p => (p.Source as TOM.MPartitionSource)?.Expression ?? "")));
        Assert.DoesNotContain("Web.Contents", sources, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Access.Database", sources, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baidu", sources, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", sources.Replace(output.Replace("#", "#(#)", StringComparison.Ordinal), "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(output.Replace("#", "#(#)", StringComparison.Ordinal), db.Model.Expressions["PbiUtilsProjectRoot"].Expression);
        Assert.DoesNotContain(stage, db.Model.Expressions["PbiUtilsProjectRoot"].Expression);
    }

    /// <summary>样例经真实流式生成, 原始 CSV 与共享生成器一致且直接绑定模型.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task SamplePopulatesCurrentIndependentMappings(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        var request = Request(template) with { Sample = new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42) };
        var result = await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        Assert.False(Directory.Exists(Path.Combine(stage, "data", "sample")));
        var expected = Path.Combine(root, "shared-sample");
        _ = new SampleDataGenerator(request.Sample!, SampleResources.Load()).GenerateAll(new SampleCsvStorage(expected));
        foreach (var file in SampleTables.AllFiles)
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(expected, file)), await File.ReadAllBytesAsync(Path.Combine(stage, "data", file)));
        using (var database = ReadDatabase(stage))
        {
            var product = database.Model.Tables["T00_产品表"];
            var productPath = "/data/T00_产品表.csv";
            Assert.Contains(productPath, ((TOM.MPartitionSource)product.Partitions[0].Source).Expression, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(stage, "data", "model", "T00_产品表.csv")));
        }
        Assert.Contains(result.Tables, t => t.Name == "T00_产品表" && t.RowCount == 2);
        _ = await new ProjectReader().ReadAsync(Path.Combine(stage, result.EntryFile), default);
    }

    /// <summary>新建时只替换 staging 的固定同名模拟文件, 冻结模板字节保持原样.</summary>
    [Fact]
    public async Task SampleGenerationReplacesStagedDataWithoutChangingSourceTemplate()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var bytes = Encoding.UTF8.GetBytes("user-owned-source");
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:fixture", Source = "user" },
            Files = [.. builtin.Files.Where(file => file.RelativePath != "pbi-utils.template.json"), new("data/" + SampleTables.FileProduct, bytes)]
        };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = Request(template) with { Sample = new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42) };
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        Assert.NotEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(stage, "data", SampleTables.FileProduct)));
        Assert.Equal(bytes, template.Files.Single(file => file.RelativePath == "data/" + SampleTables.FileProduct).Content);
        Assert.Equal("user-owned-source", Encoding.UTF8.GetString(bytes));
        Assert.False(Directory.Exists(Path.Combine(root, "final")));
    }

    /// <summary>全部字段保留, 完整 SVG URI 不重复添加前缀, 同表同名按用户配置覆盖.</summary>
    [Fact]
    public async Task MeasureFieldsRoundTripAndSameTableCollisionOverwrites()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        var request = Request(template) with { Measures = [new("author", "Jane", "Uncategorized", true, "说明", "资料"), new("logo", "data:image/svg+xml;utf8,<svg/>", "ImageUrl", true, "图", "图片"), new("disabled", "ignored", "WebUrl", false, "", "")] };
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        using var db = ReadDatabase(stage);
        Assert.Equal("说明", db.Model.Tables["Measure"].Measures["author"].Description);
        Assert.Equal("资料", db.Model.Tables["Measure"].Measures["author"].DisplayFolder);
        Assert.Equal("ImageUrl", db.Model.Tables["Measure"].Measures["logo"].DataCategory);
        Assert.Equal("\n\"data:image/svg+xml;utf8,<svg/>\"", db.Model.Tables["Measure"].Measures["logo"].Expression);
        Assert.DoesNotContain(db.Model.Tables["Measure"].Measures, m => m.Name == "disabled");
        template = template with
        {
            Files = [.. template.Files,
            new("blank.SemanticModel/definition/tables/Measure.tmdl", Encoding.UTF8.GetBytes("table Measure\n\tmeasure ExistingValue = 1\n"))]
        };
        var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Measures = [new("ExistingValue", "x", "Uncategorized", true, "", "")] }, other, Path.Combine(root, "new"), null, default);
        using var overwritten = ReadDatabase(other);
        Assert.Equal("\n\"x\"", overwritten.Model.Tables["Measure"].Measures["ExistingValue"].Expression);
    }

    /// <summary>空白工程的新简单日期表按当前默认五字段输出, 不增加未请求的日期列.</summary>
    [Fact]
    public async Task SimpleCalendarDefaultsUseCurrentFields()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Calendar = new("simple", 2024, 2024) }, stage, Path.Combine(root, "new"), null, default);
        using var database = ReadDatabase(stage);
        var expected = ScaffoldCalendarFields.Simple.Select(field => ScaffoldCalendarFields.ColumnName(field, true));
        Assert.Equal(expected.Order(), database.Model.Tables["Calendar"].Columns.Select(column => column.Name).Order());
        Assert.Equal(366, Assert.Single(result.Tables).RowCount);
    }

    /// <summary>全部51项与依赖实际进入模型, 不删除原文件夹内容.</summary>
    [Fact]
    public async Task AllPeriodsRoundTripAndRetainOriginalMeasures()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with
        {
            Files = [.. builtin.Files, new("blank.SemanticModel/definition/tables/Measure.tmdl",
                Encoding.UTF8.GetBytes("table Measure\n\tmeasure 0000measure = 1\n"))]
        };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = Request(template) with
        {
            Calendar = new("full", 2024, 2024, NumberedColumns: true),
            Periods = new("Calendar", "C01_Dates", "Measure", ["0000measure"], [.. ScaffoldTimeIntelligence.Templates.Select(t => t.Id)])
        };
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        using var db = ReadDatabase(stage);
        Assert.Equal(52, db.Model.Tables["Measure"].Measures.Count);
        Assert.Equal("1", db.Model.Tables["Measure"].Measures["0000measure"].Expression.Trim());
    }

    /// <summary>本地函数提升输出兼容级别并冻结基线, 源模板继续1606.</summary>
    [Fact]
    public async Task LocalUdfIsFrozenAndDoesNotMutateSource()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var source = template.Files.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Content))).ToArray();
        var local = new LocalUdfStore(root);
        var entry = await local.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "说明", []), default);
        using var sources = new UdfSourceProvider(root, _ => throw new InvalidOperationException("Local-only resolution must not request HTTP"));
        var provider = sources.DefaultProvider;
        var resolver = new UdfDependencyResolver(provider, local);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = Request(template) with
        {
            Measures = [new("ExistingValue", "value", "Uncategorized", true, "", "")],
            Udfs = [new("local", entry.Id, "1.0.0", ["Local.One"], Baseline: entry.Baseline)]
        };
        var result = await new ScaffoldModelWriter(resolver).ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        using var db = ReadDatabase(stage);
        Assert.Equal(1702, db.CompatibilityLevel);
        Assert.Equal("() => 1", db.Model.Functions["Local.One"].Expression);
        Assert.Equal(entry.Baseline, Assert.Single(result.UdfFunctions!).Baseline);
        Assert.Equal("Local.One", Assert.Single(ModelConfigurationUdfLocks.FromModel(db.Model).Functions).Function.Name);
        Assert.False(File.Exists(Path.Combine(stage, "config", "udf-lock.json")));
        Assert.False(Directory.Exists(Path.Combine(stage, "config", "licenses")));
        Assert.Equal(source, template.Files.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Content))));
    }

    /// <summary>小预算在写入前拒绝, 取消令牌不会被组合忽略.</summary>
    [Fact]
    public async Task BudgetAndCancellationFailBeforePublishing()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var error = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter(maximumBytes: 100).ComposeAsync(template, Request(template), stage, Path.Combine(root, "final"), null, default));
        Assert.Equal("SCAFFOLD_SIZE_LIMIT", error.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, Path.Combine(root, "final"), null, new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(root, "final")));
    }

    /// <summary>选择模拟不改未映射用户 M 和日期表, 仅追加独立样例模型.</summary>
    [Fact]
    public async Task UserTemplateKeepsOriginalMAndAddsOnlyIndependentTables()
    {
        using var source = BlankTemplateFixture.CalendarModel();
        var calendarExpression = ((TOM.MPartitionSource)source.Model.Tables["Calendar"].Partitions[0].Source).Expression;
        var userExpression = source.Model.Expressions["UserFunction"].Expression;
        var template = await BlankTemplateFixture.WithModelAsync(root, source);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Sample = new("zh-cn", 1, 1, 7, "2024-01-01", "2024-04-01", 42) }, stage, Path.Combine(root, "final"), null, default);
        using var database = ReadDatabase(stage);
        Assert.Equal(calendarExpression, ((TOM.MPartitionSource)database.Model.Tables["Calendar"].Partitions[0].Source).Expression);
        Assert.Equal(userExpression, database.Model.Expressions["UserFunction"].Expression);
        Assert.False(ModelConfigurationOwnership.IsOwned(database.Model.Tables["Calendar"], database.Model.Tables["Calendar"].Annotations));
        foreach (var file in SampleTables.AllFiles) Assert.True(database.Model.Tables.ContainsName(Path.GetFileNameWithoutExtension(file)));
    }

    /// <summary>跨年送货是实际日期角色, 默认日历须覆盖真实文件而非只看订单参数.</summary>
    [Fact]
    public async Task DefaultCalendarCoversActualDeliveryDatesAcrossYear()
    {
        var template = await BlankTemplateFixture.GeneratedCalendarAsync(root, new("full", 2023, 2023));
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Sample = new("zh-cn", 2, 10, 7, "2024-01-01", "2024-12-31", 42) }, stage, Path.Combine(root, "final"), null, default);
        using var database = ReadDatabase(stage);
        var options = ScaffoldCalendarWriter.ReadOptions(database.Model.Tables["Calendar"]);
        var calendar = ScaffoldCalendar.GenerateFields(options, ["Dates"]).Rows.Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("List.Dates", database.Model.Expressions[ScaffoldCalendarWriter.FunctionBinding(database.Model.Tables["Calendar"])!.FunctionName].Expression, StringComparison.Ordinal);
        var deliveries = ReadCsv(stage, "T04_订单主表").Select(row => row["F_04_送货日期"]).ToArray();
        Assert.Contains(deliveries, date => date.StartsWith("2025", StringComparison.Ordinal));
        foreach (var date in deliveries) Assert.Contains(date, calendar);
    }

    /// <summary>选定范围不足时预览明确拒绝, 不给出已知不完整的日期关系.</summary>
    [Fact]
    public async Task ExplicitCalendarRejectsActualOutOfRangeDelivery()
    {
        var template = await BlankTemplateFixture.GeneratedCalendarAsync(root, new("full", 2023, 2023));
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = Request(template) with { Sample = new("zh-cn", 2, 10, 7, "2024-01-01", "2024-12-31", 42), Calendar = new("full", 2024, 2024) };
        var error = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default));
        Assert.Equal("SCAFFOLD_CALENDAR_COVERAGE", error.Code);
    }

    /// <summary>模型名称不能作为磁盘路径, 非文件名字符只改变安全CSV文件名.</summary>
    [Fact]
    public async Task CalendarNameCannotEscapeOutputDirectory()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with { Info = builtin.Info with { Id = "user:custom-calendar", Source = "user" } };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var name = "../../other/calendar";
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Calendar = new("simple", 2024, 2024, name) }, stage, Path.Combine(root, "final"), null, default);
        using var db = ReadDatabase(stage);
        Assert.True(db.Model.Tables.ContainsName(name));
        Assert.Equal("PbiUtilsCalendar", ScaffoldCalendarWriter.FunctionBinding(db.Model.Tables[name])!.FunctionName);
        Assert.Single(db.Model.Expressions, expression => expression.Annotations.Find("PbiUtils.Configuration.CalendarFunction.v1") is not null);
        Assert.False(Directory.Exists(Path.Combine(root, "other")));
        Assert.Empty(Directory.GetFiles(stage, "table-*.csv", System.IO.SearchOption.AllDirectories));
    }

    /// <summary>目录读取器允许UTF8 BOM, 组合器对同一有效绑定保持一致.</summary>
    [Fact]
    public async Task UserTemplateSupportsBomJsonBindings()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:fixture", Source = "user" },
            Files = builtin.Files.Where(f => f.RelativePath != "pbi-utils.template.json")
            .Select(file => file.RelativePath.EndsWith(".pbip", StringComparison.Ordinal) || file.RelativePath.EndsWith(".pbir", StringComparison.Ordinal) || file.RelativePath.EndsWith(".json", StringComparison.Ordinal)
                ? file with { Content = [0xef, 0xbb, 0xbf, .. file.Content] } : file).ToArray()
        };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, Path.Combine(root, "final"), null, default);
        _ = await new ProjectReader().ReadAsync(Path.Combine(stage, result.EntryFile), default);
    }

    /// <summary>用户模板显式日期角色仅替换该分区, 其他任意 M 查询按原字节保留.</summary>
    [Fact]
    public async Task ExplicitUserCalendarRoleCanBeReplacedWithoutChangingOtherM()
    {
        using var source = BlankTemplateFixture.CalendarModel();
        var userExpression = source.Model.Expressions["UserFunction"].Expression;
        var template = await BlankTemplateFixture.WithModelAsync(root, source, calendarRole: true);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Calendar = new("full", 2024, 2024) }, stage, Path.Combine(root, "final"), null, default);
        using var database = ReadDatabase(stage);
        Assert.Equal(366, Assert.Single(result.Tables).RowCount);
        Assert.Contains("List.Dates", database.Model.Expressions[ScaffoldCalendarWriter.FunctionBinding(database.Model.Tables["Calendar"])!.FunctionName].Expression, StringComparison.Ordinal);
        Assert.Equal(userExpression, database.Model.Expressions["UserFunction"].Expression);
    }

    /// <summary>实际CSV写入中触发预算和取消仍不创建最终目录, 进度不报告100%.</summary>
    [Fact]
    public async Task StreamingBudgetAndMidGenerationCancellationAreEnforced()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var copied = template.Files.Where(f => f.RelativePath != "pbi-utils.template.json").Sum(f => (long)f.Content.Length);
        var limit = copied + 30_000;
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var error = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter(maximumBytes: limit).ComposeAsync(template, Request(template) with { Sample = new("zh-cn", 2, 3, 7, "2024-01-01", "2024-04-01", 42) }, stage, Path.Combine(root, "final"), null, default));
        Assert.Equal("SCAFFOLD_SIZE_LIMIT", error.Code);
        Assert.False(File.Exists(Path.Combine(stage, "data", "Calendar.csv")));
        Assert.True(Directory.EnumerateFiles(stage, "*", System.IO.SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= limit);
        using var cancellation = new CancellationTokenSource();
        var percentages = new List<double>();
        var progress = new ProgressAction(value => { percentages.Add(value.Percent); if (value.Percent >= 30) cancellation.Cancel(); });
        var cancelled = Path.Combine(root, "cancelled"); Directory.CreateDirectory(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScaffoldModelWriter().ComposeAsync(template,
            Request(template) with { Sample = new("zh-cn", 2, 3, 7, "2024-01-01", "2024-04-01", 42) }, cancelled, Path.Combine(root, "final"), progress, cancellation.Token));
        Assert.NotEmpty(percentages); Assert.All(percentages, value => Assert.True(value < 100));
        Assert.False(Directory.Exists(Path.Combine(root, "final")));
    }

    /// <summary>简单日期的已有五列不引入未请求的农历范围限制.</summary>
    [Theory]
    [InlineData(1999, "none")]
    [InlineData(2100, "none")]
    [InlineData(1900, "gregorian")]
    [InlineData(1999, "gregorian")]
    [InlineData(2100, "gregorian")]
    [InlineData(1999, "lunar")]
    public async Task ExistingSimpleCalendarDoesNotRequireLunarCoverage(int year, string compatibility)
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var options = new ScaffoldCalendarOptions("simple", year, year, "Calendar");
        var calendar = ScaffoldCalendar.Generate(options);
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model { Culture = "en-US" } };
        var table = new TOM.Table { Name = "Calendar" };
        for (var i = 0; i < calendar.Columns.Length; i++) table.Columns.Add(new TOM.DataColumn { Name = calendar.Columns[i], SourceColumn = calendar.Columns[i], DataType = Enum.Parse<TOM.DataType>(calendar.DataTypes[i]) });
        if (compatibility != "none")
        {
            var full = ScaffoldCalendar.Generate(new("full", 2024, 2024));
            var indices = compatibility == "gregorian" ? Enumerable.Range(0, 55).Append(62) : [56];
            foreach (var i in indices.Where(index => !table.Columns.ContainsName(full.Columns[index])))
                table.Columns.Add(new TOM.DataColumn { Name = full.Columns[i], SourceColumn = full.Columns[i], DataType = Enum.Parse<TOM.DataType>(full.DataTypes[i]) });
        }
        table.Partitions.Add(new TOM.Partition { Name = "Calendar", Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table({}, {})" } });
        database.Model.Tables.Add(table);
        var descriptor = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, tables = new[] { new { name = "Calendar", role = "calendar", columns = table.Columns.Cast<TOM.DataColumn>().Select(c => new { name = c.Name, dataType = c.DataType.ToString(), sourceColumn = c.SourceColumn }) } } });
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:simple", Source = "user" },
            EntryFile = "test.pbip",
            Files = [new("test.pbip", Encoding.UTF8.GetBytes("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"report\"}}]}")),
                new("report/definition.pbir", Encoding.UTF8.GetBytes("{\"version\":\"4.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../model\"}}}")),
                new("report/definition/report.json", Encoding.UTF8.GetBytes("{}")),
                new("report/definition/pages/pages.json", Encoding.UTF8.GetBytes("{\"pageOrder\":[]}")),
                new("model/definition.pbism", Encoding.UTF8.GetBytes("{\"version\":\"1.0\"}")),
                new("model/model.bim", Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(database))),
                new("pbi-utils.template.json", Encoding.UTF8.GetBytes(descriptor))]
        };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        if (compatibility == "lunar")
        {
            var error = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Calendar = options }, stage, Path.Combine(root, "final"), null, default));
            Assert.Equal("INVALID_DATE_RANGE", error.Code);
            return;
        }
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template) with { Calendar = options }, stage, Path.Combine(root, "final"), null, default);
        Assert.Equal(365, Assert.Single(result.Tables).RowCount);
        using var written = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(stage, "pbip", "project.SemanticModel", "model.bim")));
        var actualTable = written.Model.Tables["Calendar"];
        var mapping = ScaffoldCalendarWriter.ReadMapping(actualTable);
        var oracle = ScaffoldCalendar.GenerateFields(ScaffoldCalendarWriter.ReadOptions(actualTable), mapping.Select(value => value.Field).ToArray());
        var first = mapping.Select((value, index) => (value.Name, Value: oracle.Rows[0][index])).ToDictionary(value => value.Name, value => value.Value);
        Assert.Contains("List.Dates", written.Model.Expressions[ScaffoldCalendarWriter.FunctionBinding(actualTable)!.FunctionName].Expression, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(stage, "data", "Calendar.csv")));
        Assert.Equal(compatibility == "gregorian" ? 56 : 5, first.Count);
        Assert.DoesNotContain(mapping, column => ScaffoldCalendarFields.Get(column.Field).RequiresLunar);
        if (compatibility == "gregorian")
        {
            Assert.Equal($"{year}-01-01", first["C01_Dates"]);
            Assert.Equal("365", first["C50_DaysOfYear"]);
            Assert.Equal("31", first["C47_DaysOfMonth"]);
            Assert.Equal("工作日", first["C63_Status"]);
        }
        _ = await new ProjectReader().ReadAsync(Path.Combine(stage, result.EntryFile), default);
    }

    /// <summary>产品输出经真实目录发现后可复用, 更新根参数并保持模型归属、源文件和自定义说明.</summary>
    [Fact]
    public async Task GeneratedProjectCanBeReusedAsUserTemplate()
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "generated"); Directory.CreateDirectory(source);
        await new ScaffoldModelWriter().ComposeAsync(builtin, Request(builtin), source, Path.Combine(root, "first"), null, default);
        await File.WriteAllTextAsync(Path.Combine(source, "README.scaffold.txt"), "User preserved instructions");
        var info = Assert.Single(await catalog.ListAsync(default), t => t.Name == "generated");
        var template = await catalog.GetAsync(info.Id, default);
        var before = template.Files.ToDictionary(f => f.RelativePath, f => f.Content.ToArray());
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var final = Path.Combine(root, "second");
        var result = await new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, final, null, default);
        using var database = ReadDatabase(stage);
        Assert.Contains(final, database.Model.Expressions["PbiUtilsProjectRoot"].Expression, StringComparison.Ordinal);
        Assert.Equal("User preserved instructions", await File.ReadAllTextAsync(Path.Combine(stage, "README.scaffold.txt")));
        Assert.NotNull(database.Model.Annotations.Find(ModelConfigurationOwnership.RolesStamp));
        Assert.False(File.Exists(Path.Combine(stage, "pbi-utils.scaffold.json")));
        foreach (var file in before) Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(source, file.Key)));
        _ = await new ProjectReader().ReadAsync(Path.Combine(stage, result.EntryFile), default);
    }

    /// <summary>模板内同名用户文件没有产品控制含义, 复制时保持原始字节且不覆盖.</summary>
    [Theory]
    [InlineData("{\"schemaVersion\":99}")]
    [InlineData("{\"schemaVersion\":1,\"custom\":\"user-owned\"}")]
    public async Task UserScaffoldNamedFileIsPreservedWithoutInterpretingItsContent(string manifest)
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var template = builtin with { Info = builtin.Info with { Id = "user:fixture", Source = "user" }, Files = [.. builtin.Files, new("pbi-utils.scaffold.json", bytes)] };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, Path.Combine(root, "final"), null, default);
        Assert.Equal(manifest, Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(stage, "pbi-utils.scaffold.json")));
    }

    /// <summary>同步测试进度, 可在准确业务阶段触发取消.</summary>
    private sealed class ProgressAction(Action<ScaffoldProgress> action) : IProgress<ScaffoldProgress>
    {
        /// <summary>立即执行测试观察者, 不引入异步时序.</summary>
        public void Report(ScaffoldProgress value) => action(value);
    }

    /// <summary>CSV列内容由真正磁盘回读, 不重复引用生成器内存结果.</summary>
    private static List<Dictionary<string, string>> ReadCsv(string stage, string table)
    {
        var adapted = Path.Combine(stage, "data", "model", table + ".csv");
        using var parser = new TextFieldParser(File.Exists(adapted) ? adapted : Path.Combine(stage, "data", table + ".csv"), Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); var header = parser.ReadFields()!;
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData) { var row = parser.ReadFields()!; rows.Add(header.Select((key, index) => KeyValuePair.Create(key, row[index])).ToDictionary()); }
        return rows;
    }

    /// <summary>构造最小合法请求.</summary>
    private static ScaffoldRequest Request(ResolvedPbipTemplate template) => new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, null, null, null);
    /// <summary>只读测试产物的唯一模型.</summary>
    private static TOM.Database ReadDatabase(string stage) => TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", System.IO.SearchOption.AllDirectories).Single() + "/definition");
    /// <summary>只清理测试自身分配的根目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}


