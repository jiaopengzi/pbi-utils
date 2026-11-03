// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarNumberingOverrideTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证已有生成日期列的编号覆盖、引用同步及歧义保护.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅操作测试独占的内存 TOM 与临时 PBIP, 不代表真实 Desktop 提交或报表验收.</summary>
public sealed class CalendarNumberingOverrideTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-numbering-" + Guid.NewGuid().ToString("N"));

    /// <summary>显式编号可往返覆盖已有标准列, 对象身份、关系、层级、排序及限定 DAX 引用保持一致.</summary>
    [Fact]
    public void ExplicitNumberingOverridesExistingNamesAndKeepsReferences()
    {
        using var database = Generated();
        var model = database.Model;
        var table = model.Tables["Calendar's data"];
        var date = table.Columns["Dates"];
        var year = table.Columns["Year"];
        var month = table.Columns["Month"];
        var monthLabel = table.Columns["MonthCN"];
        var consumer = new TOM.Table { Name = "Consumer" }; model.Tables.Add(consumer);
        consumer.Columns.Add(new TOM.DataColumn { Name = "Year", DataType = TOM.DataType.Int64 });
        consumer.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.DateTime });
        var relation = new TOM.SingleColumnRelationship { Name = "date", FromColumn = consumer.Columns["DateKey"], ToColumn = date };
        model.Relationships.Add(relation);
        var hierarchy = new TOM.Hierarchy { Name = "Calendar" }; table.Hierarchies.Add(hierarchy);
        hierarchy.Levels.Add(new TOM.Level { Name = "Year", Column = year, Ordinal = 0 });
        const string original = "MAX('Calendar''s data'[Year]) + MAX(Consumer[Year]) /* 'Calendar''s data'[Year] */ + LEN(\"'Calendar''s data'[Year]\")";
        var measure = new TOM.Measure { Name = "CurrentYear", Expression = original }; consumer.Measures.Add(measure);
        var column = new TOM.CalculatedColumn { Name = "CalendarYear", Expression = "MAX('Calendar''s data'[Year])", DataType = TOM.DataType.Int64 };
        consumer.Columns.Add(column);
        var role = new TOM.ModelRole { Name = "Reader" }; model.Roles.Add(role);
        var permission = new TOM.TablePermission { Name = table.Name, Table = table, FilterExpression = "'Calendar''s data'[Year] >= 2024" };
        role.TablePermissions.Add(permission);
        permission.ColumnPermissions.Add(new TOM.ColumnPermission { Name = year.Name, Column = year, MetadataPermission = TOM.MetadataPermission.None });
        var perspective = new TOM.Perspective { Name = "Dates" }; model.Perspectives.Add(perspective);
        var visible = new TOM.PerspectiveTable { Name = table.Name, Table = table }; perspective.PerspectiveTables.Add(visible);
        visible.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Name = year.Name, Column = year });
        var culture = new TOM.Culture { Name = "en-US" }; model.Cultures.Add(culture);
        culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = year, Property = TOM.TranslatedProperty.Caption, Value = "Calendar year" });
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var lineage = year.LineageTag;
        foreach (var numbered in new[] { true, false })
        {
            var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = numbered };
            ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
            ModelConfigurationOwnership.Mark(table, table.Annotations);
            var dateName = ScaffoldCalendarFields.ColumnName("Dates", numbered);
            var yearName = ScaffoldCalendarFields.ColumnName("Year", numbered);
            Assert.Same(date, table.Columns[dateName]);
            Assert.Same(year, table.Columns[yearName]);
            Assert.Equal(lineage, year.LineageTag);
            Assert.Equal(yearName, Assert.IsType<TOM.DataColumn>(year).SourceColumn);
            Assert.Same(date, relation.ToColumn);
            Assert.Same(year, hierarchy.Levels[0].Column);
            Assert.Same(month, monthLabel.SortByColumn);
            Assert.Same(year, permission.ColumnPermissions[0].Column);
            Assert.Equal(yearName, permission.ColumnPermissions[0].Name);
            Assert.Same(year, visible.PerspectiveColumns[0].Column);
            Assert.Equal(yearName, visible.PerspectiveColumns[0].Name);
            Assert.Same(year, culture.ObjectTranslations[0].Object);
            Assert.Equal($"MAX('Calendar''s data'[{yearName}]) + MAX(Consumer[Year]) /* 'Calendar''s data'[Year] */ + LEN(\"'Calendar''s data'[Year]\")", measure.Expression);
            Assert.Equal($"MAX('Calendar''s data'[{yearName}])", column.Expression);
            Assert.Equal($"'Calendar''s data'[{yearName}] >= 2024", permission.FilterExpression);
            Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
        }
    }

    /// <summary>真实下游 M 查询不能由日期字段编号推断重写规则, 在改名之前明确拒绝并保留原定义.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalMQueryPreventsUnsafeNumberingOverride(bool defaultSelection)
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Consumer", Kind = TOM.ExpressionKind.M, Expression = "Table.SelectColumns(#\"Calendar's data\", {\"Year\"})" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        if (defaultSelection) options = options with { Kind = "full", Fields = null };
        var error = Assert.Throws<AppException>(() => ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default));
        Assert.Equal("CONFIGURATION_CALENDAR_RENAME_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>同名未限定列可能来自其他表的行上下文, 不猜测其绑定或修改业务公式.</summary>
    [Fact]
    public void AmbiguousUnqualifiedColumnPreventsNumberingOverride()
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var consumer = new TOM.Table { Name = "Consumer" }; database.Model.Tables.Add(consumer);
        consumer.Columns.Add(new TOM.DataColumn { Name = "Year", DataType = TOM.DataType.Int64 });
        consumer.Measures.Add(new TOM.Measure { Name = "Ambiguous", Expression = "MAX([Year])" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        var error = Assert.Throws<AppException>(() => ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default));
        Assert.Equal("CONFIGURATION_CALENDAR_RENAME_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>虚拟表声明的局部列不属于物理模型, 未限定同名引用无法证明绑定时在改名前拒绝.</summary>
    [Theory]
    [InlineData("SUMX(ADDCOLUMNS({1}, \"Year\", 2024), [Year])")]
    [InlineData("SUMX(SELECTCOLUMNS({1}, \"Year\", 2024), [Year])")]
    [InlineData("SUMX(ROW(\"Year\", 2024), [Year])")]
    [InlineData("SUMX(DATATABLE(\"Year\", INTEGER, {{2024}}), [Year])")]
    [InlineData("SUMX(SUMMARIZE('Calendar''s data', \"Year\", 2024), [Year])")]
    [InlineData("SUMX(SUMMARIZECOLUMNS(\"Year\", 2024), [Year])")]
    [InlineData("VAR Dates = ADDCOLUMNS({1}, \"Year\", 2024) RETURN SUMX(Dates, [Year])")]
    public void VirtualColumnAliasPreventsUnqualifiedNumberingRewrite(string expression)
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var consumer = new TOM.Table { Name = "Consumer" }; database.Model.Tables.Add(consumer);
        consumer.Measures.Add(new TOM.Measure { Name = "VirtualYear", Expression = expression });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        var error = Assert.Throws<AppException>(() => ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default));
        Assert.Equal("CONFIGURATION_CALENDAR_RENAME_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>字符串和注释里的虚拟列函数名不产生作用域, 普通唯一列引用仍按原规则安全改名.</summary>
    [Theory]
    [InlineData("MAX([Year]) + LEN(\"ADDCOLUMNS SELECTCOLUMNS ROW DATATABLE SUMMARIZE SUMMARIZECOLUMNS GROUPBY\") /* ADDCOLUMNS({1}, \"Year\", 2024) */")]
    [InlineData("VAR Note = \"ROW\" RETURN MAX([Year]) // SELECTCOLUMNS({1}, \"Year\", 2024)\n")]
    public void VirtualFunctionNamesInStringsAndCommentsDoNotPreventNumberingRewrite(string expression)
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var consumer = new TOM.Table { Name = "Consumer" }; database.Model.Tables.Add(consumer);
        var measure = new TOM.Measure { Name = "CurrentYear", Expression = expression }; consumer.Measures.Add(measure);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        Assert.Equal(expression.Replace("MAX([Year])", "MAX([C28_Year])", StringComparison.Ordinal), measure.Expression);
        Assert.True(table.Columns.ContainsName("C28_Year"));
    }

    /// <summary>虚拟表不改变显式物理表列的绑定, 有限定的日期引用仍可同步且局部列保持原样.</summary>
    [Fact]
    public void QualifiedDateColumnCanRenameInsideVirtualTableExpression()
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var consumer = new TOM.Table { Name = "Consumer" }; database.Model.Tables.Add(consumer);
        var measure = new TOM.Measure { Name = "CurrentYear", Expression = "SUMX(ADDCOLUMNS({1}, \"Local\", 2024), [Local] + MAX('Calendar''s data'[Year]))" };
        consumer.Measures.Add(measure);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        Assert.Equal("SUMX(ADDCOLUMNS({1}, \"Local\", 2024), [Local] + MAX('Calendar''s data'[C28_Year]))", measure.Expression);
    }

    /// <summary>离线与无文件宿主均冻结更名后的实际列映射, 预览、候选和函数指纹保持一致.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NumberingOverrideFreezesTheActualFinalMapping(bool fileBased)
    {
        var entry = PbipFixture.Create(root);
        var modelFile = Path.Combine(root, "model", "model.bim");
        using var database = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(modelFile));
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, table.Name, Fields: ["Dates", "Year"], NumberedColumns: false);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        await File.WriteAllTextAsync(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        if (!fileBased) snapshot = snapshot with { ProjectPath = "model://numbering", Connection = new("desktop", "fixture") };
        options = options with { NumberedColumns = true };
        var preview = CalendarProgramPlanner.Preview(database, options, "zh-cn", default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Calendar: options), snapshot, default);
        var output = Assert.Single(prepared.Configuration!.CalendarOutputs);
        Assert.Equal(["C01_Dates", "C28_Year"], output.Columns.Select(value => value.Name));
        ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        Assert.Equal(output.Columns.Select(value => value.Name), table.Columns.Select(value => value.Name));
        Assert.Equal(CalendarProgramPlanner.CodeHash(preview.FunctionExpression), output.Function!.DefinitionHash);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
    }

    /// <summary>日期单独覆盖及同批旧日期绑定均跟随已证明的原列对象, 期间读取和后续集合 CRUD 保持有效.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedPeriodsFollowTheRenamedDateObject(bool sameBatch)
    {
        using var database = Generated();
        var model = database.Model;
        var table = model.Tables["Calendar's data"];
        var consumer = new TOM.Table { Name = "Consumer" }; model.Tables.Add(consumer);
        consumer.Measures.Add(new TOM.Measure { Name = "Sales", Expression = "1" });
        var periods = new ScaffoldPeriodOptions(table.Name, "Dates", consumer.Name, ["Sales"], ["MTD"]);
        ScaffoldPeriodMeasures.Apply(model, periods, configuration: true);
        Assert.NotNull(ScaffoldPeriodConfiguration.Read(model));
        var calendar = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = true };
        var plan = new ModelConfigurationPlan("baseline", "digest", new(Calendar: calendar, Periods: sameBatch ? periods : null), new([], []), [], null);
        ModelConfigurationMutation.Apply(database, plan);
        var actual = ScaffoldPeriodConfiguration.Read(model);
        Assert.NotNull(actual);
        Assert.Equal("C01_Dates", actual.DateColumn);
        Assert.All(ScaffoldPeriodMeasures.ReadOwned(model), output => Assert.True(ModelConfigurationOwnership.IsOwned(output.Measure, output.Measure.Annotations)));
        ScaffoldPeriodMeasures.Apply(model, actual with { Templates = [] }, configuration: true);
        Assert.Equal("Sales", Assert.Single(consumer.Measures).Name);
    }

    /// <summary>构造标准无编号的已归属日期表, 不访问任何真实模型.</summary>
    private static TOM.Database Generated()
    {
        var database = new TOM.Database { Name = "numbering", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Calendar's data" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, Fields: ["Dates", "Year", "Month", "MonthCN"], NumberedColumns: false), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        return database;
    }

    /// <summary>仅删除本测试拥有的临时目录, 不触及用户模型和配置.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
