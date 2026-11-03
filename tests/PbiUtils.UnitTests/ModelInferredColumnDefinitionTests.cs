// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelInferredColumnDefinitionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整定义区分 SDK 推导列类型与显式类型, 保持克隆和生成归属一致.

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

public sealed class ModelInferredColumnDefinitionTests
{
    /// <summary>SDK 克隆省略推导类型而 CopyTo 保留目标推导类型时, 完整定义与表归属仍一致.</summary>
    /// <param name="kind">具有推导类型标记的实际 TOM 列种类.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("calculated")]
    [InlineData("calculatedTableColumn")]
    public void InferredColumnTypesRemainStableAcrossCloneAndCopy(string kind)
    {
        using var live = Create(kind, true);
        var table = live.Model.Tables["Values"];
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        using var candidate = live.Clone();

        Assert.Equal(TOM.DataType.Unknown, candidate.Model.Tables["Values"].Columns["Value"].DataType);
        Assert.Equal(ModelConfigurationReadback.Fingerprint(live), ModelConfigurationReadback.Fingerprint(candidate));
        ModelConfigurationOwnership.Require(candidate.Model.Tables["Values"], candidate.Model.Tables["Values"].Annotations);

        candidate.Model.Tables["Values"].Description = "approved";
        candidate.Model.CopyTo(live.Model);

        Assert.Equal(TOM.DataType.Int64, live.Model.Tables["Values"].Columns["Value"].DataType);
        Assert.Equal("approved", live.Model.Tables["Values"].Description);
        Assert.Equal(ModelConfigurationReadback.Fingerprint(candidate), ModelConfigurationReadback.Fingerprint(live));
    }

    /// <summary>显式类型仍是可写定义, 不能因修正推导类型误报而忽略其变化.</summary>
    /// <param name="kind">需要保留显式类型的实际 TOM 列种类.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("calculated")]
    [InlineData("calculatedTableColumn")]
    public void ExplicitColumnTypeChangesRemainPartOfCompleteDefinition(string kind)
    {
        using var original = Create(kind, false);
        using var changed = original.Clone();
        changed.Model.Tables["Values"].Columns["Value"].DataType = TOM.DataType.String;

        Assert.NotEqual(ModelConfigurationReadback.Fingerprint(original), ModelConfigurationReadback.Fingerprint(changed));
    }

    /// <summary>引擎重新推导得到的类型不改变可写定义, 推导策略与原列定义继续保留.</summary>
    /// <param name="kind">引擎可能重新推导类型的实际 TOM 列种类.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("calculated")]
    [InlineData("calculatedTableColumn")]
    public void RuntimeInferredTypeChangesDoNotChangeWritableDefinition(string kind)
    {
        using var original = Create(kind, true);
        using var changed = original.Clone();
        var column = changed.Model.Tables["Values"].Columns["Value"];
        column.DataType = TOM.DataType.String;
        column.IsDataTypeInferred = true;

        Assert.Equal(ModelConfigurationReadback.Fingerprint(original), ModelConfigurationReadback.Fingerprint(changed));
    }

    /// <summary>即使尚无推导类型, 自动推导和显式类型策略仍必须产生不同定义.</summary>
    /// <param name="kind">需要保留推导策略的实际 TOM 列种类.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("calculated")]
    [InlineData("calculatedTableColumn")]
    public void InferencePolicyRemainsPartOfDefinitionWhenTypeIsUnknown(string kind)
    {
        using var inferred = Create(kind, true);
        using var explicitType = inferred.Clone();
        explicitType.Model.Tables["Values"].Columns["Value"].IsDataTypeInferred = false;
        using var unknownInferred = inferred.Clone();

        Assert.Equal(TOM.DataType.Unknown, explicitType.Model.Tables["Values"].Columns["Value"].DataType);
        Assert.NotEqual(ModelConfigurationReadback.Fingerprint(unknownInferred), ModelConfigurationReadback.Fingerprint(explicitType));
    }

    /// <summary>创建不含用户定义的最小 SDK 模型, 包含真实列类型和可独立核验的业务元数据.</summary>
    /// <param name="kind">SDK 支持的数据列、计算列或计算表列类型.</param>
    /// <param name="inferred">是否由 SDK 标记为引擎推导类型.</param>
    /// <returns>由用例独占并负责释放的完整私有数据库.</returns>
    private static TOM.Database Create(string kind, bool inferred)
    {
        var column = kind == "calculated" ? "\"expression\":\"42\"" : "\"sourceColumn\":\"Value\"";
        return TOM.JsonSerializer.DeserializeDatabase($$$"""
            {"name":"isolated","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Values","columns":[{"name":"Value","type":"{{{kind}}}","dataType":"int64","isDataTypeInferred":{{{inferred.ToString().ToLowerInvariant()}}},{{{column}}}}],"partitions":[{"name":"Values","mode":"import","source":{"type":"calculated","expression":"ROW(\"Value\", 42)"}}],"annotations":[{"name":"Business.Note","value":"preserve"}]}]}}
            """);
    }
}
