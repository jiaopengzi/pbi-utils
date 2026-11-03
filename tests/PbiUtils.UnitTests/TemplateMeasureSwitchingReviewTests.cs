// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureSwitchingReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模式切换仅保留真实存活旧项, 不覆盖未选中的业务表达式.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用未连接的隔离 TOM, 不执行真实引擎写入或访问用户文件.</summary>
public sealed class TemplateMeasureSwitchingReviewTests
{
    /// <summary>已移除但仍被引用的手改旧项属于保留对象, 切换模式不能覆盖其业务 DAX 或元数据.</summary>
    [Fact]
    public void RemovedReferencedBusinessExpressionIsPreservedDuringModeSwitch()
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(model, [Definition("old", "stale")], "config", roles, configuration: true);
        var old = model.Tables["Measure"].Measures["old"];
        old.Expression = "SUM('Sales'[Amount])";
        old.Description = "user description";
        old.DisplayFolder = "user folder";
        old.DataCategory = "WebUrl";
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "consumer", Expression = "[old]" });
        var before = ModelConfigurationOwnership.Fingerprint(old);

        ScaffoldTemplateMeasureMode.Apply(model, [], "tmdl", roles, configuration: true);

        Assert.Same(old, model.Tables["Measure"].Measures["old"]);
        Assert.Equal(before, ModelConfigurationOwnership.Fingerprint(old));
        Assert.Empty(TemplateMeasureConfigurationStorage.Read(model)!.Measures);
    }

    /// <summary>一个被引用的旧项不应让整批无关旧项逃过删除, 其依赖闭包仍须保留.</summary>
    [Fact]
    public void ModeSwitchRetainsOnlyTheReferencedRemovalClosure()
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(model,
            [Definition("referenced", "one"), Definition("dependency", "two"), Definition("unreferenced", "three"), Definition("cycle_a", "a"), Definition("cycle_b", "b")],
            "tmdl", roles, configuration: true);
        model.Tables["Measure"].Measures["referenced"].Expression = "[dependency]";
        model.Tables["Measure"].Measures["cycle_a"].Expression = "[cycle_b]";
        model.Tables["Measure"].Measures["cycle_b"].Expression = "[cycle_a]";
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "consumer", Expression = "[referenced]" });

        ScaffoldTemplateMeasureMode.Apply(model, [], "config", roles, configuration: true, sourceDefinitions: []);

        Assert.NotNull(model.Tables["Measure"].Measures.Find("referenced"));
        Assert.NotNull(model.Tables["Measure"].Measures.Find("dependency"));
        Assert.Null(model.Tables["Measure"].Measures.Find("unreferenced"));
        Assert.Null(model.Tables["Measure"].Measures.Find("cycle_a"));
        Assert.Null(model.Tables["Measure"].Measures.Find("cycle_b"));
        Assert.Equal("[dependency]", model.Tables["Measure"].Measures["referenced"].Expression);
    }

    /// <summary>旧生成查值禁用后被移出本轮列表, 精确物化可使其被保留的用户引用在多次模式切换后仍有相同值.</summary>
    [Theory]
    [InlineData("Uncategorized", "retained value")]
    [InlineData("ImageUrl", "<svg></svg>")]
    [InlineData("ImageUrl", "<svg/>")]
    [InlineData("ImageUrl", "prefix<svg></svg>")]
    public void RemovedDisabledGeneratedLookupIsMaterializedAcrossRepeatedSwitches(string category, string value)
    {
        using var database = Database();
        var model = database.Model;
        foreach (var name in new[] { "ConfigTable", "ConfigValue", "SvgStartImage", "SvgMatchPosition", "Result" })
            model.Tables.Add(new TOM.Table { Name = name });
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = Definition("old", value) with { DataCategory = category };
        ScaffoldTemplateMeasureMode.Apply(model, [definition], "config", roles, configuration: true);
        var old = model.Tables["Measure"].Measures["old"];
        ScaffoldTemplateMeasureMode.Apply(model, [definition with { Enabled = false }], "config", roles, configuration: true);
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "consumer", Expression = "[old]" });
        // 当前表集合发生变化, 不能仅按此刻的变量避让结果猜测旧公式是否由工具生成.
        model.Tables.Remove("ConfigTable");
        var expected = category == "ImageUrl" ? DaxFormatter.FormatMeasure($"""
            VAR SvgStartImage1 = "data:image/svg+xml;utf8,"
            VAR ConfigValue1 = {ScaffoldTemplateMeasures.Literal(value)}
            VAR SvgMatchPosition1 = COALESCE(SEARCH("<svg*</svg>", ConfigValue1, 1, BLANK()), SEARCH("<svg*/>", ConfigValue1, 1, BLANK()))
            VAR Result1 = IF(ISBLANK(SvgMatchPosition1) || LEFT(ConfigValue1, 5) = "data:" || LEFT(ConfigValue1, 7) = "http://" || LEFT(ConfigValue1, 8) = "https://", ConfigValue1, SvgStartImage1 & ConfigValue1)
            RETURN Result1
            """) : ScaffoldTemplateMeasures.LiteralMeasure(value);

        foreach (var mode in new[] { "tmdl", "tmdl", "config", "tmdl" })
        {
            ScaffoldTemplateMeasureMode.Apply(model, [], mode, roles, configuration: true);
            Assert.Same(old, model.Tables["Measure"].Measures["old"]);
            Assert.Equal(expected, old.Expression);
            Assert.True(old.IsHidden);
            Assert.Empty(TemplateMeasureConfigurationStorage.Read(model)!.Measures);
        }
    }

    /// <summary>仍在当前列表内的禁用查值保持公式, 多轮 TMDL 快照及再次 Config 均保留该项数据行.</summary>
    [Fact]
    public void ListedDisabledLookupKeepsItsSourceAcrossRepeatedSwitches()
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = Definition("old", "first");
        ScaffoldTemplateMeasureMode.Apply(model, [definition], "config", roles, configuration: true);
        var old = model.Tables["Measure"].Measures["old"];
        var expression = old.Expression;

        foreach (var mode in new[] { "tmdl", "tmdl", "config", "tmdl" })
        {
            definition = definition with { Value = definition.Value + " next", Enabled = false };
            ScaffoldTemplateMeasureMode.Apply(model, [definition], mode, roles, configuration: true);
            Assert.Equal(expression, old.Expression);
            Assert.True(old.IsHidden);
            var config = model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
            var source = ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression;
            if (mode == "config") Assert.Contains("File.Contents", source);
            else
            {
                Assert.DoesNotContain("File.Contents", source);
                Assert.Contains(PowerQueryLiterals.Text(definition.Value), source);
            }
        }
    }

    /// <summary>TMDL 中仍依赖隐藏快照的禁用项, 随下次 Config 切换移出清单时也必须消除旧行依赖.</summary>
    [Fact]
    public void RemovedDisabledSnapshotLookupIsMaterializedWhenReturningToConfig()
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = Definition("old", "snapshot value");
        ScaffoldTemplateMeasureMode.Apply(model, [definition], "config", roles, configuration: true);
        var old = model.Tables["Measure"].Measures["old"];
        ScaffoldTemplateMeasureMode.Apply(model, [definition with { Enabled = false }], "tmdl", roles, configuration: true);
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "consumer", Expression = "[old]" });

        ScaffoldTemplateMeasureMode.Apply(model, [], "config", roles, configuration: true, sourceDefinitions: []);

        Assert.Same(old, model.Tables["Measure"].Measures["old"]);
        Assert.Equal("\n\"snapshot value\"", old.Expression);
        Assert.True(old.IsHidden);
        Assert.Empty(TemplateMeasureConfigurationStorage.Read(model)!.Measures);
    }

    /// <summary>当前 Config 的有效文件值优先于旧注解, 空来源或缺键保留查值 BLANK, null 才使用旧值.</summary>
    [Theory]
    [InlineData("none", "\"stored\"")]
    [InlineData("empty", "BLANK()")]
    [InlineData("missing-key", "BLANK()")]
    [InlineData("external", "\"external value\"")]
    public void MaterializationUsesTheVerifiedCurrentConfigSource(string sourceKind, string expectedDax)
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(model, [Definition("old", "stored")], "config", roles, configuration: true);
        var old = model.Tables["Measure"].Measures["old"];
        old.Description = "keep description";
        model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "consumer", Expression = "[old]" });
        TemplateMeasureDefinition[]? sources = sourceKind switch
        {
            "none" => null,
            "empty" => [],
            "missing-key" => [Definition("another", "another value")],
            _ => [Definition("old", "external value") with { DataCategory = "ImageUrl", Description = "must not apply" }]
        };

        ScaffoldTemplateMeasureMode.Apply(model, [], "tmdl", roles, configuration: true, sourceDefinitions: sources);

        Assert.Equal("\n" + (expectedDax == "BLANK()" ? "BLANK ()" : expectedDax), old.Expression);
        Assert.Equal("Uncategorized", old.DataCategory);
        Assert.Equal("keep description", old.Description);
    }

    /// <summary>零来源行必须物化为 BLANK(), 存在一行空字符串必须物化为 ""; 图像保持原条件语义及用户引用.</summary>
    [Theory]
    [InlineData("Uncategorized", false)]
    [InlineData("Uncategorized", true)]
    [InlineData("ImageUrl", false)]
    [InlineData("ImageUrl", true)]
    public void MaterializationPreservesMissingRowsAndPresentEmptyValues(string category, bool sourceRowExists)
    {
        // 缺失来源同时覆盖明确空数组及非空数组缺键, 它们与存在空值行具有不同的真实引擎语义.
        TemplateMeasureDefinition[][] sources = sourceRowExists ? [[Definition("old", "")]] : [[], [Definition("another", "another value")]];
        foreach (var source in sources)
        {
            using var database = Database();
            var model = database.Model;
            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            ScaffoldTemplateMeasureMode.Apply(model, [Definition("old", "stored") with { DataCategory = category }], "config", roles, configuration: true);
            var old = model.Tables["Measure"].Measures["old"];
            var consumer = new TOM.Measure { Name = "consumer", Expression = "[old]" };
            model.Tables["Measure"].Measures.Add(consumer);
            var scalar = sourceRowExists ? "\"\"" : "BLANK()";
            var expected = category == "ImageUrl" ? DaxFormatter.FormatMeasure($"""
                VAR SvgStartImage = "data:image/svg+xml;utf8,"
                VAR ConfigValue = {scalar}
                VAR SvgMatchPosition = COALESCE(SEARCH("<svg*</svg>", ConfigValue, 1, BLANK()), SEARCH("<svg*/>", ConfigValue, 1, BLANK()))
                VAR Result = IF(ISBLANK(SvgMatchPosition) || LEFT(ConfigValue, 5) = "data:" || LEFT(ConfigValue, 7) = "http://" || LEFT(ConfigValue, 8) = "https://", ConfigValue, SvgStartImage & ConfigValue)
                RETURN Result
                """) : "\n" + (sourceRowExists ? scalar : "BLANK ()");

            ScaffoldTemplateMeasureMode.Apply(model, [], "tmdl", roles, configuration: true, sourceDefinitions: source);

            Assert.Same(old, model.Tables["Measure"].Measures["old"]);
            Assert.Equal(expected, old.Expression);
            Assert.Equal("[old]", consumer.Expression);
            Assert.Empty(TemplateMeasureConfigurationStorage.Read(model)!.Measures);
            Assert.DoesNotContain("PbiUtilsTemplateMeasures", old.Expression);
        }
    }

    /// <summary>参数表的用户层次和关系在双向切换时保持原对象和完整定义, 不因表可见列恰好六项而误删.</summary>
    [Fact]
    public void UserHierarchyAndRelationshipKeepTheSameParameterObjects()
    {
        using var database = Database();
        var model = database.Model;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new[] { Definition("author", "value") };
        ScaffoldTemplateMeasureMode.Apply(model, definitions, "config", roles, configuration: true);
        var config = model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
        var column = config.Columns["name"];
        var hierarchy = new TOM.Hierarchy { Name = "user hierarchy" };
        hierarchy.Levels.Add(new TOM.Level { Name = "key", Column = column });
        config.Hierarchies.Add(hierarchy);
        var dimension = new TOM.Table { Name = "Dimension" };
        var key = new TOM.DataColumn { Name = "Key", SourceColumn = "Key", DataType = TOM.DataType.String };
        dimension.Columns.Add(key); model.Tables.Add(dimension);
        var relationship = new TOM.SingleColumnRelationship { Name = "user relationship", FromColumn = column, ToColumn = key };
        model.Relationships.Add(relationship);
        var expectedHierarchy = ModelConfigurationOwnership.Fingerprint(hierarchy);
        var expectedRelationship = ModelConfigurationOwnership.Fingerprint(relationship);

        foreach (var mode in new[] { "tmdl", "config", "tmdl" })
        {
            ScaffoldTemplateMeasureMode.Apply(model, definitions, mode, roles, configuration: true);
            Assert.Same(config, model.Tables[config.Name]);
            Assert.Same(column, config.Columns["name"]);
            Assert.Same(column, hierarchy.Levels["key"].Column);
            Assert.Same(column, relationship.FromColumn);
            Assert.Equal(expectedHierarchy, ModelConfigurationOwnership.Fingerprint(hierarchy));
            Assert.Equal(expectedRelationship, ModelConfigurationOwnership.Fingerprint(relationship));
        }
    }

    /// <summary>不连接模型的最小数据库仍使用正式 SDK 的完整可写定义扫描.</summary>
    private static TOM.Database Database() => new() { Name = "review", CompatibilityLevel = 1606, Model = new TOM.Model() };

    /// <summary>构造本用例完整六字段配置, 其他字段均为明确的空字符串.</summary>
    private static TemplateMeasureDefinition Definition(string name, string value) => new(name, value, "Uncategorized", true, "", "");
}
