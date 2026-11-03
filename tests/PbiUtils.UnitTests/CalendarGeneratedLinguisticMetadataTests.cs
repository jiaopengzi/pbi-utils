// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarGeneratedLinguisticMetadataTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前无内置 Q&A 来源时的拒绝认领、人工内容保护和 culture 解析边界.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Calendar;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用当前空白模板和显式人工元数据验证保护边界, 不代表 Power BI 引擎计算或报表验收.</summary>
public sealed class CalendarGeneratedLinguisticMetadataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-qna-" + Guid.NewGuid().ToString("N"));

    /// <summary>完整生成归属和相同表名不赋予未知 Q&A 认领权限, 两种编号偏好均受保护.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidGeneratedCalendarDoesNotClaimUnknownQna(bool numbered)
    {
        using var database = await Compose("blank", numbered);
        var table = database.Model.Tables["Calendar"];
        var metadata = AddManualMetadata(database, table, table.Columns.Select(column => column.Name));
        var original = metadata.Content;
        Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedCultures(table));
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedMeasureConfigCultures(database.Model));
        Assert.Equal(original, metadata.Content);
    }

    /// <summary>两种模板度量模式都不能把人工 Q&A 作为当前模板全文认领或清理.</summary>
    [Theory]
    [InlineData("tmdl")]
    [InlineData("config")]
    public async Task MeasureModesKeepManualQnaUnclaimed(string mode)
    {
        using var database = await Compose("blank", true, mode);
        var table = database.Model.Tables["Calendar"];
        var metadata = AddManualMetadata(database, table, [table.Columns[0].Name]);
        var original = metadata.Content;
        Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedCultures(table));
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedMeasureConfigCultures(database.Model));
        Assert.Equal(original, metadata.Content);
    }

    /// <summary>人工 Q&A 的全部真实列绑定保持保护, 缩减字段不能删除其依赖或改写正文.</summary>
    [Fact]
    public async Task ManualQnaRemainsUnchangedAndKeepsColumnReferences()
    {
        using var database = await Compose("blank", true);
        var table = database.Model.Tables["Calendar"];
        var metadata = AddManualMetadata(database, table, table.Columns.Select(column => column.Name));
        var manual = metadata.Content;
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedCultures(table));
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, Fields: [.. ScaffoldCalendarFields.Simple]), true, null, [], [], default);
        Assert.Equal(manual, metadata.Content);
        Assert.Equal(63, table.Columns.OfType<TOM.DataColumn>().Count());
        AssertBindingsExist(database, table);
    }

    /// <summary>人工 Q&A 日期列绑定阻止改编号, 失败前后保留完整原模型.</summary>
    [Fact]
    public async Task ManualQnaPreventsUnsafeNumberingOverride()
    {
        using var database = await Compose("blank", true);
        var table = database.Model.Tables["Calendar"];
        AddManualMetadata(database, table, ["C01_Dates"]);
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = false };
        var error = Assert.Throws<AppException>(() => ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default));
        Assert.Equal("CONFIGURATION_CALENDAR_RENAME_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>Q&A 仅声明表实体不依赖整张表的每列, 不应使没有列引用的日期表全部锁定.</summary>
    [Fact]
    public void PureQnaTableEntityDoesNotLockAllColumns()
    {
        using var database = new TOM.Database { Name = "qna-table", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name), true, null, [], [], default);
        var culture = new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"calendar\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Calendar\"}}}}}"
            }
        };
        database.Model.Cultures.Add(culture);
        Assert.Equal(["Dates"], CalendarColumnProtection.RequiredFields(table).Order());
    }

    /// <summary>空白模板的合法 culture 不要求 Q&A, 显式日期字段切换也不创建语言元数据.</summary>
    [Fact]
    public async Task EmptyBuiltinCultureAllowsCalendarReconfigurationWithoutQna()
    {
        using var database = await Compose("blank", true);
        var culture = Assert.Single(database.Model.Cultures);
        var table = database.Model.Tables["Calendar"];
        Assert.Null(culture.LinguisticMetadata);
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedCultures(table));
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name,
            Fields: [.. ScaffoldCalendarFields.Simple], NumberedColumns: false), true, null, [], [], default);
        Assert.Equal(5, table.Columns.OfType<TOM.DataColumn>().Count());
        Assert.Null(culture.LinguisticMetadata);
        Assert.Empty(CalendarGeneratedLinguisticMetadata.VerifiedCultures(table));
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name,
            Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)], NumberedColumns: true), true, null, [], [], default);
        Assert.Equal(63, table.Columns.OfType<TOM.DataColumn>().Count());
        Assert.Null(culture.LinguisticMetadata);
    }

    /// <summary>合法无 Q&A 的 culture 仅有名称及空白正文, LF/CRLF 与末尾换行不改变含义.</summary>
    [Theory]
    [InlineData("cultureInfo zh-CN")]
    [InlineData("cultureInfo zh-CN\n\n")]
    [InlineData("cultureInfo zh-CN\r\n\t \r\n")]
    public void EmptyCultureHasNoBuiltinQna(string text)
    {
        var (name, content) = CalendarGeneratedLinguisticMetadata.ParseBuiltinCulture(text);
        Assert.Equal("zh-CN", name);
        Assert.Null(content);
    }

    /// <summary>存在完整 JSON Q&A 时保留真实对象内容, 不将已声明的元数据当成空 culture.</summary>
    [Fact]
    public void JsonCultureRetainsBuiltinQna()
    {
        var (name, content) = CalendarGeneratedLinguisticMetadata.ParseBuiltinCulture("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t{\"Entities\":{\"calendar\":{}}}\n\t\tcontentType: json\n");
        Assert.Equal("zh-CN", name);
        Assert.NotNull(content);
        Assert.True(content["Entities"]!.AsObject().ContainsKey("calendar"));
    }

    /// <summary>缺失头、未知正文、重复段、不完整声明和畸形 JSON 均明确失败, 不被空 culture 分支吞掉.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("cultureInfo \n")]
    [InlineData("cultureInfo zh-CN\n\tunknown: value\n")]
    [InlineData("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t{}\n")]
    [InlineData("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t{\"Entities\":}\n\t\tcontentType: json\n")]
    [InlineData("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t[]\n\t\tcontentType: json\n")]
    [InlineData("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t{}\n\t\tcontentType: xml\n")]
    [InlineData("cultureInfo zh-CN\n\tlinguisticMetadata =\n\t\t{}\n\t\tcontentType: json\n\tlinguisticMetadata =\n\t\t{}\n\t\tcontentType: json\n")]
    public void MalformedCultureDoesNotBecomeEmptyQna(string text)
    {
        var error = Assert.Throws<AppException>(() => CalendarGeneratedLinguisticMetadata.ParseBuiltinCulture(text));
        Assert.Equal("TEMPLATE_RESOURCE_INVALID", error.Code);
    }

    /// <summary>组合真实嵌入模板, 返回测试独占目录内的官方 TOM 回读结果.</summary>
    private async Task<TOM.Database> Compose(string kind, bool numbered, string mode = "tmdl")
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "calendar", "zh-cn", mode == "config" ? TemplateMeasureValidation.DefaultMeasures() : null, null,
            new("full", 2024, 2024, Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)], NumberedColumns: numbered), null, null, MeasuresMode: mode);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        return TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single(), "definition"));
    }

    /// <summary>建立用户拥有的显式列绑定, 不复制任何历史内置语言元数据.</summary>
    /// <param name="database">来自当前空白模板的独占模型.</param>
    /// <param name="table">用户词汇实际绑定的日期表.</param>
    /// <param name="columns">需要保护的真实列名.</param>
    /// <returns>保存人工 JSON 内容的原元数据对象.</returns>
    private static TOM.LinguisticMetadata AddManualMetadata(TOM.Database database, TOM.Table table, IEnumerable<string> columns)
    {
        var entities = new JsonObject();
        foreach (var column in columns)
            entities.Add("manual_" + column, new JsonObject
            {
                ["Definition"] = new JsonObject
                {
                    ["Binding"] = new JsonObject { ["ConceptualEntity"] = table.Name, ["ConceptualProperty"] = column }
                }
            });
        var content = new JsonObject { ["Entities"] = entities, ["ManualDescription"] = "用户维护的业务词汇" };
        var metadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = content.ToJsonString() };
        database.Model.Cultures.Single().LinguisticMetadata = metadata;
        return metadata;
    }

    /// <summary>所有目标表 Q&A 属性必须仍能定位到实际输出列, 其他业务实体内容不参与本断言.</summary>
    private static void AssertBindingsExist(TOM.Database database, TOM.Table table)
    {
        foreach (var culture in database.Model.Cultures)
        {
            var node = JsonNode.Parse(culture.LinguisticMetadata.Content)!;
            foreach (var entity in node["Entities"]!.AsObject())
            {
                var binding = entity.Value?["Definition"]?["Binding"];
                if (binding?["ConceptualEntity"]?.GetValue<string>() == table.Name && binding["ConceptualProperty"] is JsonValue property)
                    Assert.True(table.Columns.ContainsName(property.GetValue<string>()));
            }
        }
    }

    /// <summary>仅删除本测试创建且路径固定的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
