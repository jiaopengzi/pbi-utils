// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarMirrorFieldProtectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证生成日期镜像的完整归属、实际别名、外部依赖和引擎推导字段边界.

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>内存 TOM 验证不代表引擎提交、刷新或保存验收.</summary>
public sealed class CalendarMirrorFieldProtectionTests
{
    /// <summary>经镜像访问的关系、表达式和元数据仍保护其真实源语义, 整表消费者保留完整输出形状.</summary>
    [Theory]
    [InlineData("column", "Year")]
    [InlineData("relationship", "Month")]
    [InlineData("hierarchy", "Year")]
    [InlineData("permission", "Year")]
    [InlineData("perspective", "Month")]
    [InlineData("translation", "Year")]
    [InlineData("key", "Year")]
    [InlineData("whole", null)]
    public void MirrorDependenciesProtectTheirActualSourceFields(string kind, string? field)
    {
        using var database = Generated();
        var source = database.Model.Tables["Calendar's data"];
        var mirror = database.Model.Tables["Mirror"];
        var consumer = new TOM.Table { Name = "Consumer" }; database.Model.Tables.Add(consumer);
        switch (kind)
        {
            case "column": consumer.Measures.Add(new TOM.Measure { Name = "Year", Expression = "MAX('Mirror'[alias-Year])" }); break;
            case "whole": consumer.Measures.Add(new TOM.Measure { Name = "Count", Expression = "COUNTROWS('Mirror')" }); break;
            case "relationship":
                consumer.Columns.Add(new TOM.DataColumn { Name = "Month", DataType = TOM.DataType.Int64 });
                database.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "month", FromColumn = consumer.Columns[0], ToColumn = mirror.Columns["alias-Month"] }); break;
            case "hierarchy":
                var hierarchy = new TOM.Hierarchy { Name = "Year" }; mirror.Hierarchies.Add(hierarchy);
                hierarchy.Levels.Add(new TOM.Level { Name = "Year", Column = mirror.Columns["alias-Year"], Ordinal = 0 });
                ModelConfigurationOwnership.Mark(mirror, mirror.Annotations); break;
            case "permission":
                var role = new TOM.ModelRole { Name = "Reader" }; database.Model.Roles.Add(role);
                var permission = new TOM.TablePermission { Name = mirror.Name, Table = mirror }; role.TablePermissions.Add(permission);
                permission.ColumnPermissions.Add(new TOM.ColumnPermission { Name = "alias-Year", Column = mirror.Columns["alias-Year"], MetadataPermission = TOM.MetadataPermission.None }); break;
            case "perspective":
                var perspective = new TOM.Perspective { Name = "Month" }; database.Model.Perspectives.Add(perspective);
                var visible = new TOM.PerspectiveTable { Name = mirror.Name, Table = mirror }; perspective.PerspectiveTables.Add(visible);
                visible.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Name = "alias-Month", Column = mirror.Columns["alias-Month"] }); break;
            case "translation":
                var culture = new TOM.Culture { Name = "en-US" }; database.Model.Cultures.Add(culture);
                culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = mirror.Columns["alias-Year"], Property = TOM.TranslatedProperty.Caption, Value = "Year" }); break;
            case "key": mirror.Columns["alias-Year"].IsKey = true; ModelConfigurationOwnership.Mark(mirror, mirror.Annotations); break;
        }
        Assert.NotNull(CalendarGeneratedMirror.Find(source));
        Assert.Equal(field is null ? ScaffoldCalendarFields.All.Select(value => value.Id).Order() : new[] { "Dates", field }.Order(),
            CalendarColumnProtection.RequiredFields(source).Order());
    }

    /// <summary>人工变更或未归属镜像不被同步, 原始整表引用继续保护源表全部字段.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedOrUnownedMirrorIsPreserved(bool changed)
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var mirror = database.Model.Tables["Mirror"];
        if (changed) mirror.Description = "用户修改";
        else mirror.Annotations.Remove(mirror.Annotations[ModelConfigurationOwnership.Stamp]);
        var before = TOM.JsonSerializer.SerializeObject(mirror);
        Assert.Null(CalendarGeneratedMirror.Find(table));
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, Fields: [.. ScaffoldCalendarFields.Simple]), true, null, [], [], default);
        Assert.Equal(63, table.Columns.OfType<TOM.DataColumn>().Count());
        Assert.Equal(before, TOM.JsonSerializer.SerializeObject(mirror));
    }

    /// <summary>含单引号的源名和镜像别名可连续裁剪和恢复, 保留列原对象身份且新增引用合法编码.</summary>
    [Fact]
    public void QuotedSourceAndMirrorAliasesSurviveRepeatedSelection()
    {
        using var database = Generated();
        var table = database.Model.Tables["Calendar's data"];
        var mirror = database.Model.Tables["Mirror"];
        var original = mirror.Columns["alias-Dates"];
        var numbered = true;
        foreach (var fields in new[] { ScaffoldCalendarFields.Simple, ScaffoldCalendarFields.Recommended, ScaffoldCalendarFields.All.Select(field => field.Id).ToArray() })
        {
            ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, Fields: [.. fields], NumberedColumns: numbered), true, null, [], [], default);
            ModelConfigurationOwnership.Mark(table, table.Annotations);
            Assert.Equal(fields.Count, mirror.Columns.OfType<TOM.CalculatedTableColumn>().Count());
            Assert.Same(original, mirror.Columns["alias-Dates"]);
            Assert.NotNull(CalendarGeneratedMirror.Find(table));
            Assert.All(mirror.Columns.OfType<TOM.CalculatedTableColumn>(), column => Assert.StartsWith("'Calendar''s data'[", column.SourceColumn));
            Assert.Contains("[" + ScaffoldCalendarFields.ColumnName("Dates", numbered) + "]", Assert.IsType<TOM.CalculatedTableColumn>(original).SourceColumn, StringComparison.Ordinal);
            numbered = !numbered;
        }
    }

    /// <summary>服务端 RowNumber 键既不锁全字段也不参与源列或镜像列修改, 完整可写归属仍有效.</summary>
    [Fact]
    public void InferredRowNumberKeysArePreservedWithoutLockingAllFields()
    {
        using var original = Generated();
        var json = JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(original))!;
        foreach (var table in json["model"]!["tables"]!.AsArray())
            table!["columns"]!.AsArray().Add(JsonNode.Parse("""{"type":"rowNumber","name":"RowNumber","dataType":"int64","isHidden":true,"isUnique":true,"isKey":true,"isNullable":false}"""));
        using var database = TOM.JsonSerializer.DeserializeDatabase(json.ToJsonString());
        var source = database.Model.Tables["Calendar's data"];
        var mirror = database.Model.Tables["Mirror"];
        Assert.Equal(["Dates"], CalendarColumnProtection.RequiredFields(source).Order());
        var sourceRowNumber = TOM.JsonSerializer.SerializeObject(source.Columns.OfType<TOM.RowNumberColumn>().Single());
        var mirrorRowNumber = TOM.JsonSerializer.SerializeObject(mirror.Columns.OfType<TOM.RowNumberColumn>().Single());
        ScaffoldCalendarWriter.Write(source, new("full", 2024, 2024, source.Name, Fields: [.. ScaffoldCalendarFields.Simple]), true, null, [], [], default);
        Assert.Equal(5, mirror.Columns.OfType<TOM.CalculatedTableColumn>().Count());
        Assert.Equal(sourceRowNumber, TOM.JsonSerializer.SerializeObject(source.Columns.OfType<TOM.RowNumberColumn>().Single()));
        Assert.Equal(mirrorRowNumber, TOM.JsonSerializer.SerializeObject(mirror.Columns.OfType<TOM.RowNumberColumn>().Single()));
    }

    /// <summary>构造完整带别名镜像并精确登记生成角色, 不访问用户模型或文件.</summary>
    private static TOM.Database Generated()
    {
        var database = new TOM.Database { Name = "mirror", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Calendar's data" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, table.Name, Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)], NumberedColumns: false), true, null, [], [], default);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var mirror = new TOM.Table { Name = "Mirror" }; database.Model.Tables.Add(mirror);
        foreach (var column in table.Columns.OfType<TOM.DataColumn>())
            mirror.Columns.Add(new TOM.CalculatedTableColumn { Name = "alias-" + column.Name, SourceColumn = "'Calendar''s data'[" + column.Name + "]", DataType = column.DataType });
        mirror.Partitions.Add(new TOM.Partition { Name = "Mirror", Mode = TOM.ModeType.Import, Source = new TOM.CalculatedPartitionSource { Expression = "'Calendar''s data'" } });
        ModelConfigurationOwnership.Mark(mirror, mirror.Annotations);
        ModelConfigurationOwnership.SetRoles(database.Model, new(1, new(StringComparer.Ordinal) { [table.Name] = "calendar", [mirror.Name] = "calendar-mirror" }, true, new(StringComparer.Ordinal)));
        return database;
    }
}
