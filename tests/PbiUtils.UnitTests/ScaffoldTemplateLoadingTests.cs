// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldTemplateLoadingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证新建辅助表启用加载、刷新时间动态定义及未知模板源保护.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>通过隔离 staging 和官方 TOM 回读验证模型定义, 不宣称执行了 M 或真实刷新.</summary>
public sealed class ScaffoldTemplateLoadingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-template-loading-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前内置来源的显式选择均产生类型完整的导入表, 查询不再停留在禁用加载表达式.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task SelectedAuxiliariesAreLoadedTables(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var database = await ComposeAsync(template, Request(template) with { TemplateQueries = ["Hour24", "MQHY"] });
        var hour = database.Model.Tables.Find("Hour24");
        var period = database.Model.Tables.Find("MQHY");
        Assert.NotNull(hour);
        Assert.NotNull(period);
        Assert.Equal(["ID", "小时", "小时L", "小时R"], hour.Columns.Select(column => column.Name));
        Assert.Equal([TOM.DataType.Int64, TOM.DataType.String, TOM.DataType.Int64, TOM.DataType.Int64], hour.Columns.Select(column => column.DataType));
        Assert.Equal([TOM.AggregateFunction.Count, TOM.AggregateFunction.None, TOM.AggregateFunction.Sum, TOM.AggregateFunction.Sum], hour.Columns.Select(column => column.SummarizeBy));
        Assert.Equal(["Index", "MQHY"], period.Columns.Select(column => column.Name));
        Assert.Equal([TOM.DataType.Int64, TOM.DataType.String], period.Columns.Select(column => column.DataType));
        Assert.Same(period.Columns["Index"], period.Columns["MQHY"].SortByColumn);
        foreach (var table in new[] { hour, period })
        {
            Assert.Null(database.Model.Expressions.Find(table.Name));
            Assert.All(table.Columns.OfType<TOM.DataColumn>(), column => Assert.Equal(column.Name, column.SourceColumn));
            var partition = Assert.Single(table.Partitions);
            Assert.Equal(TOM.ModeType.Import, partition.Mode);
            Assert.Equal("model", partition.QueryGroup?.Folder);
            Assert.Equal(ScaffoldTemplateQueries.All.Single(query => query.Id == table.Name).Expression,
                Assert.IsType<TOM.MPartitionSource>(partition.Source).Expression.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal("Table", table.Annotations.Find("PBI_ResultType")?.Value);
            Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
        }
    }

    /// <summary>未选择保留模板原加载语义; 空白模板不隐式增加辅助表.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task EmptySelectionDoesNotLoadAuxiliaries(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var database = await ComposeAsync(template, Request(template) with { TemplateQueries = [] });
        Assert.Null(database.Model.Tables.Find("Hour24"));
        Assert.Null(database.Model.Tables.Find("MQHY"));
        Assert.Null(database.Model.Tables.Find("RefreshTime"));
    }

    /// <summary>当前空白模板无样例时都由辅助表勾选控制刷新表, 选中后启用真实加载.</summary>
    [Theory]
    [InlineData("blank", false)]
    [InlineData("blank", true)]
    public async Task RefreshTimeSelectionControlsLoading(string kind, bool selected)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var database = await ComposeAsync(template, Request(template) with { TemplateQueries = selected ? ["RefreshTime"] : [] });
        var table = database.Model.Tables.Find("RefreshTime");
        if (!selected) { Assert.Null(table); return; }
        Assert.NotNull(table);
        Assert.Null(database.Model.Expressions.Find("RefreshTime"));
        var column = Assert.IsType<TOM.DataColumn>(Assert.Single(table.Columns));
        Assert.Equal("RefreshTime", column.Name);
        Assert.Equal("RefreshTime", column.SourceColumn);
        Assert.Equal(TOM.DataType.String, column.DataType);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal(TOM.ModeType.Import, partition.Mode);
        Assert.Equal("model", partition.QueryGroup?.Folder);
        Assert.Equal(ScaffoldTemplateQueries.All.Single(query => query.Id == "RefreshTime").Expression,
            Assert.IsType<TOM.MPartitionSource>(partition.Source).Expression.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>示例基础度量引用刷新表, 即使未手动选中也保留其必要依赖.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task SampleKeepsRequiredRefreshTime(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var database = await ComposeAsync(template, Request(template) with
        {
            TemplateQueries = [],
            Sample = new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)
        });
        Assert.NotNull(database.Model.Tables.Find("RefreshTime"));
        Assert.Contains("RefreshTime[RefreshTime]", database.Model.Tables["Measure"].Measures["000_刷新时间"].Expression, StringComparison.Ordinal);
    }

    /// <summary>四个固定目录项及其独立分组可一起冻结, 不因旧三项上限被拒绝.</summary>
    [Fact]
    public void AllFourQueriesAndGroupsAreAccepted()
    {
        var request = new ScaffoldRequest(1, "builtin:blank", new string('a', 64), "project", "zh-cn", null, null, null, null, null)
        {
            TemplateQueries = ["Hour24", "MQHY", "RefreshTime", "TypeDetails"],
            TemplateQueryGroups = new() { ["Hour24"] = "model", ["MQHY"] = "model", ["RefreshTime"] = "刷新信息", ["TypeDetails"] = "" }
        };
        ScaffoldValidation.Validate(request);
    }

    /// <summary>选中表的分组落在真实 M 分区, 空组名表示不分组, 分组共用仍保持单一组对象.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("共享维度")]
    public async Task LoadedTablesRespectSelectedGroups(string group)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        using var database = await ComposeAsync(template, Request(template) with
        {
            TemplateQueries = ["Hour24", "MQHY", "RefreshTime"],
            TemplateQueryGroups = new() { ["Hour24"] = group, ["MQHY"] = group, ["RefreshTime"] = group }
        });
        foreach (var name in new[] { "Hour24", "MQHY", "RefreshTime" })
            Assert.Equal(group.Length == 0 ? null : group, Assert.Single(database.Model.Tables[name].Partitions).QueryGroup?.Folder);
        Assert.Equal(group.Length == 0 ? 0 : 1, database.Model.QueryGroups.Count);
    }

    /// <summary>当前空白模板显式辅助查询提供动态刷新表, 样例与度量配置开关不会改写为固定 CSV.</summary>
    [Theory]
    [InlineData("blank", false, false)]
    [InlineData("blank", false, true)]
    [InlineData("blank", true, false)]
    [InlineData("blank", true, true)]
    public async Task BuiltinRefreshTimeRemainsDynamic(string kind, bool sample, bool measures)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var request = Request(template) with
        {
            Sample = sample ? new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42) : null,
            Measures = measures ? TemplateMeasureValidation.DefaultMeasures() : null,
            TemplateQueries = ["Hour24", "MQHY", "RefreshTime"],
            TemplateQueryGroups = new() { ["RefreshTime"] = "刷新信息" }
        };
        using var database = await ComposeAsync(template, request);
        var table = database.Model.Tables.Find("RefreshTime");
        Assert.NotNull(table);
        Assert.Equal(TOM.DataType.String, Assert.Single(table.Columns).DataType);
        var expression = Assert.IsType<TOM.MPartitionSource>(Assert.Single(table.Partitions).Source).Expression;
        Assert.Contains("DateTime.LocalNow()", expression, StringComparison.Ordinal);
        Assert.Contains("#duration(0,0,0,0)", expression.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("#(000021BB) #[date]", expression, StringComparison.Ordinal);
        Assert.Contains("\"zh-CN\"", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Contents", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshTime.csv", expression, StringComparison.Ordinal);
        Assert.False(table.ExcludeFromModelRefresh);
        Assert.Equal("刷新信息", Assert.Single(table.Partitions).QueryGroup?.Folder);
        Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
    }

    /// <summary>未选择样例或辅助查询的空白模板保持空白, 不隐式增加刷新表.</summary>
    [Fact]
    public async Task BlankWithoutSampleRemainsEmpty()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        using var database = await ComposeAsync(template, Request(template));
        Assert.Empty(database.Model.Tables);
    }

    /// <summary>自定义同名刷新表继续使用原查询, 不以表名或字段形状推定可替换.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserRefreshTimeSourceIsPreserved(bool sample)
    {
        using var source = NewDatabase();
        var table = new TOM.Table { Name = "RefreshTime" };
        table.Columns.Add(new TOM.DataColumn { Name = "RefreshTime", SourceColumn = "RefreshTime", DataType = TOM.DataType.String });
        const string expression = "#table(type table [RefreshTime = text], {{\"user fixed value\"}})";
        table.Partitions.Add(new TOM.Partition { Name = table.Name, Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = expression } });
        source.Model.Tables.Add(table);
        var template = UserTemplate(source);
        using var database = await ComposeAsync(template, Request(template) with
        {
            Sample = sample ? new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42) : null
        });
        var actual = database.Model.Tables["RefreshTime"];
        Assert.Equal(expression, Assert.IsType<TOM.MPartitionSource>(Assert.Single(actual.Partitions).Source).Expression);
        Assert.False(ModelConfigurationOwnership.IsOwned(actual, actual.Annotations));
    }

    /// <summary>样例辅助表也遵守查询命名空间, 不在自定义同名表达式旁增加另一加载查询.</summary>
    [Theory]
    [InlineData("MQHY")]
    [InlineData("RefreshTime")]
    public async Task SampleAuxiliaryDoesNotReuseUserQueryName(string name)
    {
        using var source = NewDatabase();
        source.Model.Expressions.Add(new TOM.NamedExpression { Name = name, Kind = TOM.ExpressionKind.M, Expression = "#table({}, {})" });
        var template = UserTemplate(source);
        var error = await Assert.ThrowsAsync<AppException>(() => ComposeAsync(template, Request(template) with
        {
            Sample = new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)
        }));
        Assert.Equal("SCAFFOLD_OBJECT_CONFLICT", error.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
    }

    /// <summary>统一度量表后显式配置应最后应用, 启用覆盖样例定义, 禁用保留样例表达式并隐藏.</summary>
    [Theory]
    [InlineData("blank", true)]
    [InlineData("blank", false)]
    [InlineData("user", true)]
    [InlineData("user", false)]
    public async Task ExplicitMeasureConfigurationWinsOverSample(string kind, bool enabled)
    {
        using var source = NewDatabase();
        var template = kind == "user" ? UserTemplate(source) : await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var database = await ComposeAsync(template, Request(template) with
        {
            Sample = new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42),
            Measures = [new("001_销售金额_单位", "用户显式配置", "Uncategorized", enabled, "用户说明", "用户目录")]
        });
        var measure = database.Model.Tables["Measure"].Measures["001_销售金额_单位"];
        Assert.Equal(enabled ? "\"用户显式配置\"" : "SUM ( 'T05_订单子表'[F_06_产品销售金额] )", measure.Expression.Trim());
        Assert.Equal(!enabled, measure.IsHidden);
        if (enabled)
        {
            Assert.Equal("用户说明", measure.Description);
            Assert.Equal("用户目录", measure.DisplayFolder);
            Assert.Equal("Uncategorized", measure.DataCategory);
        }
        Assert.True(ModelConfigurationOwnership.IsOwned(measure, measure.Annotations));
    }

    /// <summary>失败前完成全部名称检查, 先选的合法查询不能在后续冲突时残留.</summary>
    [Fact]
    public void UserTableConflictLeavesCandidateUntouched()
    {
        using var database = NewDatabase();
        database.Model.Tables.Add(new TOM.Table { Name = "hour24" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => ScaffoldTemplateQueryWriter.Apply(database.Model, ["TypeDetails", "Hour24"], false));
        Assert.Equal("SCAFFOLD_OBJECT_CONFLICT", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>损坏的查询顺序不能导致先写表再失败, 保持候选的完整输入定义.</summary>
    [Fact]
    public void InvalidQueryOrderLeavesCandidateUntouched()
    {
        using var database = NewDatabase();
        database.Model.Annotations.Add(new TOM.Annotation { Name = "PBI_QueryOrder", Value = "invalid" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => ScaffoldTemplateQueryWriter.Apply(database.Model, ["Hour24", "MQHY"], false));
        Assert.Equal("SCAFFOLD_OBJECT_CONFLICT", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>为真实组合建立独占目录并回读模型; 格式错误和组合失败由测试直接报告.</summary>
    private async Task<TOM.Database> ComposeAsync(ResolvedPbipTemplate template, ScaffoldRequest request)
    {
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        var model = Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single();
        return Directory.Exists(Path.Combine(model, "definition"))
            ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(model, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(model, "model.bim")));
    }

    /// <summary>默认关闭可选段, 只使用当前模板的冻结标识和基线.</summary>
    private static ScaffoldRequest Request(ResolvedPbipTemplate template) => new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, null, null, null);

    /// <summary>构造测试拥有的空模型, 用于验证用户来源与失败时不变性.</summary>
    private static TOM.Database NewDatabase() => new() { ID = "fixture", Name = "fixture", CompatibilityLevel = 1606, Model = new TOM.Model { Name = "fixture" } };

    /// <summary>包装调用者拥有的真实 TOM 定义为最小用户模板, 不标记内置来源.</summary>
    private static ResolvedPbipTemplate UserTemplate(TOM.Database database) => new(
        new("user:fixture", "fixture", "user", new string('a', 64), "", []), "project.pbip",
        [new("project.pbip", Encoding.UTF8.GetBytes("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"report\"}}]}")),
            new("report/definition.pbir", Encoding.UTF8.GetBytes("{\"version\":\"4.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../model\"}}}")),
            new("report/definition/report.json", Encoding.UTF8.GetBytes("{}")),
            new("report/definition/pages/pages.json", Encoding.UTF8.GetBytes("{\"pageOrder\":[]}")),
            new("model/definition.pbism", Encoding.UTF8.GetBytes("{\"version\":\"1.0\"}")),
            new("model/model.bim", Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(database)))]);

    /// <summary>仅清理本测试自行分配的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
