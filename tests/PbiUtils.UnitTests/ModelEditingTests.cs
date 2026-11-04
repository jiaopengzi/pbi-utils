// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelEditingTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 实际 PBIP 内存暂存、批量直接保存、冲突取消及桥接来源边界测试.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelEditingTests : IDisposable
{
    /// <summary>当前工程及显式副本共用表说明应用上限, 超限在写入前拒绝, 空说明仍可清空.</summary>
    /// <param name="copy">是否使用 CLI 显式副本计划入口.</param>
    /// <param name="length">本轮表说明字符数, 不复用产品常量推导期望.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2000)]
    [InlineData(false, 2001)]
    [InlineData(true, 0)]
    [InlineData(true, 2000)]
    [InlineData(true, 2001)]
    public async Task TableDescriptionUsesApplicationLimitBeforePreparingChanges(bool copy, int length)
    {
        var description = new string('中', length);
        var before = File.ReadAllBytes(ModelFile);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        if (copy)
        {
            using var service = new ChangeService(workspace, TimeProvider.System);
            var request = new ChangeRequest(Entry, Path.Combine(root, "copy"), "table.set", "Sales", "description", description);
            if (length > 2000)
                Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => service.PrepareAsync(request, default))).Code);
            else Assert.Equal(description, (await service.PrepareAsync(request, default)).Request.Value);
            Assert.Equal(before, File.ReadAllBytes(ModelFile));
        }
        else
        {
            using var edits = new ModelEditService(reader, workspace);
            edits.Select(await reader.ReadAsync(Entry, default));
            var request = new ModelEditRequest("table.set", "Sales", "description", description);
            if (length > 2000)
            {
                Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(request, default))).Code);
                Assert.Equal(before, File.ReadAllBytes(ModelFile));
                Assert.False(edits.HasPendingChanges);
            }
            else
            {
                var saved = await edits.EditAndSaveAsync(request, default);
                Assert.Equal(description, saved.Model.Tables.Single(table => table.Name == "Sales").Description ?? "");
            }
        }
    }

    /// <summary>表说明限长不限制合法的较长 DAX, 仍通过相同模型编辑和权威回读入口.</summary>
    [Fact]
    public async Task TableDescriptionLimitDoesNotReduceExpressionCapacity()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        var expression = "// " + new string('x', 3000) + "\n42";

        var saved = await edits.EditAndSaveAsync(new("measure.set", "Total", "expression", expression, "Sales"), default);

        Assert.Equal(expression, saved.Model.Measures.Single(measure => measure.Name == "Total").Expression);
    }

    /// <summary>单个和批量 CRUD 经真实 BIM/TMDL 回读, 直接保存且不创建历史备份; 失败批次没有局部写入.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasureCrudBatchesAreAtomicWithoutHistoricalBackups(bool tmdl)
    {
        if (tmdl)
        {
            using var db = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(ModelFile));
            Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(root, "source/model/definition"));
            File.Delete(ModelFile); Write("source/model/definition.pbism", """{"version":"4.2"}""");
        }
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(Entry, default); edits.Select(snapshot);
        ModelEditRequest Batch(params MeasureChange[] items) => new("measure.batch", "批量", "items", JsonSerializer.Serialize(new MeasureBatch(snapshot.BaselineId, items), JsonSerializerOptions.Web));
        snapshot = await edits.EditAndSaveAsync(Batch(new("create", "Sales", "New A", "1+2", "note", "0.00", "Folder", true), new("create", "Sales", "New B", "4")), default);
        Assert.Equal(3, snapshot.Model.Measures.Count);
        snapshot = await edits.EditAndSaveAsync(Batch(new("update", "Sales", "New A", Description: "", IsHidden: false), new("update", "Sales", "New B", Expression: "5", DisplayFolder: "F")), default);
        var changed = snapshot.Model.Measures.Single(m => m.Name == "New A");
        Assert.True(string.IsNullOrEmpty(changed.Description)); Assert.False(changed.IsHidden); Assert.Equal("0.00", changed.FormatString); Assert.Equal("1+2", changed.Expression);
        var baseline = snapshot.BaselineId;
        await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(Batch(new("update", "Sales", "New A", Expression: "999"), new("delete", "Sales", "Missing")), default));
        Assert.False(edits.HasPendingChanges);
        Assert.Equal(baseline, (await reader.ReadAsync(Entry, default)).BaselineId);
        Assert.Equal("1+2", (await edits.ReadAsync(Entry, default)).Model.Measures.Single(m => m.Name == "New A").Expression);
        Assert.Equal("OBJECT_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(Batch(new MeasureChange("create", "Sales", "total", "2")), default))).Code);
        snapshot = await edits.EditAndSaveAsync(Batch(new("delete", "Sales", "New A"), new("delete", "Sales", "New B")), default);
        Assert.Single((await reader.ReadAsync(Entry, default)).Model.Measures);
        Assert.Equal("Total", snapshot.Model.Measures[0].Name);
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        var stale = Batch(new MeasureChange("delete", "Sales", "Total")) with { Value = JsonSerializer.Serialize(new MeasureBatch("old", [new("delete", "Sales", "Total")]), JsonSerializerOptions.Web) };
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(stale, default))).Code);
    }

    /// <summary>批次字段严格解析, 不接受未知字段、重复属性、重复目标或空更新.</summary>
    [Theory]
    [InlineData("{\"baselineId\":\"b\",\"items\":[]}")]
    [InlineData("{\"baselineId\":\"b\",\"items\":[{\"action\":\"update\",\"table\":\"T\",\"name\":\"N\"}]}")]
    [InlineData("{\"baselineId\":\"b\",\"items\":[{\"action\":\"create\",\"table\":\"T\",\"name\":\"N\",\"expression\":\"1\",\"path\":\"x\"}]}")]
    [InlineData("{\"baselineId\":\"b\",\"items\":[{\"action\":\"delete\",\"table\":\"T\",\"name\":\"N\",\"name\":\"M\"}]}")]
    [InlineData("{\"baselineId\":\"b\",\"items\":[{\"action\":\"delete\",\"table\":\"T\",\"name\":\"N\"},{\"action\":\"delete\",\"table\":\"t\",\"name\":\"n\"}]}")]
    public void InvalidBatchesAreRejected(string json) => Assert.Throws<AppException>(() => MeasureBatch.Parse(json));
    /// <summary>所选表删除直接保存并权威回读, 取消、旧基线或错误目标均不写文件.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TableDeletionUsesGuardedDirectSaveForBimAndTmdl(bool tmdl)
    {
        using (var database = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(ModelFile)))
        {
            var keep = new Microsoft.AnalysisServices.Tabular.Table { Name = "Keep" };
            keep.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "Value", SourceColumn = "Value", DataType = Microsoft.AnalysisServices.Tabular.DataType.Int64 });
            database.Model.Tables.Add(keep);
            database.Model.Relationships.Add(new Microsoft.AnalysisServices.Tabular.SingleColumnRelationship
            { Name = "SalesToKeep", FromColumn = database.Model.Tables["Sales"].Columns["Value"], ToColumn = keep.Columns["Value"] });
            if (tmdl)
                Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(database, Path.Combine(root, "source/model/definition"));
            else File.WriteAllText(ModelFile, Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(database));
        }
        if (tmdl) { File.Delete(ModelFile); Write("source/model/definition.pbism", """{"version":"4.2"}"""); }
        var user = Path.Combine(root, "user");
        using var workspace = new ChangeWorkspace(user);
        using var edits = new ModelEditService(reader, workspace);
        var original = await reader.ReadAsync(Entry, default);
        edits.Select(original);
        var request = new ModelEditRequest("table.deleteBatch", "tables", "items",
            JsonSerializer.Serialize(new TableDeleteBatch(original.ProjectId, original.BaselineId, ["Sales"]), JsonSerializerOptions.Web));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.EditAndSaveAsync(request, cancellation.Token));
        Assert.Equal(original.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
        Assert.False(Directory.Exists(Path.Combine(user, "backup")));
        var stale = request with { Value = JsonSerializer.Serialize(new TableDeleteBatch(original.ProjectId, "old", ["Sales"]), JsonSerializerOptions.Web) };
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(stale, default))).Code);
        var wrongTarget = request with { Value = JsonSerializer.Serialize(new TableDeleteBatch("other-model", original.BaselineId, ["Sales"]), JsonSerializerOptions.Web) };
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(wrongTarget, default))).Code);
        var saved = await edits.EditAndSaveAsync(request, default);
        Assert.Equal("Keep", Assert.Single(saved.Model.Tables).Name);
        Assert.Empty(saved.Model.Measures);
        Assert.Equal(0, saved.Model.RelationshipCount);
        Assert.Equal(saved.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
        Assert.False(edits.HasPendingChanges);
        Assert.False(Directory.Exists(Path.Combine(user, "backup")));
    }

    /// <summary>共享服务独立拒绝远端所选表删除, 网页不能通过伪造本地按钮绕过只读来源.</summary>
    [Theory]
    [InlineData("powerbi-service")]
    [InlineData("ssas-xmla")]
    public async Task RemoteTableDeletionCannotPrepareOrWrite(string kind)
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var original = (await reader.ReadAsync(Entry, default)) with { Connection = new(kind, kind) };
        edits.Select(original);
        var request = new ModelEditRequest("table.deleteBatch", "tables", "items",
            JsonSerializer.Serialize(new TableDeleteBatch(original.ProjectId, original.BaselineId, ["Sales"]), JsonSerializerOptions.Web));
        Assert.Equal("READ_ONLY_MODEL", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(request, default))).Code);
        Assert.Equal(original.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
    }

    /// <summary>Desktop 导出的 TMDL 允许无数据库名, 所有模型操作均不得依赖 JSON 往返克隆.</summary>
    [Fact]
    public async Task UnnamedTmdlDatabaseCanBeEditedAndSaved()
    {
        var folder = Path.Combine(root, "source/model/definition");
        using (var db = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(ModelFile)))
            Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(db, folder);
        File.WriteAllText(Path.Combine(folder, "database.tmdl"), "database\n\tcompatibilityLevel: 1600\n");
        Write("source/model/definition.pbism", """{"version":"4.2"}""");
        File.Delete(ModelFile);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        await edits.StageAsync(new("measure.set", "Total", "description", "unnamed works", "Sales"), default);
        var saved = await edits.SaveAsync(default);
        Assert.Equal("unnamed works", saved.Model.Measures[0].Description);
        Assert.False(saved.HasPendingChanges);
    }
    /// <summary>实际 TMDL 模型中日期表和依赖新日期表的期间指标累积保存, 保持报表文件字节不变.</summary>
    [Fact]
    public async Task TmdlCalendarAndPeriodsShareOneDraftWithoutChangingReport()
    {
        using (var db = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(ModelFile)))
            Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(root, "source/model/definition"));
        File.Delete(ModelFile);
        Write("source/model/definition.pbism", """{"version":"4.2"}""");
        Write("source/report/definition/pages/pages.json", """{"pageOrder":["p1"]}""");
        Write("source/report/definition/pages/p1/page.json", """{"name":"p1","displayName":"Page"}""");
        Write("source/report/definition/pages/p1/visuals/v1/visual.json", """{"$schema":"https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/2.4.0/schema.json","name":"v1","position":{"x":1,"y":2,"width":100,"height":100},"visual":{"visualType":"card"},"unknown":{"preserve":true}}""");
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var baseline = await reader.ReadAsync(Entry, default);
        edits.Select(baseline);
        await edits.StageAsync(new("calendar.create", "Calendar", "config", """{"startYear":2024,"endYear":2025}"""), default);
        await edits.StageAsync(new("period.generate", "Total", "config", """{"dateTable":"Calendar","dateColumn":"Date","measureTable":"Sales","templates":["YTD"],"digitsN":2,"digitsP":2,"folder":"Time"}"""), default);
        var reportFile = Path.Combine(root, "source/report/definition/pages/p1/visuals/v1/visual.json");
        var reportBytes = File.ReadAllBytes(reportFile);
        Assert.Equal(baseline.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
        Assert.Equal(1, (await edits.ReadAsync(Entry, default)).Pages[0].Visuals[0].X);
        var saved = await edits.SaveAsync(default);
        Assert.Equal("TMDL", saved.ModelFormat); Assert.Contains(saved.Model.Tables, t => t.Name == "Calendar");
        Assert.Contains(saved.Model.Measures, m => m.Expression.Contains("DATESYTD", StringComparison.Ordinal));
        Assert.Equal(1, saved.Pages[0].Visuals[0].X);
        Assert.Equal(reportBytes, File.ReadAllBytes(reportFile));
    }
    /// <summary>移除报表编辑后, 模型编辑和副本计划都拒绝视觉操作且不改变完整工程基线.</summary>
    [Fact]
    public async Task ReportVisualWritesAreRejectedWithoutChangingProject()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var baseline = await reader.ReadAsync(Entry, default);
        edits.Select(baseline);
        var error = await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(new("visual.set", "v1", "x", "50"), default));
        Assert.Equal("UNSUPPORTED_OPERATION", error.Code);
        using var changes = new ChangeService(workspace, TimeProvider.System);
        error = await Assert.ThrowsAsync<AppException>(() => changes.PrepareAsync(new(Entry, Path.Combine(root, "copy"), "visual.set", "v1", "x", "50"), default));
        Assert.Equal("UNSUPPORTED_OPERATION", error.Code);
        Assert.False((await edits.ReadAsync(Entry, default)).HasPendingChanges);
        Assert.Equal(baseline.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-tests", Guid.NewGuid().ToString("N"));
    private string Entry => Path.Combine(root, "source", "sample.pbip");
    private string ModelFile => Path.Combine(root, "source", "model", "model.bim");
    private readonly ProjectReader reader = new();

    /// <summary>独立临时夹具只包含固定定义, 不读取用户工程.</summary>
    public ModelEditingTests()
    {
        Write("source/sample.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Write("source/report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        Write("source/report/definition/report.json", "{}");
        Write("source/report/definition/pages/pages.json", """{"pageOrder":[]}""");
        Write("source/model/definition.pbism", """{"version":"1.0"}""");
        Write("source/model/model.bim", """{"name":"sample","compatibilityLevel":1600,"model":{"culture":"en-US","tables":[{"name":"Sales","columns":[{"name":"Value","dataType":"int64","sourceColumn":"Value"}],"measures":[{"name":"Total","expression":"SUM('Sales'[Value])","description":"before"}]}]}}""");
    }
    /// <summary>真实暂存不创建 staging 或备份; 多次修改一次直接保存, 完成后不保留临时发布目录.</summary>
    [Fact]
    public async Task StagesOnlyInMemoryThenSavesBatchWithoutHistoricalBackups()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        var before = File.ReadAllBytes(ModelFile);
        await edits.StageAsync(new("measure.set", "Total", "description", "after", "Sales"), default);
        var draft = await edits.StageAsync(new("measure.set", "Total", "formatString", "0.00", "Sales"), default);
        Assert.Equal(before, File.ReadAllBytes(ModelFile));
        Assert.False(Directory.Exists(Path.Combine(root, "user")));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
        Assert.Equal("after", draft.Model.Measures[0].Description);
        Assert.Equal("0.00", (await edits.ReadAsync(Entry, default)).Model.Measures[0].FormatString);
        Assert.True(draft.HasPendingChanges);
        Assert.Equal("UNSAVED_CHANGES", Assert.Throws<AppException>(() => edits.Select(null)).Code);
        var saved = await edits.SaveAsync(default);
        Assert.False(saved.HasPendingChanges); Assert.False(edits.HasPendingChanges);
        Assert.Equal("after", (await reader.ReadAsync(Entry, default)).Model.Measures[0].Description);
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        await edits.StageAsync(new("table.set", "Sales", "description", "table note"), default);
        await edits.SaveAsync(default);
        var fresh = await reader.ReadAsync(Entry, default);
        Assert.Equal("table note", fresh.Model.Tables[0].Description);
        Assert.Equal("after", fresh.Model.Measures[0].Description);
        Assert.Equal("0.00", fresh.Model.Measures[0].FormatString);
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
    }
    /// <summary>失败和取消保留草稿; 外部修改不得被覆盖, 明确放弃后展示外部最新内容.</summary>
    [Fact]
    public async Task FailedEditCancellationAndStaleSavePreserveDraft()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        await edits.StageAsync(new("measure.set", "Total", "description", "draft", "Sales"), default);
        await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(new("measure.set", "Missing", "expression", "1", "Sales"), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.SaveAsync(cancelled.Token));
        File.WriteAllText(ModelFile, File.ReadAllText(ModelFile).Replace("before", "external", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default));
        Assert.Equal("STALE_BASELINE", error.Code); Assert.True(edits.HasPendingChanges);
        Assert.Equal("draft", (await edits.ReadAsync(Entry, default)).Model.Measures[0].Description);
        Assert.Equal("external", (await reader.ReadAsync(Entry, default)).Model.Measures[0].Description);
        Assert.False(Directory.Exists(Path.Combine(root, "user")));
        Assert.Equal("external", (await edits.DiscardAsync(default)).Model.Measures[0].Description);
        Assert.False(edits.HasPendingChanges);
    }
    /// <summary>远端服务和 SSAS 即使数据格式完整也不能进入写通道.</summary>
    [Theory]
    [InlineData("powerbi-service")]
    [InlineData("ssas-xmla")]
    public async Task RejectsRemoteEdits(string kind)
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select((await reader.ReadAsync(Entry, default)) with { ProjectPath = "model://remote", Connection = new(kind, kind) });
        Assert.Equal("READ_ONLY_MODEL", (await Assert.ThrowsAsync<AppException>(() => edits.StageAsync(new("table.set", "Sales", "description", "bad"), default))).Code);
        Assert.Equal("READ_ONLY_MODEL", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(new("table.set", "Sales", "description", "bad"), default))).Code);
        Assert.False(edits.HasPendingChanges);
    }
    /// <summary>实际桥接拒绝路径注入, 一次编辑直接落盘, 完成后才允许断开.</summary>
    [Fact]
    public async Task BridgeOwnsTargetAndProtectsPendingChanges()
    {
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        ConcurrentQueue<string> messages = new();
        using var bridge = new BridgeSession(new OriginPolicy(false, null), new DoctorService(new RuntimeEnvironment()),
            messages.Enqueue, "test",
            projects: new ProjectService(edits), selectProject: _ => Task.FromResult<string?>(Entry), connections: new TestLocalFileConnection(edits).Service(), edits: edits);
        async Task<JsonElement> Call(string method, object args)
        {
            var connectionRevision = messages.Select(text => JsonSerializer.Deserialize<JsonElement>(text))
                .Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).DefaultIfEmpty().Max();
            await bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = Guid.NewGuid().ToString("N"), method, @params = args, connectionRevision }));
            using var response = JsonDocument.Parse(messages.Last()); return response.RootElement.Clone();
        }
        await Call("project.open", new { });
        Assert.Equal("UNKNOWN_METHOD", (await Call("model.saveSettings", new { })).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("UNKNOWN_METHOD", (await Call("model.updateSaveSettings", new { confirmBeforeSave = false })).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("VALIDATION_ERROR", (await Call("model.edit", new { operation = "table.set", objectName = "Sales", property = "description", value = "bad", table = "", projectPath = "other" })).GetProperty("error").GetProperty("code").GetString());
        var result = await Call("model.edit", new { operation = "table.set", objectName = "Sales", property = "description", value = "draft", table = "" });
        Assert.False(result.GetProperty("result").GetProperty("hasPendingChanges").GetBoolean());
        Assert.False((await Call("project.inspect", new { })).GetProperty("result").GetProperty("hasPendingChanges").GetBoolean());
        Assert.Equal("draft", (await reader.ReadAsync(Entry, default)).Model.Tables[0].Description);
        Assert.True((await Call("project.disconnect", new { })).TryGetProperty("result", out _));
        Assert.False(bridge.HasPendingChanges);
    }

    /// <summary>普通直接写入不依赖旧保存偏好, 不读取损坏旧文件或生成新的设置和历史备份.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSaveDoesNotReadOrCreateSavePreferences(bool existingSettings)
    {
        var user = Path.Combine(root, "user");
        var obsoleteSettings = Path.Combine(user, "model-save-settings.json");
        if (existingSettings)
        {
            Directory.CreateDirectory(user);
            File.WriteAllText(obsoleteSettings, "unreadable-old-settings");
        }
        using var workspace = new ChangeWorkspace(user);
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        var saved = await edits.EditAndSaveAsync(new("table.set", "Sales", "description", "direct"), default);
        Assert.False(saved.HasPendingChanges);
        Assert.Equal("direct", (await reader.ReadAsync(Entry, default)).Model.Tables[0].Description);
        Assert.False(Directory.Exists(Path.Combine(user, "backup")));
        Assert.Equal(existingSettings, File.Exists(obsoleteSettings));
        if (existingSettings) Assert.Equal("unreadable-old-settings", File.ReadAllText(obsoleteSettings));
    }

    /// <summary>模型定义两种格式均经同一直接保存路径, 每种操作实际回读文件验证.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSaveAllModelOperationsAcrossFormats(bool tmdl)
    {
        Write("source/model/model.bim", File.ReadAllText(ModelFile).Replace("1600", "1702", StringComparison.Ordinal));
        if (tmdl)
        {
            using var db = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(ModelFile));
            Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(root, "source/model/definition"));
            Write("source/model/definition/database.tmdl", "database\n\tcompatibilityLevel: 1702\n");
            File.Delete(ModelFile); Write("source/model/definition.pbism", """{"version":"4.2"}""");
        }
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(Entry, default));
        ModelEditRequest[] operations = [new("measure.set", "Total", "description", "note", "Sales"), new("measure.set", "Total", "formatString", "0.00", "Sales"),
            new("measure.set", "Total", "displayFolder", "Folder", "Sales"), new("measure.set", "Total", "expression", "1 + 2", "Sales"), new("table.set", "Sales", "description", "table"),
            new("udf.set", "AddOne", "expression", "(x: NUMERIC) => x + 1"), new("calendar.create", "Calendar", "config", """{"startYear":2024,"endYear":2025}"""),
            new("period.generate", "Total", "config", """{"dateTable":"Calendar","dateColumn":"Date","measureTable":"Sales","templates":["YTD","PY","YOY"],"digitsN":2,"digitsP":2,"folder":"Time"}""")];
        foreach (var request in operations)
        {
            var result = await edits.EditAndSaveAsync(request, default);
            Assert.False(result.HasPendingChanges);
            Assert.Equal(result.BaselineId, (await reader.ReadAsync(Entry, default)).BaselineId);
        }
        var fresh = await reader.ReadAsync(Entry, default);
        var measure = fresh.Model.Measures.Single(m => m.Name == "Total");
        Assert.Equal("note", measure.Description); Assert.Equal("0.00", measure.FormatString); Assert.Equal("Folder", measure.DisplayFolder); Assert.Equal("1 + 2", measure.Expression);
        Assert.Equal("table", fresh.Model.Tables.Single(t => t.Name == "Sales").Description);
        Assert.Contains(fresh.Model.Functions, f => f.Name == "AddOne"); Assert.Contains(fresh.Model.Tables, t => t.Name == "Calendar");
        Assert.Contains(fresh.Model.Measures, m => m.Expression.Contains("DATESYTD", StringComparison.Ordinal));
    }
    /// <summary>提交后文件同步失败不能留下可重复应用的草稿, 重试只完成文件保存.</summary>
    [Fact]
    public async Task CommittedModelRetriesOnlyFileCompletion()
    {
        var original = await reader.ReadAsync(Entry, default);
        var workspace = new PartialWorkspace();
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(original);
        var partial = await edits.EditAndSaveAsync(new("table.set", "Sales", "description", "saved"), default);
        Assert.True(edits.HasPendingChanges); Assert.False(partial.HasPendingChanges);
        Assert.Equal("SAVE_PENDING", partial.SaveResult?.ErrorCode);
        Assert.Equal("UNSAVED_CHANGES", Assert.Throws<AppException>(() => edits.Select(null)).Code);
        Assert.Equal("UNSAVED_CHANGES", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(new("table.set", "Sales", "description", "again"), default))).Code);
        var saved = await edits.SaveAsync(default);
        Assert.False(edits.HasPendingChanges); Assert.Null(saved.SaveResult?.ErrorCode);
        Assert.Equal(1, workspace.Applied); Assert.Equal(1, workspace.Saved); Assert.Equal(1, workspace.Completed);
    }
    private sealed class PartialWorkspace : IModelEditWorkspace, IModelSaveCompletion
    {
        public int Applied, Saved, Completed;
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.FromResult<IModelDraft>(new PartialDraft(this, baseline));
        public Task<ProjectSnapshot> CompleteSaveAsync(ProjectSnapshot saved, Action? committing, CancellationToken token)
        { Completed++; committing?.Invoke(); return Task.FromResult(saved with { SaveResult = new(true, true, true, "done") }); }
        private sealed class PartialDraft(PartialWorkspace owner, ProjectSnapshot baseline) : IModelDraft
        {
            public ProjectSnapshot Snapshot { get; private set; } = baseline;
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token) { owner.Applied++; Snapshot = Snapshot with { HasPendingChanges = true }; return Task.CompletedTask; }
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            { owner.Saved++; committing?.Invoke(); return Task.FromResult(Snapshot with { HasPendingChanges = false, SaveResult = new(true, false, false, "pending", "SAVE_PENDING") }); }
            public void Dispose() { }
        }
    }
    /// <summary>写入本测试明确拥有的夹具路径.</summary>
    private void Write(string relative, string value) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
    /// <summary>只清理唯一临时测试目录.</summary>
    public void Dispose() => Directory.Delete(root, true);
}
