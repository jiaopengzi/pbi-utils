// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarColumnProtectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证已有日期表裁剪前真实模型依赖的语义字段保护.

using PbiUtils.Infrastructure.Calendar;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用内存 TOM 对象验证依赖保护, 不代表报表文件或引擎执行验收.</summary>
public sealed class CalendarColumnProtectionTests
{
    private static readonly string[] dateMonthYearFields = ["Dates", "Month", "Year"];
    private static readonly string[] structuralReferenceFields = ["Dates", "Day", "DayOfYear", "Month", "Year"];
    private static readonly string[] dateDayMonthYearFields = ["Dates", "Day", "Month", "Year"];
    private static readonly string[] dateYearFields = ["Dates", "Year"];

    /// <summary>生成函数、分区及自身排序不会将未引用的完整 63 字段全部锁住.</summary>
    [Fact]
    public void GeneratedCalendarKeepsOnlyDateKeyWithoutExternalReferences()
    {
        using var database = Database();
        var table = Generated(database);
        Assert.Equal(63, table.Columns.Count);
        Assert.NotNull(table.Columns["C18_YearMonthM"].SortByColumn);
        Assert.Equal(["Dates"], Required(table).Order());
    }

    /// <summary>服务端推导的 RowNumber 键不属于日期输出语义, 不能使已有日历全部锁定.</summary>
    [Fact]
    public void InferredRowNumberKeyDoesNotLockAllDateFields()
    {
        using var database = Database();
        var table = Generated(database);
        var json = JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(database))!;
        ((JsonArray)json["model"]!["tables"]![0]!["columns"]!).Add(JsonNode.Parse("""{"type":"rowNumber","name":"RowNumber","dataType":"int64","isHidden":true,"isUnique":true,"isKey":true,"isNullable":false}"""));
        using var runtime = TOM.JsonSerializer.DeserializeDatabase(json.ToJsonString());
        Assert.Equal(["Dates"], Required(runtime.Model.Tables[table.Name]).Order());
    }

    /// <summary>关系两端和日期别名按实际输出列绑定回语义, 不依赖 SourceColumn 的名称推断.</summary>
    [Fact]
    public void RelationshipAndKeyProtectMappedAliases()
    {
        using var database = Database();
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        table.Columns.Add(new TOM.DataColumn { Name = "日期", SourceColumn = "source-date", DataType = TOM.DataType.DateTime });
        table.Columns.Add(new TOM.DataColumn { Name = "C28_Year", SourceColumn = "source-year", DataType = TOM.DataType.Int64, IsKey = true });
        table.Columns.Add(new TOM.DataColumn { Name = "Month", SourceColumn = "source-month", DataType = TOM.DataType.Int64 });
        var fact = new TOM.Table { Name = "Fact" }; database.Model.Tables.Add(fact);
        fact.Columns.Add(new TOM.DataColumn { Name = "MonthKey", DataType = TOM.DataType.Int64 });
        database.Model.Relationships.Add(new TOM.SingleColumnRelationship
        { Name = "month", FromColumn = fact.Columns[0], ToColumn = table.Columns["Month"] });
        Assert.Equal(dateMonthYearFields, Required(table).Order());
    }

    /// <summary>层级、分组和外表排序保留所引用的列, 自身无关排序仍可随列移除.</summary>
    [Fact]
    public void StructuralReferencesProtectOnlyTheirActualColumns()
    {
        using var database = Database();
        var table = Generated(database);
        var hierarchy = new TOM.Hierarchy { Name = "Calendar path" };
        table.Hierarchies.Add(hierarchy);
        hierarchy.Levels.Add(new TOM.Level { Name = "Year", Column = table.Columns["C28_Year"], Ordinal = 0 });
        table.Columns["C13_Month"].RelatedColumnDetails = new TOM.RelatedColumnDetails();
        table.Columns["C13_Month"].RelatedColumnDetails.GroupByColumns.Add(new TOM.GroupByColumn { GroupingColumn = table.Columns["C45_DayOfYear"] });
        var fact = new TOM.Table { Name = "Fact" }; database.Model.Tables.Add(fact);
        fact.Columns.Add(new TOM.DataColumn { Name = "Sorted", DataType = TOM.DataType.Int64, SortByColumn = table.Columns["C02_Day"] });
        Assert.Equal(structuralReferenceFields, Required(table).Order());
    }

    /// <summary>明确列引用只保护对应字段, 引号转义、大小写和未限定列引用均被识别.</summary>
    [Theory]
    [InlineData("MAX('Calendar''s data'[C28_Year])", "Year")]
    [InlineData("MAX('calendar''s DATA' [C13_Month])", "Month")]
    [InlineData("[C02_Day] + 1", "Day")]
    [InlineData("VAR result = 1 RETURN [C28_Year]", "Year")]
    [InlineData("NOT [C02_Day]", "Day")]
    public void SpecificDaxReferencesKeepTheirSemanticField(string expression, string field)
    {
        using var database = Database();
        var table = Generated(database, "Calendar's data");
        var measures = new TOM.Table { Name = "MeasureValues" }; database.Model.Tables.Add(measures);
        measures.Measures.Add(new TOM.Measure { Name = "Value", Expression = expression });
        Assert.Equal(new[] { "Dates", field }.Order(), Required(table).Order());
    }

    /// <summary>无法证明列粒度的 DAX、M 和计算表整体引用保守保护全部已有字段.</summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("m")]
    [InlineData("calculated-table")]
    [InlineData("udf")]
    [InlineData("rls")]
    [InlineData("calculation-group")]
    public void WholeTableReferencesKeepAllFields(string kind)
    {
        using var database = Database();
        var table = Generated(database);
        var other = new TOM.Table { Name = "Other" }; database.Model.Tables.Add(other);
        switch (kind)
        {
            case "measure": other.Measures.Add(new TOM.Measure { Name = "Value", Expression = "COUNTROWS('Calendar')" }); break;
            case "m": database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Copy", Kind = TOM.ExpressionKind.M, Expression = "let source = #\"Calendar\" in source" }); break;
            case "calculated-table": other.Partitions.Add(new TOM.Partition { Name = "Copy", Source = new TOM.CalculatedPartitionSource { Expression = "Calendar" } }); break;
            case "udf": database.Model.Functions.Add(new TOM.Function { Name = "CalendarRows", Expression = "() => COUNTROWS(Calendar)" }); break;
            case "rls":
                var role = new TOM.ModelRole { Name = "Reader" }; database.Model.Roles.Add(role);
                role.TablePermissions.Add(new TOM.TablePermission { Name = "Other", Table = other, FilterExpression = "COUNTROWS(Calendar) > 0" }); break;
            case "calculation-group":
                other.CalculationGroup = new TOM.CalculationGroup();
                other.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Current", Expression = "COUNTROWS(Calendar)" }); break;
        }
        Assert.Equal(ScaffoldCalendarWriter.ReadMapping(table).Select(column => column.Field).Order(), Required(table).Order());
    }

    /// <summary>计算列、计算表输出和 RLS 的精确列引用不扩张为整表锁定.</summary>
    [Fact]
    public void CalculatedAndSecurityColumnReferencesKeepTheirFields()
    {
        using var database = Database();
        var table = Generated(database);
        var other = new TOM.Table { Name = "Other" }; database.Model.Tables.Add(other);
        other.Columns.Add(new TOM.CalculatedColumn { Name = "Month", DataType = TOM.DataType.Int64, Expression = "MAX(Calendar[C13_Month])" });
        other.Columns.Add(new TOM.CalculatedTableColumn { Name = "Year", DataType = TOM.DataType.Int64, SourceColumn = "Calendar[C28_Year]" });
        var role = new TOM.ModelRole { Name = "Reader" }; database.Model.Roles.Add(role);
        role.TablePermissions.Add(new TOM.TablePermission { Name = "Other", Table = other, FilterExpression = "MAX(Calendar[C02_Day]) > 0" });
        Assert.Equal(dateDayMonthYearFields, Required(table).Order());
    }

    /// <summary>列级对象安全权限通过真实表列引用保留, 不因序列化仅有 name 而遗漏.</summary>
    [Fact]
    public void ObjectLevelSecurityColumnPermissionKeepsItsField()
    {
        using var database = Database();
        var table = Generated(database);
        var role = new TOM.ModelRole { Name = "Restricted" }; database.Model.Roles.Add(role);
        var permission = new TOM.TablePermission { Name = table.Name, Table = table }; role.TablePermissions.Add(permission);
        permission.ColumnPermissions.Add(new TOM.ColumnPermission { Name = "C28_Year", Column = table.Columns["C28_Year"], MetadataPermission = TOM.MetadataPermission.None });
        Assert.Equal(dateYearFields, Required(table).Order());
    }

    /// <summary>M 的双引号转义表名仍是真实整体引用, 不能因名称编码漏掉依赖.</summary>
    [Fact]
    public void EscapedMTableNameKeepsAllFields()
    {
        using var database = Database();
        var table = Generated(database, "A\"B");
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Copy", Kind = TOM.ExpressionKind.M, Expression = "#\"A\"\"B\"" });
        Assert.Equal(63, Required(table).Count);
    }

    /// <summary>路径、字符串和注释中的表名不会成为真实模型依赖, 引号表标识符仍须保护.</summary>
    [Theory]
    [InlineData("\"C:/calendar/test\"", false)]
    [InlineData("VAR value = 1 RETURN \"Calendar\"", false)]
    [InlineData("1 // Calendar\n+ 1 -- Calendar\n/* Calendar */", false)]
    [InlineData("#\"Calendar\"", true)]
    public void NonReferenceTokensDoNotLockFields(string expression, bool referencesTable)
    {
        using var database = Database();
        var table = Generated(database);
        // # 引号表名属于 M, 其余固定用例为 DAX 路径常量、VAR 或含 -- 的注释; 不伪装成未知 M.
        if (referencesTable) database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Parameter", Kind = TOM.ExpressionKind.M, Expression = expression });
        else table.Measures.Add(new TOM.Measure { Name = "Parameter", Expression = expression });
        Assert.Equal(referencesTable ? 63 : 1, Required(table).Count);
    }

    /// <summary>动态 M 查找和求值无法证明列粒度, 即使表名仅在字符串中也保留全部字段.</summary>
    [Theory]
    [InlineData("Record.Field(#shared, \"Calendar\")")]
    [InlineData("Expression.Evaluate(\"Calendar\", #shared)")]
    public void DynamicMReferencesKeepAllFields(string expression)
    {
        using var database = Database();
        var table = Generated(database);
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "DynamicCopy", Kind = TOM.ExpressionKind.M, Expression = expression });
        Assert.Equal(63, Required(table).Count);
    }

    /// <summary>外部查询调用日期生成函数会依赖其完整返回形状, 不因函数名区别于表名而遗漏.</summary>
    [Fact]
    public void ExternalCalendarFunctionCallKeepsAllFields()
    {
        using var database = Database();
        var table = Generated(database);
        var function = ScaffoldCalendarWriter.FunctionBinding(table)!.FunctionName;
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Copy", Kind = TOM.ExpressionKind.M, Expression = "#\"" + function + "\"(2024, 2024)" });
        Assert.Equal(63, Required(table).Count);
    }

    /// <summary>透视、翻译和 Q&A 的真实列绑定按字段保护, 不把注解内容作为依赖.</summary>
    [Fact]
    public void PerspectiveTranslationAndLinguisticBindingsProtectTheirColumns()
    {
        using var database = Database();
        var table = Generated(database);
        var perspective = new TOM.Perspective { Name = "Calendar view" }; database.Model.Perspectives.Add(perspective);
        var visible = new TOM.PerspectiveTable { Name = table.Name, Table = table }; perspective.PerspectiveTables.Add(visible);
        visible.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Name = "C28_Year", Column = table.Columns["C28_Year"] });
        var culture = new TOM.Culture { Name = "en-US" }; database.Model.Cultures.Add(culture);
        culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = table.Columns["C13_Month"], Property = TOM.TranslatedProperty.Caption, Value = "Month" });
        culture.LinguisticMetadata = new TOM.LinguisticMetadata
        {
            ContentType = TOM.ContentType.Json,
            Content = "{\"Entities\":{\"day\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C02_Day\"}}}}}"
        };
        table.Annotations.Add(new TOM.Annotation { Name = "Unrelated", Value = "COUNTROWS(Calendar)" });
        Assert.Equal(dateDayMonthYearFields, Required(table).Order());
    }

    /// <summary>透视 IncludeAll 与无法粒度化的 Q&A 表绑定保守保留完整字段集.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WholeMetadataTableBindingsKeepAllFields(bool perspectiveAll)
    {
        using var database = Database();
        var table = Generated(database);
        if (perspectiveAll)
        {
            var perspective = new TOM.Perspective { Name = "Calendar view" }; database.Model.Perspectives.Add(perspective);
            perspective.PerspectiveTables.Add(new TOM.PerspectiveTable { Name = table.Name, Table = table, IncludeAll = true });
        }
        else
        {
            var culture = new TOM.Culture { Name = "en-US" }; database.Model.Cultures.Add(culture);
            culture.LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = "{\"Binding\":{\"ConceptualEntity\":\"Calendar\"}}" };
        }
        Assert.Equal(63, Required(table).Count);
    }

    /// <summary>标准 Q&A 表实体的扩展成员或显式空属性不适用单一表声明例外, 未知绑定继续保守保护.</summary>
    [Theory]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"UnknownBinding\":\"value\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":null}")]
    public void ExtendedQnaTableBindingsStillKeepAllFields(string binding)
    {
        using var database = Database();
        var table = Generated(database);
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"calendar\":{\"Definition\":{\"Binding\":" + binding + "}}}}"
            }
        });
        Assert.Equal(63, Required(table).Count);
    }

    /// <summary>其他完整表标识符内的同名前缀和明确属于其他表的同名列不会锁定日期字段.</summary>
    [Theory]
    [InlineData("SUM('Calendar Facts'[Amount])")]
    [InlineData("let source = #\"Calendar Facts\" in source")]
    [InlineData("SUM(Other[C28_Year])")]
    [InlineData("SUM('Other' [C28_Year])")]
    [InlineData("SUM('Calendar Facts'[C28_Year])")]
    public void OtherQualifiedReferencesDoNotLockCalendarFields(string expression)
    {
        using var database = Database();
        var table = Generated(database);
        foreach (var name in new[] { "Other", "Calendar Facts" })
        {
            var other = new TOM.Table { Name = name }; database.Model.Tables.Add(other);
            other.Columns.Add(new TOM.DataColumn { Name = "C28_Year", SourceColumn = "C28_Year", DataType = TOM.DataType.Int64 });
        }
        // 只将真实 let 查询放入 M 容器, DAX 限定表列由 SDK 的度量表达式承载.
        if (expression.StartsWith("let ", StringComparison.Ordinal)) database.Model.Expressions.Add(new TOM.NamedExpression { Name = "OtherQuery", Kind = TOM.ExpressionKind.M, Expression = expression });
        else table.Measures.Add(new TOM.Measure { Name = "OtherQuery", Expression = expression });
        Assert.Equal(["Dates"], Required(table).Order());
    }

    /// <summary>构造独立高兼容级别内存模型, 不访问文件或服务.</summary>
    private static TOM.Database Database() => new() { Name = "dependencies", CompatibilityLevel = 1702, CompatibilityMode = Microsoft.AnalysisServices.CompatibilityMode.PowerBI, Model = new TOM.Model() };

    /// <summary>生成真实完整日历与其绑定 M 函数, 返回已写入的日期表.</summary>
    private static TOM.Table Generated(TOM.Database database, string name = "Calendar")
    {
        var table = new TOM.Table { Name = name }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, name), true, null, [], [], default);
        return table;
    }

    /// <summary>调用产品依赖策略返回真实语义集合, 供行为断言共用.</summary>
    private static IReadOnlySet<string> Required(TOM.Table table) => CalendarColumnProtection.RequiredFields(table);
}
