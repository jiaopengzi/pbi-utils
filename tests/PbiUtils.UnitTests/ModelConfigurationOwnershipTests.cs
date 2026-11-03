// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationOwnershipTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实Desktop只读元数据不改变生成归属, 可写定义与lineage仍严格核验.

using PbiUtils.Infrastructure.Models.Configuration;
using System.Text.Json.Nodes;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Core;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

public sealed class ModelConfigurationOwnershipTests
{
    /// <summary>配置准备/克隆/实际保存生成相同身份, 不让随机lineage破坏冻结后精确重放.</summary>
    [Fact]
    public void EquivalentGeneratedObjectsReceiveDeterministicLineage()
    {
        using var first = Create(); using var second = Create();
        ModelConfigurationOwnership.Mark(first.Model.Tables["Owned"], first.Model.Tables["Owned"].Annotations);
        ModelConfigurationOwnership.Mark(second.Model.Tables["Owned"], second.Model.Tables["Owned"].Annotations);
        Assert.Equal(ModelConfigurationReadback.Fingerprint(first), ModelConfigurationReadback.Fingerprint(second));
    }

    /// <summary>血缘字段只在1540起支持, 低级别普通配置不得被动引入不支持属性.</summary>
    [Fact]
    public void LowCompatibilityDoesNotReceiveUnsupportedLineage()
    {
        using var database = Create(); database.CompatibilityLevel = 1500;
        var table = database.Model.Tables["Owned"]; ModelConfigurationOwnership.Mark(table, table.Annotations);
        Assert.True(string.IsNullOrEmpty(table.LineageTag)); Assert.True(string.IsNullOrEmpty(table.Columns["Value"].LineageTag));
        ModelConfigurationOwnership.Require(table, table.Annotations);
    }
    /// <summary>复现真实引擎新增时间、属性层次、rowNumber和度量推导类型后原stamp误失效.</summary>
    [Fact]
    public void ServerControlledMetadataPreservesOwnedDefinition()
    {
        using var database = Create(); var table = database.Model.Tables["Owned"];
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        using var live = WithRuntimeMetadata(database);
        ModelConfigurationOwnership.Require(live.Model.Tables["Owned"], live.Model.Tables["Owned"].Annotations);
        Assert.False(string.IsNullOrWhiteSpace(table.LineageTag));
        Assert.False(string.IsNullOrWhiteSpace(table.Columns["Value"].LineageTag));
        Assert.False(string.IsNullOrWhiteSpace(table.Measures["Owned.Measure"].LineageTag));
        Assert.Equal(ModelConfigurationReadback.Fingerprint(database), ModelConfigurationReadback.Fingerprint(live));
    }

    /// <summary>规范化只忽略SDK控制的运行字段, 真实表达式/分区/业务注解/已有血缘必须改变完整指纹且拒绝覆盖.</summary>
    [Theory]
    [InlineData("partition")]
    [InlineData("measure")]
    [InlineData("annotation")]
    [InlineData("lineage")]
    [InlineData("source-lineage")]
    [InlineData("column-type")]
    [InlineData("udf-expression")]
    [InlineData("udf-annotation")]
    public void DefinitionAndOwnershipRetainWritableSemantics(string change)
    {
        using var disk = Create(); var table = disk.Model.Tables["Owned"];
        var function = new TOM.Function { Name = "Owned.Function", Expression = "() => 42" }; disk.Model.Functions.Add(function);
        function.Annotations.Add(new TOM.Annotation { Name = "User.Note", Value = "keep" });
        table.Columns["Value"].SourceLineageTag = "original";
        ModelConfigurationOwnership.Mark(table, table.Annotations); ModelConfigurationOwnership.Mark(function, function.Annotations);
        using var live = WithRuntimeMetadata(disk); var current = live.Model.Tables["Owned"];
        TOM.MetadataObject target = current; ICollection<TOM.Annotation> annotations = current.Annotations;
        if (change == "partition") ((TOM.MPartitionSource)current.Partitions[0].Source).Expression = "#table({\"Value\"}, {{99}})";
        else if (change == "measure") current.Measures["Owned.Measure"].Expression = "99";
        else if (change == "annotation") current.Annotations["User.Note"].Value = "manual";
        else if (change == "lineage") current.LineageTag = Guid.NewGuid().ToString();
        else if (change == "source-lineage") current.Columns["Value"].SourceLineageTag = "manual";
        else if (change == "column-type") current.Columns["Value"].DataType = TOM.DataType.String;
        else
        {
            var udf = live.Model.Functions["Owned.Function"]; target = udf; annotations = udf.Annotations;
            if (change == "udf-expression") udf.Expression = "() => 99";
            else udf.Annotations["User.Note"].Value = "manual";
        }
        Assert.NotEqual(ModelConfigurationReadback.Fingerprint(disk), ModelConfigurationReadback.Fingerprint(live));
        Assert.Throws<AppException>(() => ModelConfigurationOwnership.Require(target, annotations));
    }

    /// <summary>没有当前 v2 前缀的旧 stamp 不再赋予覆盖权, 当前完整归属仍正常接受.</summary>
    [Fact]
    public void UnsupportedOwnershipStampDoesNotAuthorizeMutation()
    {
        using var database = Create();
        var table = database.Model.Tables["Owned"];
        ModelConfigurationOwnership.Mark(table, table.Annotations);
        table.Annotations[ModelConfigurationOwnership.Stamp].Value = table.Annotations[ModelConfigurationOwnership.Stamp].Value[3..];
        Assert.False(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
        Assert.Throws<AppException>(() => ModelConfigurationOwnership.Require(table, table.Annotations));
    }

    private static TOM.Database Create()
    {
        var database = new TOM.Database { Name = "isolated", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Owned" };
        table.Columns.Add(new TOM.DataColumn { Name = "Value", SourceColumn = "Value", DataType = TOM.DataType.Int64 });
        table.Measures.Add(new TOM.Measure { Name = "Owned.Measure", Expression = "42" });
        table.Partitions.Add(new TOM.Partition { Name = "Owned", Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table({\"Value\"}, {{42}})" } });
        table.Annotations.Add(new TOM.Annotation { Name = "User.Note", Value = "keep" });
        database.Model.Tables.Add(table); return database;
    }

    private static TOM.Database WithRuntimeMetadata(TOM.Database source)
    {
        var json = JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(source))!;
        var table = json["model"]!["tables"]![0]!;
        table["modifiedTime"] = "2026-10-17T10:47:54.413333";
        table["structureModifiedTime"] = "2026-10-17T10:47:50.956667";
        var column = table["columns"]![0]!;
        column["modifiedTime"] = "2026-10-17T10:47:50.956667";
        column["structureModifiedTime"] = "2026-10-17T10:47:50.956667";
        column["refreshedTime"] = "1699-12-31T00:00:00";
        column["attributeHierarchy"] = JsonNode.Parse("{\"state\":\"ready\",\"modifiedTime\":\"2026-10-17T10:47:54.556667\",\"refreshedTime\":\"2026-10-17T10:47:54.556667\"}");
        ((JsonArray)table["columns"]!).Add(JsonNode.Parse("{\"type\":\"rowNumber\",\"name\":\"RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61\",\"dataType\":\"int64\",\"isHidden\":true,\"isUnique\":true,\"isKey\":true,\"isNullable\":false}"));
        table["measures"]![0]!["dataType"] = "int64";
        foreach (var annotation in (JsonArray)table["annotations"]!) annotation!["modifiedTime"] = "2026-10-17T10:47:50.956667";
        return TOM.JsonSerializer.DeserializeDatabase(json.ToJsonString());
    }
}
