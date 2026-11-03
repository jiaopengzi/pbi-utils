// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelMeasureDeletionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证普通度量删除保护存活引用, 同批最终状态可解除依赖且不向 BIM/TMDL 保存悬空定义.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Editing;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用私有 TOM 与唯一临时文件夹具验证共享操作, 不连接用户模型或执行数据源.</summary>
public sealed class ModelMeasureDeletionTests
{
    /// <summary>原对象批次在修改前核验完整目标和名称, 后续冲突不得遗留前面的属性更改.</summary>
    [Fact]
    public void LaterBatchConflictDoesNotMutateEarlierOriginalObject()
    {
        using var database = Database();
        var original = database.Model.Tables[0].Measures.Find("Unreferenced");
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => Apply(database,
            new("update", "Audit", "Unreferenced", Description: "must remain unchanged"),
            new("create", "Audit", "AuditBase", Expression: "2")));
        Assert.Equal("OBJECT_CONFLICT", error.Code);
        Assert.Same(original, database.Model.Tables[0].Measures.Find("Unreferenced"));
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>只读预检沿用 TOM 大小写不敏感定位, 后续 setter 仍修改相同原对象.</summary>
    [Fact]
    public void CaseInsensitiveBatchTargetsKeepOriginalObject()
    {
        using var database = Database();
        var consumer = database.Model.Tables[0].Measures.Find("AuditConsumer");
        Apply(database, new("update", "audit", "auditconsumer", Expression: "2"), new("delete", "audit", "auditbase"));
        Assert.Same(consumer, database.Model.Tables[0].Measures.Find("AuditConsumer"));
        Assert.Equal("2", consumer.Expression);
        Assert.Null(database.Model.Tables[0].Measures.Find("AuditBase"));
    }

