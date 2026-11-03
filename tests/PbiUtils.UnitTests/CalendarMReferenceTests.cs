// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarMReferenceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证日期裁剪区分真实 M 查询依赖与局部记录字段, 未知词法和非 SDK 假容器保持保护.

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用完整独立 TOM 核对依赖保护, 不调用 Power Query 或外部数据源.</summary>
public sealed class CalendarMReferenceTests
{
    /// <summary>M 的局部 record 字段不隐式绑定同名日期列, 仅真实 SDK 声明的位置可按 M 解释.</summary>
    [Theory]
    [InlineData("named")]
    [InlineData("partition")]
    [InlineData("source")]
    [InlineData("polling")]
    public void LocalRecordFieldsDoNotLockUnrelatedCalendar(string source)
    {
        using var database = Database();
        var table = Generated(database);
        AddM(database, source, "let record = [month = 1] in record[month]");
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(["Dates"], CalendarColumnProtection.RequiredFields(table).Order());
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>真实查询、固定函数、转义名称、动态环境或不明词法继续保护完整列集.</summary>
    [Theory]
    [InlineData("named", "Calendar")]
    [InlineData("partition", "#\"Calendar\"")]
    [InlineData("named", "#\"Cal#(0065)ndar\"")]
    [InlineData("named", "#\"PbiUtils#(0043)alendar\"(2024, 2024)")]
    [InlineData("source", "let x = Calendar in x[Month]")]
    [InlineData("polling", "Table.RowCount(Calendar)")]
    [InlineData("named", "#shared")]
    [InlineData("partition", "#sections")]
    [InlineData("named", "Expression /* split */ . #\"Eval#(0075)ate\"(\"1\")")]
    [InlineData("source", "\"unterminated")]
    public void ActualAndUnprovableMReferencesKeepAllColumns(string source, string expression)
    {
        using var database = Database();
        var table = Generated(database);
        AddM(database, source, expression);
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal(63, CalendarColumnProtection.RequiredFields(table).Count);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>真实 DAX 的未限定列仍是模型依赖, 不因同一模型存在 M 查询而漏检.</summary>
    [Fact]
    public void DaxUnqualifiedColumnKeepsItsSemanticField()
    {
        using var database = Database();
        var table = Generated(database);
        table.Measures.Add(new TOM.Measure { Name = "MonthValue", Expression = "MAX([Month])" });
        AddM(database, "named", "let record = [month = 1] in record[month]");
        Assert.Equal(["Dates", "Month"], CalendarColumnProtection.RequiredFields(table).Order());
    }

    /// <summary>Q&A 中同名 kind/type/refreshPolicy 容器不是 SDK M 定义, 不能掩盖真实列表达式.</summary>
    [Theory]
    [InlineData("{\"kind\":\"m\",\"expression\":\"[Month]\"}")]
    [InlineData("{\"type\":\"m\",\"expression\":\"[Month]\"}")]
    [InlineData("{\"refreshPolicy\":{\"policyType\":\"basic\",\"sourceExpression\":\"[Month]\"}}")]
    public void MetadataCannotImpersonateSdkMContainers(string body)
    {
        using var database = Database();
        var table = Generated(database);
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = "{\"Unknown\":" + body + "}" }
        });
        Assert.Contains("Month", CalendarColumnProtection.RequiredFields(table));
    }

    /// <summary>按真实 SDK 容器增加一个 M 表达式, 其他来源拒绝作为测试错误.</summary>
    private static void AddM(TOM.Database database, string source, string expression)
    {
        if (source == "named") database.Model.Expressions.Add(new TOM.NamedExpression { Name = "Unrelated", Kind = TOM.ExpressionKind.M, Expression = expression });
        else
        {
            var other = new TOM.Table { Name = "Other" }; database.Model.Tables.Add(other);
            if (source == "partition") other.Partitions.Add(new TOM.Partition { Name = "Other", Source = new TOM.MPartitionSource { Expression = expression } });
            else if (source is "source" or "polling") other.RefreshPolicy = new TOM.BasicRefreshPolicy
            {
                SourceExpression = source == "source" ? expression : null,
                PollingExpression = source == "polling" ? expression : null
            };
            else throw new ArgumentException("未知测试来源", nameof(source));
        }
    }

    /// <summary>生成真实完整日期字段和 M 函数, 由 SDK 提供确切映射.</summary>
    private static TOM.Table Generated(TOM.Database database)
    {
        var table = new TOM.Table { Name = "Calendar" }; database.Model.Tables.Add(table);
        ScaffoldCalendarWriter.Write(table, new ScaffoldCalendarOptions("full", 2024, 2024, table.Name, NumberedColumns: false), true, null, [], [], default);
        return table;
    }

    /// <summary>创建独立高兼容级别模型, 不读写文件.</summary>
    private static TOM.Database Database() => new() { Name = "calendar-m", CompatibilityLevel = 1702, Model = new TOM.Model() };
}
