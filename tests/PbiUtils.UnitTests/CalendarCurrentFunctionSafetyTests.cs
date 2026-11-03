// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarCurrentFunctionSafetyTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证固定日期函数的当前绑定安全验证、重复配置和用户名称保护.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarCurrentFunctionSafetyTests
{
    private const string FixedName = "PbiUtilsCalendar";

    /// <summary>只更新已核验绑定函数, 切换语言、编号、字段和年份不产生第二个函数或删除未知查询.</summary>
    [Fact]
    public void CurrentFunctionUpdatesKeepUnknownQueries()
    {
        using var database = CurrentCalendar();
        var table = database.Model.Tables["Dates"];
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "PbiUtilsCalendar_user", Kind = TOM.ExpressionKind.M, Expression = "() => 42" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { Fields = ["Dates", "Year"], NumberedColumns = false };
        var preview = CalendarProgramPlanner.Preview(database, options, "en-us", default);
        Assert.Equal(FixedName, preview.FunctionName);
        Assert.StartsWith(FixedName + "(", preview.InvocationExpression);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
        foreach (var locale in new[] { "en-us", "zh-cn", "en-us" })
        {
            ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default, locale);
            ModelConfigurationOwnership.Mark(table, table.Annotations);
            Assert.True(ScaffoldCalendarWriter.IsSelfContained(table));
            Assert.Equal(FixedName, ScaffoldCalendarWriter.FunctionBinding(table)!.FunctionName);
            Assert.Equal(2, database.Model.Expressions.Count);
            Assert.Equal("() => 42", database.Model.Expressions["PbiUtilsCalendar_user"].Expression);
            Assert.StartsWith(FixedName + "(", ((TOM.MPartitionSource)table.Partitions[0].Source).Expression);
            options = options with { StartYear = 2023, Fields = [.. ScaffoldCalendarFields.Simple], NumberedColumns = true };
        }
    }

    /// <summary>当前绑定存在时仍不得覆盖固定名的用户表、命名查询或其他表的 M 分区, 预览失败保留原模型.</summary>
    [Theory]
    [InlineData("query")]
    [InlineData("table")]
    [InlineData("partition")]
    public void PreviewRejectsEveryFixedNameNamespaceCollision(string kind)
    {
        using var database = CurrentCalendar();
        if (kind == "query") { database.Model.Expressions.Remove(database.Model.Expressions[FixedName]); database.Model.Expressions.Add(new TOM.NamedExpression { Name = FixedName.ToLowerInvariant(), Kind = TOM.ExpressionKind.M, Expression = "() => 42" }); }
        else
        {
            var other = new TOM.Table { Name = kind == "table" ? FixedName.ToLowerInvariant() : "UserTable" };
            database.Model.Tables.Add(other);
            if (kind == "partition") other.Partitions.Add(new TOM.Partition { Name = FixedName.ToLowerInvariant(), Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table({}, {})" } });
        }
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates");
        var error = Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, options, "zh-cn", default));
        Assert.Equal("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>函数、表与绑定每一项事实都保持原完整校验, 不能因更新固定名而为改动内容背书.</summary>
    [Theory]
    [InlineData("function")]
    [InlineData("table")]
    [InlineData("binding")]
    public void PreviewRejectsTamperedOwnership(string kind)
    {
        using var database = CurrentCalendar();
        var table = database.Model.Tables["Dates"];
        if (kind == "function") database.Model.Expressions[FixedName].Expression += "\n// user";
        else if (kind == "table") table.Description = "user";
        else
        {
            var annotation = table.Annotations[ScaffoldCalendarWriter.OptionsAnnotation];
            var document = JsonNode.Parse(annotation.Value)!;
            document["Function"]!["DefinitionHash"] = new string('0', 64);
            annotation.Value = document.ToJsonString();
            ModelConfigurationOwnership.Mark(table, table.Annotations);
        }
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, new("simple", 2024, 2024, "Dates"), "zh-cn", default));
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>有文件和无文件准备入口冻结相同固定代码; 取消和冻结失败不改变草稿, 成功也只替换私有候选.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparedCurrentFunctionPreservesDraftBoundaries(bool fileBased)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-calendar-fixed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = PbipFixture.Create(root);
            var file = Path.Combine(root, "model", "model.bim");
            using var database = CurrentCalendar();
            await File.WriteAllTextAsync(file, TOM.JsonSerializer.SerializeDatabase(database));
            var baseline = await new ProjectReader().ReadAsync(entry, default);
            if (!fileBased) baseline = baseline with { ProjectPath = "model://fixed-calendar", Connection = new("desktop", "fixture") };
            var options = ScaffoldCalendarWriter.ReadOptions(database.Model.Tables["Dates"]) with { Fields = ["Dates", "Year"] };
            var source = TOM.JsonSerializer.SerializeDatabase(database);
            using var compiler = new ModelConfigurationCompiler();
            var prepared = await compiler.PrepareAsync(new(Calendar: options, Locale: "en-us"), baseline, default);
            Assert.Equal(FixedName, Assert.Single(prepared.Configuration!.CalendarOutputs).Function!.FunctionName);
            using var draft = new MemoryModelDraft(baseline, database.Clone(), (_, _, _) => throw new InvalidOperationException("不得保存"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draft.ApplyAsync(prepared, new(true)));
            Assert.Same(baseline, draft.Snapshot);
            Assert.Equal(FixedName, draft.Snapshot.Model.Tables.Single(table => table.Name == "Dates").CalendarCapability!.FunctionName);
            var expected = prepared.Configuration.CalendarOutputs[0];
            using var mismatch = new ModelConfigurationPlan(baseline.BaselineId, "digest", new(Calendar: options, Locale: "en-us"), new([], []), [], fileBased ? root : null,
                calendarOutputs: [expected with { Function = expected.Function! with { DefinitionHash = new string('0', 64) } }]);
            var failure = await Assert.ThrowsAsync<AppException>(() => draft.ApplyAsync(new("configuration.apply", "configuration", "config", "digest", Configuration: mismatch), default));
            Assert.Equal("CONFIGURATION_CALENDAR_MAPPING_CHANGED", failure.Code);
            Assert.Same(baseline, draft.Snapshot);
            await draft.ApplyAsync(prepared, default);
            Assert.Equal(FixedName, draft.Snapshot.Model.Tables.Single(table => table.Name == "Dates").CalendarCapability!.FunctionName);
            Assert.True(draft.Snapshot.HasPendingChanges);
            Assert.Equal(source, TOM.JsonSerializer.SerializeDatabase(database));
            Assert.Equal(source, await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>第二张日期表不能覆盖已绑定的固定函数, 只读预览和私有草稿都保留原对象.</summary>
    [Fact]
    public async Task SecondCalendarRejectsWithoutChangingPrivateDraft()
    {
        using var database = CurrentCalendar();
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "OtherDates");
        Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, options, "zh-cn", default));
        var baseline = new ProjectSnapshot("model://current", "project", "baseline", "TMDL", "PBIR", "1.0", null,
            ModelSnapshotMapper.Map(database), [], [], []);
        using var draft = new MemoryModelDraft(baseline, database.Clone(), (_, _, _) => throw new InvalidOperationException("不得保存"));
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(Calendar: options), new([], []), [], null);
        var error = await Assert.ThrowsAsync<AppException>(() => draft.ApplyAsync(new("configuration.apply", "configuration", "config", "digest", Configuration: plan), default));
        Assert.Equal("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", error.Code);
        Assert.Same(baseline, draft.Snapshot);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>非当前 schema 或生成器只作为不支持的输入拒绝, 不重建或升级内容.</summary>
    [Theory]
    [InlineData(1, "calendar-m-v5")]
    [InlineData(2, "calendar-m-v5")]
    [InlineData(3, "calendar-m-v3")]
    [InlineData(3, "calendar-m-v4")]
    public void UnsupportedCalendarDefinitionDoesNotAuthorizeOverwrite(int schema, string generator)
    {
        using var database = CurrentCalendar();
        var table = database.Model.Tables["Dates"];
        var annotation = table.Annotations[ScaffoldCalendarWriter.OptionsAnnotation];
        var document = JsonNode.Parse(annotation.Value)!;
        document["SchemaVersion"] = schema;
        document["GeneratorVersion"] = generator;
        annotation.Value = document.ToJsonString();
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => CalendarProgramPlanner.Preview(database, new("simple", 2024, 2024, "Dates"), "zh-cn", default));
        Assert.Equal("CONFIGURATION_CALENDAR_OPTIONS_REQUIRED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>构造当前 v5 schema3 日期表及函数, 不读取或写入用户模型.</summary>
    private static TOM.Database CurrentCalendar(string tableName = "Dates")
    {
        var database = new TOM.Database { Name = "current", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var table = new TOM.Table { Name = tableName }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new("simple", 2024, 2024, tableName), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        return database;
    }
}
