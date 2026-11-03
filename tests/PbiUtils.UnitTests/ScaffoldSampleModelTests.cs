// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldSampleModelTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证新建模拟数据的关系、基础度量和模板同名表覆盖边界.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Calendar;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>回读真实隔离产物, 不将元数据测试当作 Power BI 引擎执行.</summary>
public sealed class ScaffoldSampleModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-sample-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前空白模板显式配置日期后安装样例业务关系和基础度量, 日期关系绑定最终输出字段.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task EnabledSampleInstallsRelationshipsAndBaseMeasures(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var stage = await Compose(template, SampleRequest(template) with { Calendar = new("full", 2024, 2024, NumberedColumns: true) });
        using var database = ReadDatabase(stage);
        var model = database.Model;
        foreach (var file in SampleTables.AllFiles)
        {
            var table = model.Tables[Path.GetFileNameWithoutExtension(file)];
            foreach (var name in SampleTables.Header(file)) Assert.True(table.Columns.ContainsName(name));
        }
        var sale = model.Tables["Measure"].Measures["001_销售金额_单位"];
        Assert.Equal("\nSUM ( 'T05_订单子表'[F_06_产品销售金额] )", sale.Expression);
        Assert.Equal(13, model.Tables["Measure"].Measures.Count);
        Assert.Equal("ImageUrl", model.Tables["Measure"].Measures["011_大区idphotoURL"].DataCategory);
        Assert.Equal("0.00%;-0.00%;0.00%", model.Tables["Measure"].Measures["006_毛利率%"].FormatString);
        Assert.True(model.Tables["RefreshTime"].Columns.ContainsName("RefreshTime"));
        Assert.True(model.Tables["MQHY"].Columns.ContainsName("MQHY"));
        var details = Assert.Single(model.Relationships.OfType<TOM.SingleColumnRelationship>(), relation =>
            relation.FromTable.Name == "T05_订单子表" && relation.ToTable.Name == "T00_产品表");
        Assert.Equal("F_02_产品编号", details.FromColumn.Name);
        Assert.Equal("F_01_产品编号", details.ToColumn.Name);
        var delivery = Assert.Single(model.Relationships.OfType<TOM.SingleColumnRelationship>(), relation =>
            relation.FromTable.Name == "T04_订单主表" && relation.FromColumn.Name == "F_04_送货日期");
        Assert.False(delivery.IsActive);
        Assert.Equal("Calendar", delivery.ToTable.Name);
        Assert.Equal("C01_Dates", delivery.ToColumn.Name);
        Assert.Equal(21, model.Relationships.OfType<TOM.SingleColumnRelationship>().Count(relation =>
            IsSample(relation.FromTable.Name) || relation.ToTable.Name == "MQHY"));
        Assert.All(model.Relationships.OfType<TOM.SingleColumnRelationship>(), relation =>
        {
            Assert.Same(model.Tables[relation.FromTable.Name].Columns[relation.FromColumn.Name], relation.FromColumn);
            Assert.Same(model.Tables[relation.ToTable.Name].Columns[relation.ToColumn.Name], relation.ToColumn);
        });
    }

    /// <summary>用户同名表接入本轮样例, 原额外列及度量身份仍可被关系和表达式引用.</summary>
    [Fact]
    public async Task SameNameTemplateTableIsReplacedAndCompatibilityReferencesSurvive()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var content = Encoding.UTF8.GetBytes("table T00_产品表\n\tcolumn F_01_产品编号\n\t\tdataType: string\n\t\tsourceColumn: F_01_产品编号\n\tcolumn ExistingCode\n\t\tdataType: string\n\t\tsourceColumn: ExistingCode\n\tmeasure ExistingMeasure = COUNTROWS('T00_产品表')\n\tpartition T00_产品表 = m\n\t\tmode: import\n\t\tsource = #table({\"F_01_产品编号\",\"ExistingCode\"}, {{\"old\",\"old\"}})\n");
        template = template with
        {
            Info = template.Info with { Id = "user:collision", Source = "user" },
            Files = [.. template.Files.Where(file => file.RelativePath != "pbi-utils.template.json"),
                new("blank.SemanticModel/definition/tables/T00_产品表.tmdl", content)]
        };
        var stage = await Compose(template, SampleRequest(template));
        using var database = ReadDatabase(stage);
        var table = database.Model.Tables["T00_产品表"];
        Assert.Equal(SampleTables.HeaderProduct.Count + 1, table.Columns.Count);
        Assert.True(table.Columns.ContainsName("ExistingCode"));
        Assert.Equal("COUNTROWS('T00_产品表')", table.Measures["ExistingMeasure"].Expression.Trim());
        var expression = Assert.IsType<TOM.MPartitionSource>(Assert.Single(table.Partitions).Source).Expression;
        Assert.Contains("PbiUtilsProjectRoot", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", expression, StringComparison.Ordinal);
        Assert.Equal(content, template.Files.Single(file => file.RelativePath.EndsWith("/T00_产品表.tmdl", StringComparison.Ordinal)).Content);
        Assert.Contains("ExistingCode", await File.ReadAllTextAsync(Path.Combine(stage, "data", "model", "T00_产品表.csv")), StringComparison.Ordinal);
    }

    /// <summary>同名模板列的顺序和表名大小写不影响固定样例源的接入.</summary>
    [Theory]
    [InlineData("T00_产品表")]
    [InlineData("t00_产品表")]
    public async Task SameNameTableSupportsDifferentColumnOrderAndCase(string name)
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var columns = string.Join('\n', SampleTables.HeaderProduct.Select((column, index) =>
            $"\tcolumn {column}\n\t\tdataType: {ScaffoldDataWriter.SampleType(SampleTables.FileProduct, index).ToString().ToLowerInvariant()}\n\t\tsourceColumn: {column}").Reverse());
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:column-order", Source = "user" },
            Files = [.. builtin.Files.Where(file => file.RelativePath != "pbi-utils.template.json"),
                new($"blank.SemanticModel/definition/tables/{name}.tmdl", Encoding.UTF8.GetBytes($"table {name}\n{columns}\n"))]
        };
        var stage = await Compose(template, SampleRequest(template));
        using var database = ReadDatabase(stage);
        Assert.Equal(SampleTables.HeaderProduct.Count, database.Model.Tables[name].Columns.Count);
        Assert.Equal("F_05_产品成本价格", database.Model.Tables[name].Columns[0].Name);
        Assert.Contains("PbiUtilsProjectRoot", Assert.IsType<TOM.MPartitionSource>(Assert.Single(database.Model.Tables[name].Partitions).Source).Expression, StringComparison.Ordinal);
    }

    /// <summary>不启用模拟数据时保留模板对象, 不安装样例辅助表、关系或度量.</summary>
    [Fact]
    public async Task DisabledSampleDoesNotInstallSampleModel()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = await Compose(template, SampleRequest(template) with { Sample = null });
        using var database = ReadDatabase(stage);
        Assert.False(database.Model.Tables.ContainsName("Measure"));
        Assert.False(database.Model.Tables.ContainsName("T00_产品表"));
        Assert.False(database.Model.Tables.ContainsName("MQHY"));
    }

    /// <summary>基础度量跨表同名按全模型大小写不敏感规则拒绝, 不发布重名模型.</summary>
    [Theory]
    [InlineData("001_销售金额_单位")]
    [InlineData("011_大区idphotourl")]
    public async Task BaseMeasureNameInAnotherTableFailsBeforePublishing(string name)
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var content = Encoding.UTF8.GetBytes($"table ExistingMeasures\n\tmeasure {name} = 123\n");
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:measure-conflict", Source = "user" },
            Files = [.. builtin.Files.Where(file => file.RelativePath != "pbi-utils.template.json"),
                new("blank.SemanticModel/definition/tables/ExistingMeasures.tmdl", content)]
        };
        var error = await Assert.ThrowsAsync<AppException>(() => Compose(template, SampleRequest(template)));
        Assert.Equal("SCAFFOLD_OBJECT_CONFLICT", error.Code);
        Assert.Equal(content, template.Files.Single(file => file.RelativePath.EndsWith("/ExistingMeasures.tmdl", StringComparison.Ordinal)).Content);
        Assert.False(Directory.Exists(Path.Combine(root, "final")));
    }

    /// <summary>同表同名基础度量按需求覆盖, 保留其他模板度量, 不误套跨表拒绝规则.</summary>
    [Fact]
    public async Task BaseMeasureNameInTargetTableIsReplaced()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:measure-replace", Source = "user" },
            Files = [.. builtin.Files.Where(file => file.RelativePath != "pbi-utils.template.json"),
                new("blank.SemanticModel/definition/tables/Measure.tmdl",
                    Encoding.UTF8.GetBytes("table Measure\n\tmeasure 001_销售金额_单位 = 123\n\tmeasure ExistingCustom = 456\n"))]
        };
        var stage = await Compose(template, SampleRequest(template));
        using var database = ReadDatabase(stage);
        Assert.Equal("\nSUM ( 'T05_订单子表'[F_06_产品销售金额] )", database.Model.Tables["Measure"].Measures["001_销售金额_单位"].Expression);
        Assert.Equal("456", database.Model.Tables["Measure"].Measures["ExistingCustom"].Expression.Trim());
    }

    /// <summary>缺少可选日期表时仍生成业务关系并报告省略项, 不加入悬挂关系.</summary>
    [Fact]
    public async Task MissingOptionalCalendarReportsSkippedRelationships()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with
        {
            Info = builtin.Info with { Id = "user:unmapped-calendar", Source = "user" },
            Files = builtin.Files.Where(file => file.RelativePath != "pbi-utils.template.json").ToArray()
        };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var result = await new ScaffoldModelWriter().ComposeAsync(template, SampleRequest(template), stage, Path.Combine(root, "final"), null, default);
        using var database = ReadDatabase(stage);
        Assert.Equal(10, database.Model.Relationships.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("11 条关系未生成", StringComparison.Ordinal));
    }

    /// <summary>未知同名辅助对象缺少真实端点时明确失败, 不生成无法计算的基础度量.</summary>
    [Fact]
    public async Task IncompatibleAuxiliaryTableFailsBeforePublishing()
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var template = builtin with
        {
            Files = [.. builtin.Files, new("blank.SemanticModel/definition/tables/RefreshTime.tmdl",
                Encoding.UTF8.GetBytes("table RefreshTime\n\tcolumn Other\n\t\tdataType: string\n\t\tsourceColumn: Other\n"))]
        };
        var error = await Assert.ThrowsAsync<AppException>(() => Compose(template, SampleRequest(template)));
        Assert.Equal("SCAFFOLD_COLUMN_MAPPING", error.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "final")));
    }

    /// <summary>显式配置日期和样例的新建产物再作为用户模板时, 替换数据并扩展已核验日期角色, 关系不重复.</summary>
    [Fact]
    public async Task GeneratedSampleTemplateCanRefreshDataAndCalendarWithoutDuplicateRelationships()
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "generated"); Directory.CreateDirectory(source);
        await new ScaffoldModelWriter().ComposeAsync(builtin, SampleRequest(builtin) with { Calendar = new("full", 2024, 2024, NumberedColumns: true) }, source, source, null, default);
        var info = Assert.Single(await catalog.ListAsync(default), item => item.Name == "generated");
        var template = await catalog.GetAsync(info.Id, default);
        var before = template.Files.ToDictionary(file => file.RelativePath, file => file.Content.ToArray());
        var stage = await Compose(template, SampleRequest(template) with { Sample = new("zh-cn", 2, 1, 7, "2026-01-01", "2026-04-01", 43) });
        using var database = ReadDatabase(stage);
        Assert.True(ScaffoldCalendarWriter.ReadOptions(database.Model.Tables["Calendar"]).EndYear >= 2026);
        Assert.Equal(21, database.Model.Relationships.Count);
        foreach (var file in before) Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(source, file.Key)));
    }

    /// <summary>仅对测试拥有目录组合新工程, 返回可回读的 staging.</summary>
    private async Task<string> Compose(ResolvedPbipTemplate template, ScaffoldRequest request)
    {
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        return stage;
    }

    /// <summary>构造小规模确定性样例请求, 关闭其他可选生成段.</summary>
    private static ScaffoldRequest SampleRequest(ResolvedPbipTemplate template) => new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn",
        null, new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42), null, null, null);

    /// <summary>使用官方反序列化器回读测试唯一语义模型.</summary>
    private static TOM.Database ReadDatabase(string stage) => TOM.TmdlSerializer.DeserializeDatabaseFromFolder(
        Path.Combine(Directory.GetDirectories(stage, "*.SemanticModel", System.IO.SearchOption.AllDirectories).Single(), "definition"));

    /// <summary>识别固定十一张样例表名, 不以任意前缀猜测归属.</summary>
    private static bool IsSample(string name) => SampleTables.AllFiles.Any(file => Path.GetFileNameWithoutExtension(file) == name);

    /// <summary>仅删除测试自行分配的临时根目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