    /// <summary>完整候选中的 DAX/UDF/元数据引用均阻止删除, 失败后调用方模型完整保持原状.</summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("function")]
    [InlineData("column")]
    [InlineData("format")]
    [InlineData("qa")]
    [InlineData("perspective")]
    [InlineData("translation")]
    public void SurvivingReferencesRejectDeletionWithoutChangingModel(string kind)
    {
        using var database = Database();
        var table = database.Model.Tables[0];
        table.Measures.Remove(table.Measures.Find("AuditConsumer"));
        switch (kind)
        {
            case "measure": table.Measures.Add(new TOM.Measure { Name = "AuditConsumer", Expression = "[AuditBase] + 1" }); break;
            case "function": database.Model.Functions.Add(new TOM.Function { Name = "Audit.Consumer", Expression = "() => [AuditBase]" }); break;
            case "column": table.Columns.Add(new TOM.CalculatedColumn { Name = "Consumer", DataType = TOM.DataType.Int64, Expression = "[AuditBase]" }); break;
            case "format": table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "1", FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "[AuditBase]" } }); break;
            case "qa":
                database.Model.Cultures.Add(new TOM.Culture
                {
                    Name = "en-US",
                    LinguisticMetadata = new TOM.LinguisticMetadata
                    {
                        ContentType = TOM.ContentType.Json,
                        Content = """{"Entities":{"Value":{"Definition":{"Binding":{"ConceptualEntity":"Audit","ConceptualProperty":"AuditBase"}}}}}"""
                    }
                });
                break;
            case "perspective":
                var perspective = new TOM.Perspective { Name = "View" }; database.Model.Perspectives.Add(perspective);
                var visible = new TOM.PerspectiveTable { Name = table.Name, Table = table }; perspective.PerspectiveTables.Add(visible);
                visible.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Name = "AuditBase", Measure = table.Measures.Find("AuditBase") });
                break;
            default:
                var culture = new TOM.Culture { Name = "en-US" }; database.Model.Cultures.Add(culture);
                culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = table.Measures.Find("AuditBase"), Property = TOM.TranslatedProperty.Caption, Value = "Value" });
                break;
        }
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        var error = Assert.Throws<AppException>(() => Apply(database, new("update", "Audit", "Unreferenced", Description: "must remain unchanged"),
            new("delete", "Audit", "AuditBase")));
        Assert.Equal("MEASURE_REFERENCED", error.Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>本轮整体删除的内部依赖可以清理, 更新引用者使用最终值且不受条目顺序影响.</summary>
    [Theory]
    [InlineData("closure")]
    [InlineData("update-first")]
    [InlineData("delete-first")]
    public void FinalBatchStateAllowsRemovingDependencies(string kind)
    {
        using var database = Database();
        var remove = new MeasureChange("delete", "Audit", "AuditBase");
        var replace = new MeasureChange("update", "Audit", "AuditConsumer", Expression: "2", Description: "retained");
        Apply(database, kind switch
        {
            "closure" => [remove, new("delete", "Audit", "AuditConsumer")],
            "update-first" => [replace, remove],
            _ => [remove, replace]
        });
        Assert.Null(database.Model.Tables[0].Measures.Find("AuditBase"));
        Assert.Equal(kind == "closure" ? null : "2", database.Model.Tables[0].Measures.Find("AuditConsumer")?.Expression);
        Assert.NotNull(database.Model.Tables[0].Measures.Find("Unreferenced"));
    }

    /// <summary>删除闭包后在其他既有表创建同名度量沿用既有语义, 不把预检保留对象误作重名冲突.</summary>
    [Fact]
    public void DeleteThenCreateSameNameInAnotherTableKeepsExistingBatchBehavior()
    {
        using var database = Database();
        database.Model.Tables.Add(new TOM.Table { Name = "Other" });
        Apply(database, new("delete", "Audit", "AuditConsumer"), new("delete", "Audit", "AuditBase"), new("create", "Other", "AuditBase", Expression: "3"));
        Assert.Null(database.Model.Tables[0].Measures.Find("AuditBase"));
        Assert.Equal("3", database.Model.Tables.Find("Other").Measures.Find("AuditBase").Expression);
    }

    /// <summary>真实 BIM/TMDL 文件经产品共享直接保存入口拒绝悬空引用, 原始文件基线及所有属性不变.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedDeletionNeverPublishesBrokenFiles(bool tmdl)
    {
        using var fixture = new FileFixture(tmdl);
        using var workspace = new ChangeWorkspace(fixture.User);
        var reader = new ProjectReader();
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(fixture.Entry, default); edits.Select(snapshot);
        var error = await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(Request(snapshot.BaselineId,
            new MeasureChange("delete", "Audit", "AuditBase")), default));
        Assert.Equal("MEASURE_REFERENCED", error.Code);
        Assert.False(edits.HasPendingChanges);
        var actual = await reader.ReadAsync(fixture.Entry, default);
        Assert.Equal(snapshot.BaselineId, actual.BaselineId);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Model), JsonSerializer.Serialize(actual.Model));
        Assert.False(Directory.Exists(Path.Combine(fixture.User, "backup")));
    }

    /// <summary>同批先移除消费者引用再删除基础度量可真正落盘, 保存后仍能继续新增与删除.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalConsumerUpdateAndDeletionPersistAcrossFormats(bool tmdl)
    {
        using var fixture = new FileFixture(tmdl);
        using var workspace = new ChangeWorkspace(fixture.User);
        var reader = new ProjectReader();
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(fixture.Entry, default); edits.Select(snapshot);
        snapshot = await edits.EditAndSaveAsync(Request(snapshot.BaselineId, new("delete", "Audit", "AuditBase"),
            new("update", "Audit", "AuditConsumer", Expression: "2")), default);
        Assert.DoesNotContain(snapshot.Model.Measures, measure => measure.Name == "AuditBase");
        Assert.Equal("2", snapshot.Model.Measures.Single(measure => measure.Name == "AuditConsumer").Expression);
        snapshot = await edits.EditAndSaveAsync(Request(snapshot.BaselineId, new MeasureChange("create", "Audit", "AuditBase", Expression: "3")), default);
        snapshot = await edits.EditAndSaveAsync(Request(snapshot.BaselineId, new MeasureChange("delete", "Audit", "AuditBase")), default);
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(fixture.Entry, default)).BaselineId);
        Assert.DoesNotContain(snapshot.Model.Measures, measure => measure.Name == "AuditBase");
    }

    /// <summary>生成只有固定普通度量的完整私有 TOM, 不带生成归属或真实业务内容.</summary>
    private static TOM.Database Database() => TOM.JsonSerializer.DeserializeDatabase("""
        {"name":"measure-audit","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Audit","columns":[{"name":"Value","dataType":"int64","sourceColumn":"Value"}],"measures":[{"name":"AuditBase","expression":"1"},{"name":"AuditConsumer","expression":"[AuditBase] + 1"},{"name":"Unreferenced","expression":"0"}]}]}}
        """);

    /// <summary>将严格批次传给正式模型修改入口, 失败使用原业务错误返回.</summary>
    private static void Apply(TOM.Database database, params MeasureChange[] items) => ModelMutation.Apply(database,
        new("owned.pbip", "memory", "measure.batch", "audit", "items", Request("baseline", items).Value));

    /// <summary>构造绑定当前基线的批次, 不引入 rename 或额外权限字段.</summary>
    private static ModelEditRequest Request(string baseline, params MeasureChange[] items) => new("measure.batch", "audit", "items",
        JsonSerializer.Serialize(new MeasureBatch(baseline, items), JsonSerializerOptions.Web));

    /// <summary>唯一临时工程及其独立用户目录, 仅在本测试拥有的根目录内创建与清理文件.</summary>
    private sealed class FileFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-tests", Guid.NewGuid().ToString("N"));
        internal string Entry => Path.Combine(root, "source", "audit.pbip");
        internal string User => Path.Combine(root, "user");

        /// <summary>按指定定义格式生成同一个固定模型, 文件是实际序列化器产物.</summary>
        internal FileFixture(bool tmdl)
        {
            Write("source/audit.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
            Write("source/report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
            Write("source/report/definition/report.json", "{}");
            Write("source/report/definition/pages/pages.json", """{"pageOrder":[]}""");
            Write("source/model/definition.pbism", """{"version":"4.2"}""");
            using var database = Database();
            if (tmdl) TOM.TmdlSerializer.SerializeDatabaseToFolder(database, Path.Combine(root, "source/model/definition"));
            else Write("source/model/model.bim", TOM.JsonSerializer.SerializeDatabase(database));
        }

        /// <summary>只写本测试根目录下的固定路径与固定内容.</summary>
        private void Write(string relative, string value)
        {
            var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
        }

        /// <summary>只清理本次 Guid 唯一目录, 不影响用户工程.</summary>
        public void Dispose() => Directory.Delete(root, true);
    }
}
