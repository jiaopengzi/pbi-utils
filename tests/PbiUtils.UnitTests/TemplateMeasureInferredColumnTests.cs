// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureInferredColumnTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Desktop 只读 RowNumber 与额外用户列不阻断参数表重配和自由切换.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用 SDK 反序列化真实 RowNumber 类型, 不修改验收中的 Desktop 模型或资产.</summary>
public sealed class TemplateMeasureInferredColumnTests
{
    /// <summary>SDK 推导行号键不改变完整可写归属, Config 重配与切回均保留正确业务边界.</summary>
    [Fact]
    public void RefreshedRowNumberColumnAllowsConfigReconfiguration()
    {
        using var database = Refreshed();
        var model = database.Model;
        var config = model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
        Assert.Equal(7, config.Columns.Count);
        Assert.Equal(6, config.Columns.OfType<TOM.DataColumn>().Count());
        Assert.True(ModelConfigurationOwnership.IsOwned(config, config.Annotations));
        Assert.True(ModelConfigurationOwnership.IsOwned(model.Tables["Measure"], model.Tables["Measure"].Annotations));
        var rowNumber = Assert.Single(config.Columns.OfType<TOM.RowNumberColumn>());
        var rowDefinition = TOM.JsonSerializer.SerializeObject(rowNumber);
        var roles = ModelConfigurationOwnership.ReadRoles(model).Roles;
        TemplateMeasureDefinition[] next = [new("author", "new value", "Uncategorized", true, "", "")];
        ScaffoldTemplateMeasureMode.Apply(model, next, "config", roles, configuration: true);
        Assert.Same(rowNumber, Assert.Single(config.Columns.OfType<TOM.RowNumberColumn>()));
        Assert.Equal(rowDefinition, TOM.JsonSerializer.SerializeObject(rowNumber));
        Assert.True(ModelConfigurationOwnership.IsOwned(config, config.Annotations));
        Assert.Equal("new value", Assert.Single(TemplateMeasureConfigurationStorage.Read(model)!.Measures).Value);
        ScaffoldTemplateMeasureMode.Apply(model, next, "tmdl", roles, configuration: true);
        Assert.False(model.Tables.ContainsName(TemplateMeasureConfigurationStorage.DefaultTable));
        Assert.Equal("\n\"new value\"", model.Tables["Measure"].Measures["author"].Expression);
        Assert.Single(model.Tables["Measure"].Columns.OfType<TOM.RowNumberColumn>());
    }

    /// <summary>额外数据列或计算列保留原对象, Config 修复标准字段且不再以额外列阻止切换.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtraWritableColumnIsPreservedDuringConfigUpsert(bool calculated)
    {
        using var database = Refreshed();
        var config = database.Model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
        TOM.Column extra = calculated
            ? new TOM.CalculatedColumn { Name = "user", DataType = TOM.DataType.String, Expression = "\"user\"" }
            : new TOM.DataColumn { Name = "user", DataType = TOM.DataType.String, SourceColumn = "user" };
        config.Columns.Add(extra);
        ModelConfigurationOwnership.Mark(config, config.Annotations);
        Assert.True(ModelConfigurationOwnership.IsOwned(config, config.Annotations));
        var roles = ModelConfigurationOwnership.ReadRoles(database.Model).Roles;
        ScaffoldTemplateMeasureMode.Apply(database.Model,
            [new("author", "next", "Uncategorized", true, "", "")], "config", roles, configuration: true);
        Assert.Same(extra, config.Columns.Find("user"));
        ScaffoldTemplateMeasureMode.Apply(database.Model, [new("author", "last", "Uncategorized", true, "", "")], "tmdl", roles, configuration: true);
        Assert.Same(extra, config.Columns.Find("user"));
        Assert.True(config.IsHidden);
    }

    /// <summary>先由当前生成器建立受保护 Config, 再由官方 SDK 读取服务端合法只读行号列, 不重新签名.</summary>
    private static TOM.Database Refreshed()
    {
        using var original = new TOM.Database { Name = "refreshed-config", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        ScaffoldTemplateMeasureMode.Apply(original.Model, [new("author", "initial", "Uncategorized", true, "", "")], "config", roles, configuration: false);
        ModelConfigurationOwnership.Mark(original.Model.Tables["Measure"].Measures["author"], original.Model.Tables["Measure"].Measures["author"].Annotations);
        ModelConfigurationOwnership.SetRoles(original.Model, new(1, roles, false, new(StringComparer.Ordinal)));
        var json = JsonNode.Parse(TOM.JsonSerializer.SerializeDatabase(original))!;
        foreach (var table in json["model"]!["tables"]!.AsArray())
        {
            table!["columns"] ??= new JsonArray();
            table["columns"]!.AsArray().Add(JsonNode.Parse("""{"type":"rowNumber","name":"RowNumber","dataType":"int64","isHidden":true,"isUnique":true,"isKey":true,"isNullable":false}"""));
        }
        return TOM.JsonSerializer.DeserializeDatabase(json.ToJsonString());
    }
}
