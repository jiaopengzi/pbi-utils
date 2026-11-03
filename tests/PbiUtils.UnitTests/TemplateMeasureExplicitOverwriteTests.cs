// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureExplicitOverwriteTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 显式模板配置不依赖历史归属, 就地覆盖并保留相邻业务对象及真实删除引用保护.

using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>直接验证共享 TOM 变更的用户授权范围, 不连接或提交真实模型.</summary>
public sealed class TemplateMeasureExplicitOverwriteTests
{
    /// <summary>父表和选中项没有归属或已手改时, 双向模式均直接更新同一对象并保留其他子项.</summary>
    [Theory]
    [InlineData("Measure", "tmdl")]
    [InlineData("Measure", "config")]
    [InlineData("Business", "tmdl")]
    [InlineData("Business", "config")]
    public void ExplicitConfigurationUpsertsExistingMeasureAtItsOriginalLocation(string parent, string mode)
    {
        using var database = Database();
        var table = new TOM.Table { Name = parent, Description = "manual parent" };
        var selected = new TOM.Measure { Name = "author", Expression = "123", LineageTag = Guid.NewGuid().ToString() };
        var unrelated = new TOM.Measure { Name = "untouched", Expression = "456" };
        table.Measures.Add(selected); table.Measures.Add(unrelated); database.Model.Tables.Add(table);
        var unchanged = ModelConfigurationOwnership.Fingerprint(unrelated);
        var lineage = selected.LineageTag;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], mode, roles, configuration: true);
        Assert.Same(selected, table.Measures["author"]);
        Assert.Equal(lineage, selected.LineageTag);
        Assert.Equal("manual parent", table.Description);
        Assert.Equal(unchanged, ModelConfigurationOwnership.Fingerprint(unrelated));
        Assert.Equal(mode == "config", selected.Expression.Contains("CONCATENATEX", StringComparison.Ordinal));
        if (mode == "tmdl") Assert.Equal("\n\"next\"", selected.Expression);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "again")], mode == "config" ? "tmdl" : "config", roles, configuration: true);
        Assert.Same(selected, table.Measures["author"]);
    }

    /// <summary>失效、损坏和缺少旧模板清单不阻止本轮明确集合重建; 缺失旧度量也能重新创建.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingObjectsAndBrokenTemplateStateCanBeRecreated(bool brokenState)
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "old")], "config", roles, configuration: true);
        database.Model.Tables["Measure"].Measures.Remove("author");
        database.Model.Tables.Remove(TemplateMeasureConfigurationStorage.DefaultTable);
        database.Model.Tables["Measure"].Description = "manual";
        if (brokenState) database.Model.Tables["Measure"].Annotations[TemplateMeasureConfigurationStorage.Annotation].Value = "{broken";
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], "config", roles, configuration: true);
        Assert.Contains("CONCATENATEX", database.Model.Tables["Measure"].Measures["author"].Expression);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "last")], "tmdl", roles, configuration: true);
        Assert.Equal("\n\"last\"", database.Model.Tables["Measure"].Measures["author"].Expression);
        Assert.Equal("last", Assert.Single(TemplateMeasureConfigurationStorage.Read(database.Model)!.Measures).Value);
    }

    /// <summary>默认参数表缺少角色和归属或被手改后, Config 仍修复六字段并保留额外用户计算列.</summary>
    [Fact]
    public void ExplicitConfigRepairsUnownedParameterTable()
    {
        using var database = Database();
        var config = new TOM.Table { Name = TemplateMeasureConfigurationStorage.DefaultTable, Description = "manual" };
        config.Columns.Add(new TOM.DataColumn { Name = "name", SourceColumn = "wrong", DataType = TOM.DataType.Int64 });
        var extra = new TOM.CalculatedColumn { Name = "business", Expression = "1", DataType = TOM.DataType.Int64 };
        config.Columns.Add(extra); database.Model.Tables.Add(config);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], "config", roles, configuration: true);
        Assert.Same(config, database.Model.Tables[config.Name]);
        Assert.Same(extra, config.Columns["business"]);
        Assert.Equal("name", ((TOM.DataColumn)config.Columns["name"]).SourceColumn);
        Assert.Equal(TOM.DataType.String, config.Columns["name"].DataType);
        Assert.All(TemplateMeasureConfigurationStorage.Columns, name => Assert.NotNull(config.Columns.Find(name)));
        Assert.Equal("measure-config", roles[config.Name]);
    }

    /// <summary>参数表的用户引用不阻止 TMDL; 保留隐藏表及其可独立刷新的内嵌数据, 用户表达式原样保留.</summary>
    [Fact]
    public void UserReferenceKeepsHiddenParameterTableWithoutBlockingTmdl()
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "old")], "config", roles, configuration: true);
        var user = new TOM.Measure { Name = "user", Expression = "COUNTROWS('PbiUtilsTemplateMeasures')" };
        database.Model.Tables["Measure"].Measures.Add(user);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], "tmdl", roles, configuration: true);
        Assert.Equal("\n\"next\"", database.Model.Tables["Measure"].Measures["author"].Expression);
        Assert.Equal("COUNTROWS('PbiUtilsTemplateMeasures')", user.Expression);
        var config = database.Model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
        Assert.True(config.IsHidden);
        Assert.DoesNotContain("File.Contents", ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression);
        Assert.Contains("#table", ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression);
    }

    /// <summary>列表中真正删除已有模板项仍检查存活引用, 与单纯模式切换的覆盖授权区分.</summary>
    [Fact]
    public void RemovingReferencedTemplateMeasureStillFails()
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "old")], "tmdl", roles, configuration: true);
        database.Model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "user", Expression = "[author]" });
        var error = Assert.Throws<AppException>(() => ScaffoldTemplateMeasureMode.Apply(database.Model, [], "tmdl", roles, configuration: true));
        Assert.Equal("CONFIGURATION_MEASURE_REFERENCED", error.Code);
    }

    /// <summary>切换模式时旧清单缺项仍有引用, 保留旧度量并完成新集合和模式更新.</summary>
    [Theory]
    [InlineData("config", "tmdl")]
    [InlineData("tmdl", "config")]
    public void ModeSwitchRetainsReferencedPriorItems(string before, string after)
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("old", "kept value"), Definition("author", "one")], before, roles, configuration: true);
        var old = database.Model.Tables["Measure"].Measures["old"];
        database.Model.Tables["Measure"].Measures.Add(new TOM.Measure { Name = "user", Expression = "[old]" });
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], after, roles, configuration: true);
        Assert.Same(old, database.Model.Tables["Measure"].Measures["old"]);
        Assert.Equal("\n\"kept value\"", old.Expression);
        Assert.Equal(after, TemplateMeasureConfigurationStorage.Read(database.Model)!.Mode);
        Assert.Equal("next", Assert.Single(TemplateMeasureConfigurationStorage.Read(database.Model)!.Measures).Value);
    }

    /// <summary>模板 Config 可直接更新无归属根参数; 日期独立操作仍保留归属要求.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyExplicitTemplateConfigCanOverwriteUnownedRootParameter(bool template)
    {
        using var database = Database();
        var parameter = new TOM.NamedExpression { Name = "PbiUtilsProjectRoot", Kind = TOM.ExpressionKind.M, Expression = "\"manual\"" };
        database.Model.Expressions.Add(parameter);
        var request = template ? new ModelConfigurationRequest(Measures: [Definition("author", "next")], MeasuresMode: "config")
            : new ModelConfigurationRequest(Calendar: new("simple", 2024, 2024));
        var path = Path.Combine(Path.GetTempPath(), "explicit-root");
        var plan = new ModelConfigurationPlan("baseline", "digest", request, new([], []), [], path);
        if (!template)
        {
            Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", Assert.Throws<AppException>(() => ModelConfigurationMutation.Apply(database, plan)).Code);
            Assert.Equal("\"manual\"", parameter.Expression);
            return;
        }
        ModelConfigurationMutation.Apply(database, plan);
        Assert.Same(parameter, database.Model.Expressions[parameter.Name]);
        Assert.Equal(TOM.ExpressionKind.M, parameter.Kind);
        Assert.StartsWith(PowerQueryLiterals.Text(path), parameter.Expression);
    }

    /// <summary>保留额外数据列时分区补出 null 源列, 不把引擎刷新必定缺列的定义当成成功.</summary>
    [Fact]
    public void ExtraDataColumnHasAnExplicitNullablePartitionSource()
    {
        using var database = Database();
        var config = new TOM.Table { Name = TemplateMeasureConfigurationStorage.DefaultTable };
        var extra = new TOM.DataColumn { Name = "business", SourceColumn = "business-source", DataType = TOM.DataType.String };
        config.Columns.Add(extra); database.Model.Tables.Add(config);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], "config", roles, configuration: true);
        var expression = ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression;
        Assert.Contains("Table.SelectColumns", expression);
        Assert.Contains("MissingField.UseNull", expression);
        Assert.Contains("\"business-source\"", expression);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "last")], "tmdl", roles, configuration: true);
        Assert.Same(extra, config.Columns["business"]);
        Assert.Contains("MissingField.UseNull", ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression);
    }

    /// <summary>多个旧参数角色不导致 Single 异常, 选择已有对象并保留其他候选定义.</summary>
    [Fact]
    public void MultipleParameterRolesUseAnExistingDeterministicCandidate()
    {
        using var database = Database();
        var existing = new TOM.Table { Name = "ExistingConfig" }; database.Model.Tables.Add(existing);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal) { ["MissingConfig"] = "measure-config", [existing.Name] = "measure-config" };
        ModelConfigurationOwnership.SetRoles(database.Model, new(1, roles, false, new(StringComparer.Ordinal)));
        var rootPath = Path.GetTempPath();
        var context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, rootPath, Path.Combine(rootPath, "pbip", "test.pbip"), _ => Task.CompletedTask);
        Assert.Equal(existing.Name, context.ConfigTableName);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next")], "config", roles, configuration: true);
        Assert.Same(existing, database.Model.Tables[existing.Name]);
        Assert.Equal(existing.Name, Assert.Single(roles).Key);
        Assert.Contains("'ExistingConfig'", database.Model.Tables["Measure"].Measures["author"].Expression);
    }

    /// <summary>真正删除跨表模板项后, 原本有效的父表延续当前定义归属, 不把删除误判成人工变更.</summary>
    [Fact]
    public void RemovingCrossTableTemplateItemMaintainsPreviouslyValidParentOwnership()
    {
        using var database = Database();
        var parent = new TOM.Table { Name = "Business" };
        parent.Measures.Add(new TOM.Measure { Name = "author", Expression = "123" }); database.Model.Tables.Add(parent);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "old")], "tmdl", roles, configuration: true);
        ModelConfigurationOwnership.Mark(parent, parent.Annotations);
        Assert.True(ModelConfigurationOwnership.IsOwned(parent, parent.Annotations));
        var plan = new ModelConfigurationPlan("baseline", "digest", new(Measures: []), new([], []), [], null);
        ModelConfigurationMutation.Apply(database, plan);
        Assert.Empty(parent.Measures);
        Assert.True(ModelConfigurationOwnership.IsOwned(parent, parent.Annotations));
    }

    /// <summary>禁用项的业务 SUM 在任意模式往返时只隐藏, 不覆盖公式、描述和目录.</summary>
    [Theory]
    [InlineData("tmdl", "config")]
    [InlineData("config", "tmdl")]
    public void DisabledBusinessMeasureRetainsFormulaAcrossModes(string before, string after)
    {
        using var database = Database();
        var parent = new TOM.Table { Name = "Business" };
        var measure = new TOM.Measure { Name = "author", Expression = "SUM('Sales'[Amount])", Description = "business description", DisplayFolder = "business folder" };
        parent.Measures.Add(measure); database.Model.Tables.Add(parent);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = Definition("author", "must not replace formula") with { Enabled = false, Description = "template description", DisplayFolder = "template folder" };
        foreach (var mode in new[] { before, after })
        {
            ScaffoldTemplateMeasureMode.Apply(database.Model, [definition], mode, roles, configuration: true);
            Assert.Same(measure, parent.Measures["author"]);
            Assert.Equal("SUM('Sales'[Amount])", measure.Expression);
            Assert.Equal("business description", measure.Description);
            Assert.Equal("business folder", measure.DisplayFolder);
            Assert.True(measure.IsHidden);
        }
        parent.Measures.Add(new TOM.Measure { Name = "user", Expression = "[author]" });
        // 旧禁用项因引用被保留时仍不变成模板常量, 仅切换模式并更新本轮清单.
        ScaffoldTemplateMeasureMode.Apply(database.Model, [], before, roles, configuration: true);
        Assert.Equal("SUM('Sales'[Amount])", measure.Expression);
        Assert.True(measure.IsHidden);
    }

    /// <summary>已生成的 Config 查值禁用后切 TMDL, 表达式保持原样且隐藏参数表提供内嵌数据源.</summary>
    [Fact]
    public void DisabledConfigLookupRetainsFormulaAndSnapshotWhenSwitchingToTmdl()
    {
        using var database = Database();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "old")], "config", roles, configuration: true);
        var measure = database.Model.Tables["Measure"].Measures["author"];
        var expression = measure.Expression;
        ScaffoldTemplateMeasureMode.Apply(database.Model, [Definition("author", "next") with { Enabled = false }], "tmdl", roles, configuration: true);
        Assert.Equal(expression, measure.Expression);
        Assert.True(measure.IsHidden);
        var config = database.Model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
        Assert.True(config.IsHidden);
        var partition = ((TOM.MPartitionSource)config.Partitions.Single().Source).Expression;
        Assert.Contains("#table", partition);
        Assert.DoesNotContain("File.Contents", partition);
    }

    /// <summary>最小有效 TOM 定义不执行外部源或服务器写入.</summary>
    private static TOM.Database Database() => new() { Name = "overwrite", CompatibilityLevel = 1606, Model = new TOM.Model() };

    /// <summary>构造完整六字段请求, 名称和值为本用例明确输入.</summary>
    private static TemplateMeasureDefinition Definition(string name, string value) => new(name, value, "Uncategorized", true, "", "");
}
