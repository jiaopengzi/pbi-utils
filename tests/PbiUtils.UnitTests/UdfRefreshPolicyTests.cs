// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfRefreshPolicyTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 核验 UDF 清理区分真实增量刷新 M 查询与存活或未知 DAX 根, 不修改输入模型.

using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

/// <summary>只在独立完整 TOM 上分析函数引用, 不调用引擎或写入用户文件.</summary>
public sealed class UdfRefreshPolicyTests
{
    /// <summary>SDK 可省略空 tables, 仅含函数的模型仍能安全分析, 不要求存在日期或普通数据表.</summary>
    [Fact]
    public void FunctionOnlyModelSupportsCleanup()
    {
        using var database = new TOM.Database { Name = "Isolated", CompatibilityLevel = 1702, Model = new TOM.Model() };
        database.Model.Functions.Add(new TOM.Function { Name = "Lib.Unused", Expression = "() => 1" });
        ProjectSnapshot snapshot = new("isolated.pbip", "p", "b", "BIM", "", "", null, ModelSnapshotMapper.Map(database), [], [], []);
        var result = UdfModelLint.Analyze(database, snapshot);
        Assert.True(result.Complete);
        Assert.Equal("Lib.Unused", Assert.Single(result.Unused));
    }

    /// <summary>BasicRefreshPolicy 的两种查询明确为 M, 同名函数文本不构成 DAX 引用, 真 DAX 和未知语法继续保护.</summary>
    /// <param name="field">实际刷新策略字段及查询形状.</param>
    /// <param name="consumer">模型中的真实 DAX 消费者, 或未知 DAX 根.</param>
    [Theory]
    [InlineData("source", "none")]
    [InlineData("polling", "none")]
    [InlineData("source-call", "none")]
    [InlineData("polling-call", "none")]
    [InlineData("source", "used")]
    [InlineData("source", "unknown")]
    public void RealPolicyQueriesDoNotBecomeDaxRoots(string field, string consumer)
    {
        using var database = new TOM.Database { Name = "Isolated", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "T" };
        database.Model.Tables.Add(table);
        database.Model.Functions.Add(new TOM.Function { Name = "Lib.Unused", Expression = "() => 1" });
        table.RefreshPolicy = new TOM.BasicRefreshPolicy
        {
            SourceExpression = field.StartsWith("source", StringComparison.Ordinal)
                ? field == "source-call" ? "Lib.Unused()" : "let Source = #table({\"Value\"}, {{1}}) in Source" : null,
            PollingExpression = field.StartsWith("polling", StringComparison.Ordinal)
                ? field == "polling-call" ? "Lib.Unused()" : "#datetime(2024, 1, 1, 0, 0, 0)" : null
        };
        if (consumer != "none") table.Measures.Add(new TOM.Measure { Name = "Root", Expression = consumer == "used" ? "Lib.Unused()" : "#table({}, {})" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        ProjectSnapshot snapshot = new("isolated.pbip", "p", "b", "BIM", "", "", null, ModelSnapshotMapper.Map(database), [], [], []);

        var result = UdfModelLint.Analyze(database, snapshot);

        Assert.Equal(consumer != "unknown", result.Complete);
        if (consumer == "none") Assert.Equal("Lib.Unused", Assert.Single(result.Unused));
        else Assert.Empty(result.Unused);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }
}
