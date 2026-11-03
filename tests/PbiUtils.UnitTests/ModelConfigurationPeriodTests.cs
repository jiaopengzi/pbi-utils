// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationPeriodTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证时间智能集合缩减、依赖保留与真实 PBIP 保存的原子保护.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelConfigurationPeriodTests : IDisposable
{
    private static readonly string[] BaseOnly = ["销售额"];
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-period-sync-" + Guid.NewGuid().ToString("N"));

    /// <summary>模型重读须返回用户明确选择的期间配置, 不能把依赖闭包误当成额外勾选.</summary>
    [Fact]
    public void AppliedPeriodConfigurationIsReadBackWithExactSelections()
    {
        using var database = Database();
        var options = Options(["ΔPD", "MTD"]) with { NumberDigits = 3, PercentDigits = 1, Folder = "Configured" };
        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
        using var copy = TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(database));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(ModelSnapshotMapper.Map(copy), System.Text.Json.JsonSerializerOptions.Web);
        Assert.True(json.TryGetProperty("periods", out var periods));
        Assert.Equal("Dates", periods.GetProperty("dateColumn").GetString());
        Assert.Equal(options.Templates, periods.GetProperty("templates").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(3, periods.GetProperty("numberDigits").GetInt32());
        Assert.Equal(1, periods.GetProperty("percentDigits").GetInt32());
    }

    /// <summary>清空基础指标是已提交的空方案, 重新进入页面仍能准确回填空选择.</summary>
    [Fact]
    public void EmptyBaseSelectionIsReadBackAfterDeletingGeneratedOutputs()
    {
        using var database = Database();
        var options = Options(["AC", "PD"]);
        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
        ScaffoldPeriodMeasures.Apply(database.Model, options with { BaseMeasures = [] }, configuration: true);
        Assert.Equal("销售额", Assert.Single(database.Model.Tables["销售"].Measures).Name);
        Assert.NotNull(ModelSnapshotMapper.Map(database).Periods);
        Assert.Empty(ModelSnapshotMapper.Map(database).Periods!.BaseMeasures);
    }

    /// <summary>已删除的输出或人工改过的表达式不能被陈旧选项重新背书, 普通模型读取仍可完成.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedPeriodOutputsDoNotReportVerifiedConfiguration(bool delete)
    {
        using var database = Database();
        ScaffoldPeriodMeasures.Apply(database.Model, Options(["AC", "PD"]), configuration: true);
        var output = database.Model.Tables["销售"].Measures.First(measure => measure.Name != "销售额");
        if (delete) output.Table.Measures.Remove(output);
        else output.Expression = "999";
        var json = System.Text.Json.JsonSerializer.SerializeToElement(ModelSnapshotMapper.Map(database), System.Text.Json.JsonSerializerOptions.Web);
        Assert.True(json.TryGetProperty("periods", out var periods));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, periods.ValueKind);
    }

    /// <summary>全量保存后缩减, 实际文件回读只保留明确选择和其依赖; 增加、空集与禁用共享同一保存链路.</summary>
    [Fact]
    public async Task SavedPeriodSelectionReplacesOldOutputsAndKeepsDependencyClosure()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default);
        edits.Select(snapshot);

        var options = Options([.. ScaffoldTimeIntelligence.Templates.Select(template => template.Id)]);
        snapshot = await Apply(new(Calendar: new("simple", 2024, 2024, "Dates", Fields: [.. ScaffoldCalendarFields.Simple], NumberedColumns: false), Periods: options));
        Assert.Equal(52, snapshot.Model.Measures.Count);
        options = options with { Templates = ["ΔPD"] };
        snapshot = await Apply(new(Periods: options));
        Assert.Equal(BaseOnly.Concat(ScaffoldTimeIntelligence.Generate("销售额", options).Select(value => value.Name)).Order(), snapshot.Model.Measures.Select(value => value.Name).Order());
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
        var baseline = snapshot.BaselineId;
        snapshot = await Apply(new(Periods: options));
        Assert.Equal(baseline, snapshot.BaselineId);
        options = options with { Templates = ["ΔPD", "MTD"] };
        snapshot = await Apply(new(Periods: options));
        Assert.Equal(5, snapshot.Model.Measures.Count);
        snapshot = await Apply(new(Periods: options with { Templates = [] }));
        Assert.Equal("销售额", Assert.Single(snapshot.Model.Measures).Name);
        snapshot = await Apply(new(Periods: options));
        baseline = snapshot.BaselineId;
        snapshot = await Apply(new());
        Assert.Equal(baseline, snapshot.BaselineId);

        /// <summary>冻结当前权威基线并经共享编辑器保存, 不直接写测试模型.</summary>
        async Task<ProjectSnapshot> Apply(ModelConfigurationRequest request)
        {
            var prepared = await compiler.PrepareAsync(request, snapshot, default);
            try { return await edits.EditConfigurationAndSaveAsync(prepared, default); }
            finally { compiler.Release(prepared); }
        }
    }

    /// <summary>名称和表达式均等于期间输出但缺少明确来源时, 缩减不能删除这些未知对象.</summary>
    [Fact]
    public void UnattributedGeneratedShapesRemainProtected()
    {
        using var database = Database();
        var table = database.Model.Tables["销售"];
        var options = Options(["AC", "PD"]);
        foreach (var output in ScaffoldTimeIntelligence.Generate("销售额", options))
        {
            var unknown = new TOM.Measure { Name = output.Name, Expression = output.Expression, FormatString = output.FormatString, DisplayFolder = output.Folder };
            table.Measures.Add(unknown);
            ModelConfigurationOwnership.Mark(unknown, unknown.Annotations);
        }
        var before = table.Measures.Select(measure => (measure.Name, measure.Expression)).ToArray();
        ScaffoldPeriodMeasures.Apply(database.Model, options with { Templates = [] }, configuration: true);
        Assert.Equal(before, table.Measures.Select(measure => (measure.Name, measure.Expression)));
    }

    /// <summary>减少基础度量或改变输出表时移除旧输出, 保留基础度量与它们的原始定义.</summary>
    [Fact]
    public void ChangedBaseSelectionAndOutputTableReplaceThePriorBatch()
    {
        using var database = Database();
        var model = database.Model;
        model.Tables["销售"].Measures.Add(new TOM.Measure { Name = "数量", Expression = "2" });
        model.Tables.Add(new TOM.Table { Name = "输出" });
        var options = Options(["AC", "PD"]) with { BaseMeasures = ["销售额", "数量"] };
        ScaffoldPeriodMeasures.Apply(model, options, configuration: true);
        ScaffoldPeriodMeasures.Apply(model, options with { BaseMeasures = ["销售额"], MeasureTable = "输出", Templates = ["AC"] }, configuration: true);
        Assert.Equal(2, model.Tables["销售"].Measures.Count);
        Assert.Single(model.Tables["输出"].Measures);
    }

    /// <summary>原对象生成迁移保留说明、隐藏状态、业务注解和血缘, 不复制完整模型.</summary>
    [Fact]
    public void MovingGeneratedPeriodPreservesApprovedMetadata()
    {
        using var database = Database();
        var model = database.Model;
        model.Tables.Add(new TOM.Table { Name = "输出" });
        var options = Options(["AC"]);
        ScaffoldPeriodMeasures.Apply(model, options, configuration: true);
        var name = ScaffoldTimeIntelligence.Generate("销售额", options).Single().Name;
        var original = model.Tables["销售"].Measures[name];
        original.Description = "保留说明";
        original.IsHidden = true;
        original.Annotations.Add(new TOM.Annotation { Name = "Business", Value = "retained" });
        ModelConfigurationOwnership.Mark(original, original.Annotations);
        var lineage = original.LineageTag;
        ScaffoldPeriodMeasures.Apply(model, options with { MeasureTable = "输出" }, configuration: true);
        var actual = model.Tables["输出"].Measures[name];
        Assert.Null(model.Tables["销售"].Measures.Find(name));
        Assert.Equal("保留说明", actual.Description);
        Assert.True(actual.IsHidden);
        Assert.Equal(lineage, actual.LineageTag);
        Assert.Equal("retained", actual.Annotations["Business"].Value);
        Assert.True(ModelConfigurationOwnership.IsOwned(actual, actual.Annotations));
    }

    /// <summary>SDK 不支持原对象换父时, 无法按生成器等价保留的业务子元数据必须在移除前拒绝.</summary>
    [Fact]
    public void UnknownGeneratedPeriodSubtreeRejectsMoveBeforeRemovingOriginal()
    {
        using var database = Database();
        var model = database.Model;
        model.Tables.Add(new TOM.Table { Name = "输出" });
        var options = Options(["AC"]);
        ScaffoldPeriodMeasures.Apply(model, options, configuration: true);
        var name = ScaffoldTimeIntelligence.Generate("销售额", options).Single().Name;
        var original = model.Tables["销售"].Measures[name];
        original.DetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "ROW(\"Value\", 1)" };
        ModelConfigurationOwnership.Mark(original, original.Annotations);
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => ScaffoldPeriodMeasures.Apply(model,
            options with { MeasureTable = "输出" }, configuration: true));
        Assert.Equal("CONFIGURATION_PERIOD_MOVE_UNSUPPORTED", error.Code);
        Assert.Same(original, model.Tables["销售"].Measures[name]);
        Assert.Empty(model.Tables["输出"].Measures);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>待移除对象已被人工修改时整批失败, 不把旧归属重新当成删除授权.</summary>
    [Fact]
    public void ModifiedPeriodOutputCannotBeRemoved()
    {
        using var database = Database();
        var options = Options(["AC", "PD"]);
        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
        var removedName = ScaffoldTimeIntelligence.Generate("销售额", options).Single(value => value.TemplateId == "PD").Name;
        database.Model.Tables["销售"].Measures[removedName].Expression = "123";
        var error = Assert.Throws<AppException>(() => ScaffoldPeriodMeasures.Apply(database.Model, options with { Templates = ["AC"] }, configuration: true));
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", error.Code);
        Assert.Equal("123", database.Model.Tables["销售"].Measures[removedName].Expression);
    }

    /// <summary>普通模板生成对象即便具有完整通用归属, 也不能仅因与期间同名而被覆盖.</summary>
    [Fact]
    public void GenericOwnedSameNameIsNotPeriodOwnership()
    {
        using var database = Database();
        var options = Options(["AC"]);
        var value = ScaffoldTimeIntelligence.Generate("销售额", options).Single();
        var unknown = new TOM.Measure { Name = value.Name, Expression = "42" };
        database.Model.Tables["销售"].Measures.Add(unknown);
        ModelConfigurationOwnership.Mark(unknown, unknown.Annotations);
        var error = Assert.Throws<AppException>(() => ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true));
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", error.Code);
        Assert.Equal("42", unknown.Expression);
    }

    /// <summary>名称和生成公式相同但带业务元数据的对象没有完整期间证明, 缩减时保持.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnattributedShapeWithBusinessMetadataIsPreserved(bool annotation)
    {
        using var database = Database();
        var options = Options(["PD"]);
        var value = ScaffoldTimeIntelligence.Generate("销售额", options).Single();
        var unknown = new TOM.Measure { Name = value.Name, Expression = value.Expression, FormatString = value.FormatString, DisplayFolder = value.Folder };
        if (annotation) unknown.Annotations.Add(new TOM.Annotation { Name = "Business", Value = "owned by template" });
        else unknown.Description = "业务说明";
        database.Model.Tables["销售"].Measures.Add(unknown);
        ModelConfigurationOwnership.Mark(unknown, unknown.Annotations);
        ScaffoldPeriodMeasures.Apply(database.Model, options with { Templates = ["AC"] }, configuration: true);
        Assert.Same(unknown, database.Model.Tables["销售"].Measures[value.Name]);
    }

    /// <summary>移动期间输出前拒绝仍绑定旧表的存活表达式, 原表与完整定义保持.</summary>
    [Fact]
    public void ReferencedPeriodCannotMoveToAnotherTable()
    {
        using var database = Database();
        var options = Options(["AC"]);
        ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
        database.Model.Tables.Add(new TOM.Table { Name = "输出" });
        var name = ScaffoldTimeIntelligence.Generate("销售额", options).Single().Name;
        database.Model.Tables["销售"].Measures.Add(new TOM.Measure { Name = "业务引用", Expression = CalendarTemplates.TableReference("销售") + CalendarTemplates.ColumnReference(name) });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => ScaffoldPeriodMeasures.Apply(database.Model, options with { MeasureTable = "输出" }, configuration: true));
        Assert.Equal("CONFIGURATION_PERIOD_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>存活度量引用待删除输出时准备阶段拒绝, 权威文件与基线保持不变.</summary>
    [Fact]
    public async Task ReferencedOutputRejectsPreparationWithoutSaving()
    {
        var entry = PbipFixture.Create(root);
        var file = Path.Combine(root, "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(file)))
        {
            database.Model.Tables.Add(new TOM.Table { Name = "Dates" });
            database.Model.Tables["Dates"].Columns.Add(new TOM.DataColumn { Name = "Dates", SourceColumn = "Dates", DataType = TOM.DataType.DateTime });
            var options = Options(["AC", "PD"]);
            ScaffoldPeriodMeasures.Apply(database.Model, options, configuration: true);
            var name = ScaffoldTimeIntelligence.Generate("销售额", options).Single(value => value.TemplateId == "PD").Name;
            database.Model.Tables["销售"].Measures.Add(new TOM.Measure { Name = "业务引用", Expression = CalendarTemplates.ColumnReference(name) });
            File.WriteAllText(file, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var before = File.ReadAllText(file);
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var error = await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Periods: Options(["AC"])), snapshot, default));
        Assert.Equal("CONFIGURATION_PERIOD_REFERENCED", error.Code);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
    }

    /// <summary>构造实际 TOM 私有模型, 不代替真实 Desktop 引擎执行验收.</summary>
    private TOM.Database Database()
    {
        var entry = PbipFixture.Create(root);
        var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(Path.GetDirectoryName(entry)!, "model", "model.bim")));
        database.Model.Tables.Add(new TOM.Table { Name = "Dates" });
        database.Model.Tables["Dates"].Columns.Add(new TOM.DataColumn { Name = "Dates", SourceColumn = "Dates", DataType = TOM.DataType.DateTime });
        return database;
    }

    /// <summary>返回独立期间选项, 不读取用户偏好.</summary>
    private static ScaffoldPeriodOptions Options(string[] templates) => new("Dates", "Dates", "销售", ["销售额"], templates);

    /// <summary>只删除本测试随机创建的临时根, 不触及用户工程.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
