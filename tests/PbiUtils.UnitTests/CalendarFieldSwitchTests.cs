// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarFieldSwitchTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证已有日期表连续切换字段方案时能力、预览、冻结输出和实际候选一致.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarFieldSwitchTests : IDisposable
{
    private static readonly string[] dateKeyFields = ["Dates"];
    private static readonly string[] dateMonthYearFields = ["Dates", "Month", "Year"];

    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-calendar-switch-" + Guid.NewGuid().ToString("N"));

    /// <summary>离线 PBIP 和宿主快照的连续缩减/扩展必须使用同一精确列规划, 不保留未选择的无引用列.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingCalendarCanSwitchFullSimpleRecommendedAndBack(bool fileBased)
    {
        var entry = PbipFixture.Create(root);
        var modelFile = Path.Combine(root, "model", "model.bim");
        using var database = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(modelFile));
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, table.Name,
            Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)], NumberedColumns: false);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var functionName = ScaffoldCalendarWriter.FunctionBinding(table)!.FunctionName;
        using var compiler = new ModelConfigurationCompiler();
        foreach (var fields in new[] { ScaffoldCalendarFields.Simple, ScaffoldCalendarFields.Recommended, ScaffoldCalendarFields.All.Select(field => field.Id).ToArray() })
        {
            await File.WriteAllTextAsync(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
            var snapshot = await new ProjectReader().ReadAsync(entry, default);
            if (!fileBased) snapshot = snapshot with { ProjectPath = "model://calendar-switch", Connection = new("desktop", "fixture") };
            var capability = snapshot.Model.Tables.Single(value => value.Name == table.Name).CalendarCapability!;
            Assert.Equal("existing", capability.Mode);
            Assert.Equal(dateKeyFields, capability.LockedFields.Select(value => value.Field));
            var selected = options with { Fields = [.. fields], NumberedColumns = true };
            var preview = CalendarProgramPlanner.Preview(database, selected, "zh-cn", default);
            var prepared = await compiler.PrepareAsync(new(Calendar: selected), snapshot, default);
            var output = Assert.Single(prepared.Configuration!.CalendarOutputs);
            var prior = table.Columns.ToDictionary(column => column.Name, column => column.LineageTag, StringComparer.Ordinal);
            ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName,
                prepared.Property, prepared.Value, Configuration: prepared.Configuration));
            Assert.Equal(fields.Order(), ScaffoldCalendarWriter.ReadMapping(table).Select(value => value.Field).Order());
            Assert.Equal(fields.Count, table.Columns.OfType<TOM.DataColumn>().Count());
            Assert.Equal(table.Columns.Select(column => column.Name), output.Columns.Select(column => column.Name));
            Assert.Equal(table.Columns.OfType<TOM.DataColumn>().Select(column => column.SourceColumn), output.Columns.Select(column => column.SourceColumn));
            Assert.Equal(functionName, output.Function!.FunctionName);
            Assert.Equal(CalendarProgramPlanner.CodeHash(preview.FunctionExpression), output.Function.DefinitionHash);
            Assert.Equal(CalendarProgramPlanner.CodeHash(preview.InvocationExpression), output.Function.InvocationHash);
            Assert.All(table.Columns.Where(column => prior.ContainsKey(column.Name)), column => Assert.Equal(prior[column.Name], column.LineageTag));
            Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
            Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
            Assert.Single(database.Model.Expressions, expression => expression.Name == functionName);
        }
    }

    /// <summary>kind 默认选集与显式选集使用同一缩减/编号规划, 不重复追加日期语义或替换现有列对象.</summary>
    [Fact]
    public void DefaultShortcutNarrowsAndRenamesWithoutDuplicateFields()
    {
        using var database = new TOM.Database { Name = "current", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        var full = new ScaffoldCalendarOptions("full", 2024, 2024, table.Name, NumberedColumns: true);
        ScaffoldCalendarWriter.Write(table, full, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var date = table.Columns["C01_Dates"];
        var lineage = date.LineageTag;
        var simple = full with { Kind = "simple", Fields = null, NumberedColumns = false };
        ScaffoldCalendarWriter.Write(table, simple, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.Equal(ScaffoldCalendarFields.Simple.Order(), table.Columns.Select(column => column.Name).Order());
        Assert.Equal(5, table.Columns.Count);
        Assert.Same(date, table.Columns["Dates"]);
        Assert.Equal(lineage, date.LineageTag);
        Assert.All(table.Columns.OfType<TOM.DataColumn>(), column => Assert.Equal(column.Name, column.SourceColumn));
        Assert.Equal(5, ScaffoldCalendarWriter.ReadMapping(table).Select(column => column.Field).Distinct().Count());
        ScaffoldCalendarWriter.Write(table, full, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.Equal(63, table.Columns.Count);
        Assert.Same(date, table.Columns["C01_Dates"]);
        Assert.Equal(lineage, date.LineageTag);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
    }

    /// <summary>编号选项空值保持标准列和业务别名的真实名称/源列, 扩展新增列仍使用当前默认编号.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnspecifiedNumberingPreservesActualColumnsAndOnlyDefaultsNewFields(bool numbered, bool alias)
    {
        using var database = new TOM.Database { Name = "current", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        if (alias) table.Columns.Add(new TOM.DataColumn { Name = "日期", SourceColumn = "日期", DataType = TOM.DataType.DateTime });
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, NumberedColumns: numbered), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var prior = ScaffoldCalendarWriter.CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal)).Columns.ToDictionary(column => column.Field, StringComparer.Ordinal);
        var date = table.Columns[prior["Dates"].Name];
        var simple = new ScaffoldCalendarOptions("simple", 2025, 2025, table.Name);
        ScaffoldCalendarWriter.Write(table, simple, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.Equal(5, table.Columns.Count);
        Assert.All(ScaffoldCalendarFields.Simple, field =>
        {
            Assert.Equal(prior[field].SourceColumn, Assert.IsType<TOM.DataColumn>(table.Columns[prior[field].Name]).SourceColumn);
        });
        Assert.Same(date, table.Columns[prior["Dates"].Name]);
        Assert.Null(ScaffoldCalendarWriter.ReadOptions(table).NumberedColumns);
        ScaffoldCalendarWriter.Write(table, new("full", 2025, 2025, table.Name), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.Equal(63, table.Columns.Count);
        Assert.Same(date, table.Columns[prior["Dates"].Name]);
        Assert.Contains(table.Columns, column => column.Name == "C28_Year");
        Assert.Equal(63, ScaffoldCalendarWriter.ReadMapping(table).Select(column => column.Field).Distinct().Count());
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
    }

    /// <summary>默认简单选集在文件与宿主快照中保留真实关系、DAX 和业务别名, 预览/冻结/候选完整代码保持一致.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DefaultShortcutPreservesReferencesAndAliasesAcrossFrozenPlan(bool fileBased, bool alias)
    {
        var entry = PbipFixture.Create(root);
        var modelFile = Path.Combine(root, "model", "model.bim");
        using var database = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(modelFile));
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        if (alias) table.Columns.Add(new TOM.DataColumn { Name = "日期", SourceColumn = "日期", DataType = TOM.DataType.DateTime });
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, table.Name);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var date = table.Columns[alias ? "日期" : "C01_Dates"];
        var month = table.Columns["C13_Month"];
        var year = table.Columns["C28_Year"];
        var fact = new TOM.Table { Name = "CalendarFacts" }; database.Model.Tables.Add(fact);
        fact.Columns.Add(new TOM.DataColumn { Name = "MonthKey", DataType = TOM.DataType.Int64 });
        var relation = new TOM.SingleColumnRelationship { Name = "month", FromColumn = fact.Columns[0], ToColumn = month };
        database.Model.Relationships.Add(relation);
        var consumer = new TOM.Measure { Name = "CalendarYear", Expression = "MAX('Calendar'[C28_Year])" }; fact.Measures.Add(consumer);
        await File.WriteAllTextAsync(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        if (!fileBased) snapshot = snapshot with { ProjectPath = "model://calendar-default-references", Connection = new("desktop", "fixture") };
        var selected = options with { Kind = "simple", Fields = null, NumberedColumns = false };
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var preview = CalendarProgramPlanner.Preview(database, selected, "zh-cn", default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Calendar: selected), snapshot, default);
        var output = Assert.Single(prepared.Configuration!.CalendarOutputs);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
        Assert.Equal(7, output.Columns.Count);
        ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        Assert.Equal(7, table.Columns.Count);
        Assert.Same(date, table.Columns[alias ? "日期" : "Dates"]);
        Assert.Same(month, table.Columns["Month"]);
        Assert.Same(year, table.Columns["Year"]);
        Assert.Same(month, relation.ToColumn);
        Assert.Equal("MAX('Calendar'[Year])", consumer.Expression);
        Assert.Equal(7, ScaffoldCalendarWriter.ReadMapping(table).Select(column => column.Field).Distinct().Count());
        Assert.Equal(output.Columns.Select(column => column.Name), table.Columns.Select(column => column.Name));
        Assert.Equal(CalendarProgramPlanner.CodeHash(preview.FunctionExpression), output.Function!.DefinitionHash);
        Assert.Equal(CalendarProgramPlanner.CodeHash(preview.InvocationExpression), output.Function.InvocationHash);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
    }

    /// <summary>绕过网页直接请求简单方案也不能移除实际关系端点或被度量值引用的列, 预览与冻结输出必须一致.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReferencedFieldsSurviveExplicitNarrowing(bool fileBased)
    {
        var entry = PbipFixture.Create(root);
        var modelFile = Path.Combine(root, "model", "model.bim");
        using var database = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(modelFile));
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, table.Name,
            Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)], NumberedColumns: false);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var fact = new TOM.Table { Name = "CalendarFacts" }; database.Model.Tables.Add(fact);
        fact.Columns.Add(new TOM.DataColumn { Name = "MonthKey", DataType = TOM.DataType.Int64 });
        database.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "month", FromColumn = fact.Columns[0], ToColumn = table.Columns["Month"] });
        fact.Measures.Add(new TOM.Measure { Name = "CalendarYear", Expression = "MAX('Calendar'[Year])" });
        await File.WriteAllTextAsync(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        if (!fileBased) snapshot = snapshot with { ProjectPath = "model://calendar-references", Connection = new("desktop", "fixture") };
        Assert.Equal(dateMonthYearFields, snapshot.Model.Tables.Single(value => value.Name == table.Name).CalendarCapability!.LockedFields.Select(value => value.Field));
        var selected = options with { Fields = [.. ScaffoldCalendarFields.Simple] };
        var preview = CalendarProgramPlanner.Preview(database, selected, "zh-cn", default);
        using var compiler = new ModelConfigurationCompiler();
        var prepared = await compiler.PrepareAsync(new(Calendar: selected), snapshot, default);
        var output = Assert.Single(prepared.Configuration!.CalendarOutputs);
        Assert.Equal(7, output.Columns.Count);
        ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName,
            prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        Assert.Same(table.Columns["Month"], Assert.IsType<TOM.SingleColumnRelationship>(database.Model.Relationships[0]).ToColumn);
        Assert.True(table.Columns.ContainsName("Year"));
        Assert.Equal(CalendarProgramPlanner.CodeHash(preview.FunctionExpression), output.Function!.DefinitionHash);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
    }

    /// <summary>只删除本用例创建的临时工程, 不触及用户模型或配置.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
