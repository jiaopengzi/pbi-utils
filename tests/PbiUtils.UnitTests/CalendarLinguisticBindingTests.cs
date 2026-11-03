// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarLinguisticBindingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证日期列缩减时 Q&A 官方绑定别名、关系作用域和未知路径的依赖保护.

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实 SDK 内存定义上的只读引用核验, 不替代引擎或报表文件验收.</summary>
public sealed class CalendarLinguisticBindingTests
{
    /// <summary>实体及关系的纯表绑定只声明作用域, 两种官方名称格式都允许移除无引用列.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StandardScopeBindingsDoNotLockColumns(bool relationship, bool common)
    {
        using var database = Database();
        var table = Generated(database);
        AddMetadata(database, Content(Binding(common ? "Table" : "ConceptualEntity", "Calendar"), relationship));
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(["Dates"], CalendarColumnProtection.RequiredFields(table).Order());
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>真实属性绑定按实际列别名保护语义, 不把大写常见格式或关系绑定忽略为纯表声明.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PropertyBindingsProtectActualColumn(bool relationship, bool common)
    {
        using var database = Database();
        var table = Generated(database);
        var binding = common ? "{\"Table\":\"Calendar\",\"Column\":\"C28_Year\"}"
            : "{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C28_Year\"}";
        AddMetadata(database, Content(binding, relationship));
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(["Dates", "Year"], CalendarColumnProtection.RequiredFields(table).Order());
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>混用表或属性别名不通过字段优先级掩盖另一条引用, 模糊绑定保守保留全部列.</summary>
    [Theory]
    [InlineData("{\"ConceptualEntity\":\"Other\",\"Table\":\"Calendar\",\"Column\":\"C28_Year\"}")]
    [InlineData("{\"table\":\"Other\",\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C13_Month\",\"Column\":\"C28_Year\"}")]
    [InlineData("{\"Table\":\"Calendar\",\"column\":\"C13_Month\",\"Column\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C13_Month\",\"Measure\":\"C28_Year\"}")]
    public void ConflictingBindingAliasesKeepAllColumns(string binding)
    {
        using var database = Database();
        var table = Generated(database);
        AddMetadata(database, Content(binding, false));
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(63, CalendarColumnProtection.RequiredFields(table).Count);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>编号覆盖枚举全部表/属性绑定别名, 不能由优先字段掩盖另一条目标日期列引用.</summary>
    [Theory]
    [InlineData("{\"table\":\"Other\",\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C28_Year\"}")]
    [InlineData("{\"table\":\"Other\",\"Table\":\"Calendar\",\"column\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Other\",\"Table\":\"Calendar\",\"Column\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"column\":\"Unknown\",\"ConceptualProperty\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"Column\":\"Unknown\",\"ConceptualProperty\":\"C28_Year\"}")]
    [InlineData("{\"ConceptualEntity\":\"Calendar\",\"Column\":\"Unknown\",\"Measure\":\"C28_Year\"}")]
    public void ConflictingBindingAliasesPreventNumberingOverride(string binding)
    {
        using var database = Database();
        var table = Generated(database);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        AddMetadata(database, Content(binding, false));
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var options = ScaffoldCalendarWriter.ReadOptions(table) with { NumberedColumns = false, Fields = [.. ScaffoldCalendarFields.All.Select(value => value.Id)] };
        var error = Assert.Throws<AppException>(() => ScaffoldCalendarWriter.Write(table, options, true, null, [], [], default));
        Assert.Equal("CONFIGURATION_CALENDAR_RENAME_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>数组、未知路径、假元数据根和扩展绑定不获得标准作用域豁免, 未知绑定保持原模型.</summary>
    [Theory]
    [InlineData("array")]
    [InlineData("unknown")]
    [InlineData("nested-metadata")]
    [InlineData("extended")]
    [InlineData("null-property")]
    [InlineData("unknown-root")]
    public void MalformedScopeBindingsKeepAllColumns(string shape)
    {
        using var database = Database();
        var table = Generated(database);
        var binding = Binding("ConceptualEntity", "Calendar");
        var content = shape switch
        {
            "array" => "{\"Relationships\":[{\"Binding\":" + binding + "}]}",
            "unknown" => "{\"Unknown\":" + Content(binding, true) + "}",
            "nested-metadata" => "{\"Unknown\":{\"linguisticMetadata\":{\"content\":" + Content(binding, false) + "}}}",
            "unknown-root" => "{\"ManualDescription\":\"user-maintained\"," + Content(binding, true)[1..],
            "extended" => Content("{\"ConceptualEntity\":\"Calendar\",\"Unknown\":true}", true),
            _ => Content("{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":null}", true)
        };
        AddMetadata(database, content);
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(63, CalendarColumnProtection.RequiredFields(table).Count);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>完整真实列绑定在数组下仍须识别, 清除路径豁免不能同时清除引用检查.</summary>
    [Fact]
    public void ArrayPropertyBindingKeepsColumnProtection()
    {
        using var database = Database();
        var table = Generated(database);
        AddMetadata(database, "{\"Unknown\":[{\"Table\":\"Calendar\",\"Column\":\"C28_Year\"}]}");
        Assert.Contains("Year", CalendarColumnProtection.RequiredFields(table));
    }

    /// <summary>连续裁剪使用同一真实保护结果, 保留 Q&A 所引用列的对象身份并保持手工元数据原文.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedFieldSelectionKeepsReferencedColumnsAndMetadata(bool common)
    {
        using var database = Database();
        var table = Generated(database);
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        var content = Content(common ? "{\"Table\":\"Calendar\",\"Column\":\"C28_Year\"}"
            : "{\"ConceptualEntity\":\"Calendar\",\"ConceptualProperty\":\"C28_Year\"}", true);
        AddMetadata(database, content);
        var year = table.Columns["C28_Year"];
        var lineage = year.LineageTag;
        foreach (var fields in new[] { ScaffoldCalendarFields.Simple, ScaffoldCalendarFields.Recommended, ScaffoldCalendarFields.Simple })
        {
            ScaffoldCalendarWriter.Write(table, new ScaffoldCalendarOptions("full", 2024, 2024, table.Name, Fields: [.. fields]), true, null, [], [], default);
            ModelConfigurationOwnership.Mark(table, table.Annotations);
            Assert.Same(year, table.Columns[year.Name]);
            Assert.Equal(lineage, year.LineageTag);
            Assert.Equal(content, database.Model.Cultures.Single().LinguisticMetadata.Content);
            Assert.Equal(fields.Append("Year").Distinct().Order(), ScaffoldCalendarWriter.ReadMapping(table).Select(column => column.Field).Order());
            Assert.Single(database.Model.Expressions);
        }
    }

    /// <summary>使用官方实体或关系字典路径封装绑定, 不生成数组兼容旁路.</summary>
    private static string Content(string binding, bool relationship) => relationship
        ? "{\"Relationships\":{\"calendar\":{\"Binding\":" + binding + "}}}"
        : "{\"Entities\":{\"calendar\":{\"Definition\":{\"Binding\":" + binding + "}}}}";

    /// <summary>构造只含一个表身份的 JSON 绑定, 参数只来自本测试固定数据.</summary>
    private static string Binding(string key, string table) => "{\"" + key + "\":\"" + table + "\"}";

    /// <summary>为完整 SDK 模型增加固定 Q&A 内容, 不调用外部来源或查询.</summary>
    private static void AddMetadata(TOM.Database database, string content) => database.Model.Cultures.Add(new TOM.Culture
    {
        Name = "en-US",
        LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = content }
    });

    /// <summary>创建高兼容级别私有模型, 不读写文件或引擎.</summary>
    private static TOM.Database Database() => new() { Name = "qna-calendar", CompatibilityLevel = 1702, Model = new TOM.Model() };

    /// <summary>生成完整 63 字段和共享 M 函数, 供真实列映射验证.</summary>
    private static TOM.Table Generated(TOM.Database database)
    {
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new ScaffoldCalendarOptions("full", 2024, 2024, table.Name), true, null, [], [], default);
        return table;
    }
}
