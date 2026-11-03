// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarFunctionPersistenceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证日期命名函数和表调用的持久化、独立归属及迁移边界.

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarFunctionPersistenceTests
{
    /// <summary>新日期表以独立M查询返回函数, 表分区只保留调用; 不提升兼容级别.</summary>
    [Fact]
    public void CalendarUsesOwnedNamedFunctionAndDetectsItsTampering()
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates");
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var function = Assert.Single(database.Model.Expressions);
        Assert.Equal(TOM.ExpressionKind.M, function.Kind);
        Assert.Equal("Function", function.Annotations["PBI_ResultType"].Value);
        Assert.Equal("functions", function.QueryGroup?.Name);
        Assert.Same(database.Model.QueryGroups["functions"], function.QueryGroup);
        Assert.True(ModelConfigurationOwnership.IsOwned(function, function.Annotations));
        Assert.Contains("Value.ReplaceType", function.Expression, StringComparison.Ordinal);
        var invocation = Assert.IsType<TOM.MPartitionSource>(table.Partitions[0].Source).Expression;
        Assert.Contains(function.Name, invocation, StringComparison.Ordinal);
        Assert.DoesNotContain("List.Dates", invocation, StringComparison.Ordinal);
        Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
        function.Expression += "\n// changed";
        Assert.Throws<AppException>(() => ScaffoldCalendarWriter.IsSelfContained(table));
        Assert.Equal(1606, database.CompatibilityLevel);
    }

    /// <summary>固定函数仅属于一个日期表, 新的独立日期表不能覆盖其映射; 缺失函数不被补写掩盖.</summary>
    [Fact]
    public void MultipleCalendarsDoNotShareOrRecreateMissingFunction()
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, table.Name);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, options with { TableName = "OtherDates" }, "zh-cn", default));
        Assert.Single(database.Model.Expressions);
        Assert.Single(database.Model.Tables);
        database.Model.Expressions.Clear();
        Assert.Throws<AppException>(() => ScaffoldCalendarWriter.ReadOptions(database.Model.Tables["Dates"]));
    }

    /// <summary>M查询公共名称空间按大小写检查, 未知表或查询均不能被日期函数覆盖.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownCaseInsensitiveQueryNameConflictsAreRejected(bool isTable)
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates");
        var name = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options).Select(value => new ScaffoldCalendarColumn(value.Name, value.Field)).ToArray()).FunctionName.ToLowerInvariant();
        if (isTable) database.Model.Tables.Add(new TOM.Table { Name = name });
        else database.Model.Expressions.Add(new TOM.NamedExpression { Name = name, Kind = TOM.ExpressionKind.M, Expression = "() => 1" });
        var error = Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, options, "zh-cn", default));
        Assert.Equal("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", error.Code);
        Assert.Equal(isTable ? 1 : 0, database.Model.Tables.Count);
        Assert.Equal(isTable ? 0 : 1, database.Model.Expressions.Count);
    }

    /// <summary>目标日期表名也不得与用户M查询重名, 不能只核生成函数的名称.</summary>
    [Fact]
    public void TargetTableNameCannotShadowExistingMQuery()
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "dates", Kind = TOM.ExpressionKind.M, Expression = "1" });
        Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, new("simple", 2024, 2024, "Dates"), "zh-cn", default));
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(Calendar: new("simple", 2024, 2024, "Dates")), new([], []), [], null);
        Assert.Throws<AppException>(() => ModelConfigurationMutation.Apply(database, plan));
    }

    /// <summary>列形状一样也必须核对冻结函数代码hash; 失败保留原私有草稿.</summary>
    [Fact]
    public async Task FrozenFunctionHashMismatchDoesNotMutateDraft()
    {
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates");
        var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var output = ScaffoldCalendarWriter.CaptureOutput(table, table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal));
        var expected = output with { Function = output.Function! with { DefinitionHash = new string('0', 64) } };
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(Calendar: options), new([], []), [], null, calendarOutputs: [expected]);
        var baseline = new PbiUtils.Application.Models.ProjectSnapshot("model://test", "project", "baseline", "TMDL", "PBIR", "1.0", null,
            PbiUtils.Infrastructure.Models.ModelSnapshotMapper.Map(database), [], [], []);
        using var draft = new PbiUtils.Infrastructure.Models.Pbip.MemoryModelDraft(baseline, database.Clone(), (_, _, _) => throw new InvalidOperationException("不得保存"));
        var error = await Assert.ThrowsAsync<AppException>(() => draft.ApplyAsync(new("configuration.apply", "configuration", "config", "digest", Configuration: plan), default));
        Assert.Equal("CONFIGURATION_CALENDAR_MAPPING_CHANGED", error.Code);
        Assert.Same(baseline, draft.Snapshot);
        Assert.False(draft.Snapshot.HasPendingChanges);
    }
}
