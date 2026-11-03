// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableDeletionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证所选表与关联关系的原子删除, 严格批次和缺失目标不遗留局部修改.

using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Editing;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用私有 TOM 对象验证删除, 不连接用户模型或触发业务数据刷新.</summary>
public sealed class ModelTableDeletionTests
{
    /// <summary>同时清理所选表作为关系任一端的关系, 保留无关表、关系及其原对象.</summary>
    [Fact]
    public void SelectedTablesRemoveBothRelationshipEndpointsAndPreserveUnrelatedObjects()
    {
        using var database = Database();
        var retained = database.Model.Tables["KeepA"];
        var relation = database.Model.Relationships["Keep"];
        Apply(database, "selected", "OtherSelected");
        Assert.Equal(["KeepA", "KeepB"], database.Model.Tables.Select(table => table.Name));
        Assert.Same(retained, database.Model.Tables["KeepA"]);
        Assert.Same(relation, Assert.Single(database.Model.Relationships));
        using var roundtrip = TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(database));
        Assert.Equal(["KeepA", "KeepB"], roundtrip.Model.Tables.Select(table => table.Name));
        Assert.Equal("Keep", Assert.Single(roundtrip.Model.Relationships).Name);
    }

    /// <summary>所有表先整体核验, 后面的缺失目标不得提前删除第一张表或它的关系.</summary>
    [Fact]
    public void MissingLaterTableLeavesOriginalDefinitionUntouched()
    {
        using var database = Database();
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal("OBJECT_NOT_FOUND", Assert.Throws<AppException>(() => Apply(database, "Selected", "Missing")).Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>整模删除仍是一个明确批次, 不遗留任何关系或仅含度量值的表.</summary>
    [Fact]
    public void ExplicitlySelectingEveryTableAlsoDeletesAllRelationships()
    {
        using var database = Database();
        Apply(database, [.. database.Model.Tables.Select(table => table.Name)]);
        Assert.Empty(database.Model.Tables);
        Assert.Empty(database.Model.Relationships);
    }

    /// <summary>大型所选集合仍在同一原对象批次删除, 不因历史度量批次上限拆分提交或丢失无关关系.</summary>
    [Fact]
    public void OneBatchDeletes129TablesAndPreservesUnrelatedRelationship()
    {
        using var database = Database();
        var extra = Enumerable.Range(0, 127).Select(index => "Extra" + index).ToArray();
        foreach (var name in extra) database.Model.Tables.Add(new TOM.Table { Name = name });
        var relationship = database.Model.Relationships["Keep"];
        Apply(database, ["Selected", "OtherSelected", .. extra]);
        Assert.Equal(["KeepA", "KeepB"], database.Model.Tables.Select(table => table.Name));
        Assert.Same(relationship, Assert.Single(database.Model.Relationships));
    }

    /// <summary>关系端点缺失时不能猜测与所选表无关, 在任何删除前拒绝并保留当前定义.</summary>
    [Fact]
    public void MissingRelationshipEndpointRejectsBeforeDeletingAnyTable()
    {
        using var database = Database();
        ((TOM.SingleColumnRelationship)database.Model.Relationships["Keep"]).FromColumn = null!;
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal("TABLE_RELATIONSHIP_UNSUPPORTED", Assert.Throws<AppException>(() => Apply(database, "Selected")).Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>超过单轮表数上限时整批拒绝, 不拆成可能部分提交的多轮删除.</summary>
    [Fact]
    public void TooManySelectedTablesRejectWholeBatch() => Assert.Throws<AppException>(() =>
        TableDeleteBatch.Parse(JsonSerializer.Serialize(new TableDeleteBatch("p", "b", Enumerable.Range(0, 2001).Select(index => "T" + index).ToArray()), JsonSerializerOptions.Web)));

    /// <summary>表批次按已有表格能力容纳 2000 个目标, 不沿用度量值批次的 128 个条目上限.</summary>
    [Theory]
    [InlineData(129)]
    [InlineData(2000)]
    public void LargeBatchWithinInputBudgetIsAccepted(int count)
    {
        var batch = TableDeleteBatch.Parse(JsonSerializer.Serialize(new TableDeleteBatch("p", "b", Enumerable.Range(0, count).Select(index => "T" + index).ToArray()), JsonSerializerOptions.Web));
        Assert.Equal(count, batch.Tables.Count);
    }

    /// <summary>表数在范围内但嵌入 JSON 超过 30000 字符时仍整体拒绝, 不截断目标或拆分请求.</summary>
    [Fact]
    public void LargeBatchStillRequiresExistingJsonCharacterBudget()
    {
        var tables = Enumerable.Range(0, 200).Select(index => new string('T', 195) + index).ToArray();
        var value = JsonSerializer.Serialize(new TableDeleteBatch("p", "b", tables), JsonSerializerOptions.Web);
        Assert.True(value.Length > 30000);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => TableDeleteBatch.Parse(value)).Code);
    }

    /// <summary>拒绝空批次、身份缺失、重复大小写目标及未知字段, 不允许宽松解析改变删除范围.</summary>
    [Theory]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[]}")]
    [InlineData("{\"projectId\":\"\",\"baselineId\":\"b\",\"tables\":[\"Selected\"]}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Selected\",\"selected\"]}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Selected\"],\"all\":true}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Selected\"],\"tables\":[\"KeepA\"]}")]
    public void InvalidBatchIsRejected(string value) => Assert.Throws<AppException>(() => TableDeleteBatch.Parse(value));

    /// <summary>构造两种方向的关联和一条无关关系, 每次返回独立对象供测试拥有和释放.</summary>
    private static TOM.Database Database()
    {
        var database = new TOM.Database { Name = "Isolated", ID = "Isolated", CompatibilityLevel = 1702, Model = new TOM.Model() };
        foreach (var name in new[] { "Selected", "OtherSelected", "KeepA", "KeepB" })
        {
            var table = new TOM.Table { Name = name };
            table.Columns.Add(new TOM.DataColumn { Name = "Id", DataType = TOM.DataType.Int64, SourceColumn = "Id" });
            database.Model.Tables.Add(table);
        }
        Add("FromSelected", "Selected", "KeepA");
        Add("ToSelected", "KeepA", "Selected");
        Add("BothSelected", "Selected", "OtherSelected");
        Add("Keep", "KeepA", "KeepB");
        return database;

        /// <summary>关系始终引用本次模型的真实列, 不使用相同名称的外部替身.</summary>
        void Add(string name, string from, string to) => database.Model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = name,
            FromColumn = database.Model.Tables[from].Columns["Id"],
            ToColumn = database.Model.Tables[to].Columns["Id"]
        });
    }

    /// <summary>通过产品共用内存修改入口应用准确批次, 不进行提交或文件写入.</summary>
    private static void Apply(TOM.Database database, params string[] tables) => ModelMutation.Apply(database,
        new("isolated.pbip", "memory", "table.deleteBatch", "tables", "items", JsonSerializer.Serialize(new TableDeleteBatch("p", "b", tables), JsonSerializerOptions.Web)));
}
