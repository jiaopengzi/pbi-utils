// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实 PBIP 的持续配置、文件归属、整批失败与大数据保存边界.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using System.Security.Cryptography;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;
using PbiUtils.Application.Udf;
using System.Text.Json;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-configuration-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] AliasColumns = ["日期", "Dates", "Year", "Month"];
    private static readonly string[] MixedColumns = ["C01_Dates", "C28_Year", "C13_Month"];
    private static readonly string[] NewColumns = ["Dates", "Month", "Year"];

    /// <summary>未涉及的大 CSV 超过原 128MiB 限制仍可保存小定义, 原 CSV 字节和时间戳保持.</summary>
    [Fact]
    public async Task SmallModelEditPreservesLargeUnchangedCsv()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var csv = Path.Combine(root, "project", "data", "large.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        using (var output = File.Create(csv)) output.SetLength(129L * 1024 * 1024);
        var stamp = File.GetLastWriteTimeUtc(csv);
        string Hash() { using var input = File.OpenRead(csv); return Convert.ToHexString(SHA256.HashData(input)); }
        var before = Hash();
        var reader = new ProjectReader();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        edits.Select(await reader.ReadAsync(entry, default));
        await edits.StageAsync(new("table.set", "销售", "description", "持续配置"), default);
        var saved = await edits.SaveAsync(default);
        Assert.Equal("持续配置", saved.Model.Tables.Single().Description);
        Assert.Equal(before, Hash());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(csv));
    }

    /// <summary>创建后同目录依次配置四段, 不重新创建工程, 重复配置不保存.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task CreatedProjectSupportsSequentialSectionsAndNoChange(string kind)
    {
        var user = Path.Combine(root, "user");
        using var runtime = new ScaffoldRuntime(user);
        var template = await runtime.Catalog.GetAsync("builtin:" + kind, default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, null, null, null), project, project, null, default);
        var entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
        var reader = new ProjectReader();
        using var workspace = new ChangeWorkspace(user);
        using var edits = new ModelEditService(reader, workspace);
        using var compiler = new ModelConfigurationCompiler(runtime.Resolver);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);
        var commits = 0;
        async Task Apply(ModelConfigurationRequest request)
        {
            var prepared = await compiler.PrepareAsync(request, snapshot, default);
            try { snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, () => commits++, ct => compiler.VerifyAsync(prepared, ct)); }
            finally { if (!edits.HasPendingChanges) compiler.Release(prepared); }
            Assert.False(edits.HasPendingChanges);
            Assert.Equal(entry, snapshot.ProjectPath);
        }
        await Apply(new(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)));
        var firstSampleCommits = commits;
        var originalCsv = Path.Combine(project, "data", "T00_产品表.csv");
        var originalStamp = File.GetLastWriteTimeUtc(originalCsv);
        await Apply(new(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)));
        Assert.Equal(firstSampleCommits, commits);
        Assert.Equal(originalStamp, File.GetLastWriteTimeUtc(originalCsv));
        var unchanged = snapshot.Model.TemplateMeasureConfiguration?.Measures.Where(value => value.Name != "author") ?? [];
        await Apply(new(Measures: [.. unchanged, new("author", "continuous", "Uncategorized", true, "说明", "模板")]));
        Assert.Equal("\n\"continuous\"", snapshot.Model.Measures.Single(measure => measure.Name == "author").Expression);
        var priorCalendar = snapshot.Model.Tables.SingleOrDefault(table => table.Name == "Calendar")?.CalendarCapability?.OutputColumns;
        await Apply(new(Calendar: new("full", 2024, 2024)));
        var calendar = snapshot.Model.Tables.Single(table => table.Name == "Calendar");
        var mapping = calendar.CalendarCapability!.OutputColumns!;
        Assert.Equal(ScaffoldCalendarFields.All.Select(field => field.Id).Order(), mapping.Select(column => column.Field).Order());
        Assert.Equal(63, mapping.Select(column => column.Field).Distinct(StringComparer.Ordinal).Count());
        if (priorCalendar is not null)
            Assert.All(priorCalendar, prior => Assert.Contains(mapping, column => column.Name == prior.Name && column.SourceColumn == prior.SourceColumn && column.Field == prior.Field));
        var local = await runtime.Local.CreateAsync(new(1, "Configuration.Answer", "1.0.0", "() => 42", "answer", []), default);
        var udf = new ModelConfigurationRequest(Udfs: [new("local", local.Id, local.Definition.Version, [local.Definition.Name], Baseline: local.Baseline)], AllowCompatibilityUpgrade: true);
        await Apply(udf);
        Assert.Equal(1702, snapshot.Model.CompatibilityLevel);
        Assert.Contains(snapshot.Model.Functions, function => function.Name == "Configuration.Answer");
        var before = snapshot.BaselineId;
        var count = commits;
        await Apply(udf);
        Assert.Equal(before, snapshot.BaselineId);
        Assert.Equal(count, commits);
        Assert.False(Directory.Exists(Path.Combine(project, "data", "sample")));
        Assert.Single(Directory.GetFiles(project, "*.pbip", SearchOption.AllDirectories));
    }

    /// <summary>整批后项冲突不会留下前项, 空段不删除; 模板度量的明确后续请求覆盖人工更改.</summary>
    [Fact]
    public async Task ConflictsAndNullSectionsPreserveActualProject()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var source = File.ReadAllBytes(Path.Combine(root, "project", "model", "model.bim"));
        var collision = new ModelConfigurationRequest(Measures: [new("new-first", "value", "Uncategorized", true, "", "")], Calendar: new("simple", 2024, 2024, "销售"));
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(collision, snapshot, default))).Code);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(root, "project", "model", "model.bim")));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Measures: [new("owned", "v1", "Uncategorized", true, "", "")]), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        var empty = await compiler.PrepareAsync(new(), snapshot, default);
        var saved = await edits.EditConfigurationAndSaveAsync(empty, default, () => throw new InvalidOperationException("empty commit"));
        compiler.Release(empty);
        Assert.Equal(snapshot.BaselineId, saved.BaselineId);
        Assert.Contains(saved.Model.Measures, measure => measure.Name == "owned");
        var modelFile = Path.Combine(root, "project", "model", "model.bim");
        using (var db = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
        {
            db.Model.Tables["Measure"].Measures["owned"].Expression = "\"manual\"";
            File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(db));
        }
        snapshot = await reader.ReadAsync(entry, default);
        edits.Select(snapshot);
        var overwrite = await compiler.PrepareAsync(new(Measures: [new("owned", "v2", "Uncategorized", true, "", "")]), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(overwrite, default, beforeCommit: token => compiler.VerifyAsync(overwrite, token));
        Assert.Equal("\n\"v2\"", snapshot.Model.Measures.Single(measure => measure.Name == "owned").Expression);
    }

    /// <summary>未知同名原始 CSV 不覆盖, 已归属 CSV 外部改写后也不覆盖.</summary>
    [Fact]
    public async Task UnknownAssetAndChangedFrozenAssetAreRejected()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var file = Path.Combine(root, "project", "data", "T00_产品表.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "manual");
        var request = new ModelConfigurationRequest(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42));
        Assert.Equal("CONFIGURATION_ASSET_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(request, snapshot, default))).Code);
        Assert.Equal("manual", File.ReadAllText(file)); File.Delete(file);
        var prepared = await compiler.PrepareAsync(request, snapshot, default);
        File.WriteAllText(file, "external");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => compiler.VerifyAsync(prepared, default))).Code);
        compiler.Release(prepared);
    }

    /// <summary>Config 业务 JSON 归属跟随同一模型候选与保存, 冻结资产摘要进入完整指纹.</summary>
    [Fact]
    public async Task FrozenAssetsAreOwnedByCandidateAndPersistedModel()
    {
        Directory.CreateDirectory(Path.Combine(root, "project", "pbi-utils"));
        var entry = PbipFixture.Create(Path.Combine(root, "project", "pbip"));
        var modelFile = Path.Combine(root, "project", "pbip", "model", "model.bim");
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);
        var prepared = await compiler.PrepareAsync(new(Measures: [new("owned", "v1", "Uncategorized", true, "", "")], MeasuresMode: "config"), snapshot, default);
        var asset = Assert.Single(prepared.Configuration!.Assets);
        Assert.Equal("pbi-utils/config.json", asset.RelativePath);
        using (var candidate = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
        {
            ModelMutation.Apply(candidate, new(entry, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
            Assert.Equal(asset.AfterHash, ModelConfigurationOwnership.ReadRoles(candidate.Model).Assets[asset.RelativePath]);
            var before = ModelConfigurationReadback.Fingerprint(candidate);
            var changed = ModelConfigurationOwnership.ReadRoles(candidate.Model);
            changed.Assets[asset.RelativePath] = new string('0', 64);
            ModelConfigurationOwnership.SetRoles(candidate.Model, changed);
            Assert.NotEqual(before, ModelConfigurationReadback.Fingerprint(candidate));
        }

        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
        using (var saved = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
            Assert.Equal(asset.AfterHash, ModelConfigurationOwnership.ReadRoles(saved.Model).Assets[asset.RelativePath]);
        Assert.True(File.Exists(Path.Combine(root, "project", "pbi-utils", "config.json")));
        var config = Path.Combine(root, "project", asset.RelativePath);
        File.AppendAllText(config, "manual");
        var fallback = await compiler.PrepareAsync(new(Measures: [new("owned", "v2", "Uncategorized", true, "", "")], MeasuresMode: "config"), snapshot, default);
        Assert.Equal("tmdl", fallback.Configuration!.Request.MeasuresMode);
        Assert.DoesNotContain(fallback.Configuration.Assets, asset => asset.RelativePath == "pbi-utils/config.json");
        compiler.Release(fallback);
        Assert.EndsWith("manual", File.ReadAllText(config), StringComparison.Ordinal);
    }

    /// <summary>逐包安装将精确锁与原始许可保存在同一模型, 不产生附加配置文件或提交时重解析 latest.</summary>
    [Fact]
    public async Task SequentialPackagesPreserveLocksLicensesAndNeverResolveLatestAtCommit()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user"));
        var provider = new TestPackages();
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(provider, runtime.Local));
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        foreach (var id in new[] { "PkgA", "PkgB" })
        {
            var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", id, "latest", [id + ".Answer"])], AllowCompatibilityUpgrade: true), snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
        }
        Assert.Equal(2, provider.VersionRequests);
        Assert.Equal(2, snapshot.Model.Functions.Count);
        var installedLock = ReadInstalledLock();
        using (var document = JsonDocument.Parse(installedLock))
        {
            Assert.Equal(2, document.RootElement.GetProperty("packages").GetArrayLength());
            Assert.Equal(2, document.RootElement.GetProperty("functions").GetArrayLength());
        }
        var licenses = ModelConfigurationUdfLocks.Read(installedLock).Packages.SelectMany(package => package.LicenseFiles.Values).ToArray();
        Assert.Equal(2, licenses.Length);
        Assert.Contains("License-PkgA", licenses); Assert.Contains("License-PkgB", licenses);
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "model", "model.bim"))))
            Assert.Contains("License-PkgA", database.Model.Annotations["PbiUtils.Configuration.UdfLock.v1"].Value);
        Assert.False(Directory.Exists(Path.Combine(root, "project", "config")));
    }

    /// <summary>首次提交前失败后重试保存仍复核来源, 来源改变不绕过冻结校验也不修改文件.</summary>
    [Fact]
    public async Task SaveRetryRevalidatesFrozenSources()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user")); var provider = new TestPackages();
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(provider, runtime.Local));
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "latest", ["PkgA.Answer"])], AllowCompatibilityUpgrade: true), snapshot, default);
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: async ct =>
        {
            if (calls++ == 0) throw new IOException("precommit fault");
            await compiler.VerifyAsync(prepared, ct);
        }));
        Assert.True(edits.HasPendingChanges);
        provider.Changed = true;
        Assert.Equal("UDF_PACKAGE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => edits.SaveAsync(default))).Code);
        Assert.Equal(2, calls);
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
        await edits.DiscardAsync(default);
        Assert.False(edits.HasPendingChanges);
    }

    /// <summary>全部51期间复用同一生成器, 更新和重复提交只触及已归属对象.</summary>
    [Fact]
    public async Task FullPeriodBatchCanUpdateWithoutRecreatingObjects()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler(); using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var periods = new ScaffoldPeriodOptions("Dates", ScaffoldCalendar.Generate(new("simple", 2024, 2024, "Dates")).DateColumn, "销售", ["销售额"], [.. ScaffoldTimeIntelligence.Templates.Select(template => template.Id)]);
        var request = new ModelConfigurationRequest(Calendar: new("simple", 2024, 2024, "Dates"), Periods: periods);
        var prepared = await compiler.PrepareAsync(request, snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        Assert.Equal(52, snapshot.Model.Measures.Count);
        prepared = await compiler.PrepareAsync(new(Periods: periods with { Folder = "updated" }), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        Assert.Equal(52, snapshot.Model.Measures.Count);
        Assert.All(snapshot.Model.Measures.Where(measure => measure.Name != "销售额"), measure => Assert.Contains("updated", measure.DisplayFolder));
        var baseline = snapshot.BaselineId;
        prepared = await compiler.PrepareAsync(new(Periods: periods with { Folder = "updated" }), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, () => throw new InvalidOperationException("unchanged period commit"));
        compiler.Release(prepared); Assert.Equal(baseline, snapshot.BaselineId);
    }

    /// <summary>无目录 Desktop 允许纯模型日期与受信宿主批准的 UDF 升级, 文件资产仍被禁止.</summary>
    [Fact]
    public async Task DesktopConfigurationKeepsPureModelBoundary()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var snapshot = await new ProjectReader().ReadAsync(entry, default);
        snapshot = snapshot with { ProjectPath = "model://desktop-fixture", Connection = new("desktop", "fixture") };
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user")); var provider = new TestPackages();
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(provider, runtime.Local));
        Assert.Equal("CONFIGURATION_ASSETS_UNSUPPORTED", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Sample: new("zh-cn", 2, 1, 7, "2024-01-01", "2024-04-01", 42)), snapshot, default))).Code);
        Assert.Equal("CONFIGURATION_COMPATIBILITY_APPROVAL_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "latest", ["PkgA.Answer"])]), snapshot, default))).Code);
        var udfPrepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "latest", ["PkgA.Answer"])], AllowCompatibilityUpgrade: true), snapshot, default);
        Assert.Empty(udfPrepared.Configuration!.Assets);
        Assert.Null(udfPrepared.Configuration.AssetRoot);
        compiler.Release(udfPrepared);
        var prepared = await compiler.PrepareAsync(new(Calendar: new("full", 2024, 2024, "Dates")), snapshot, default);
        Assert.Empty(prepared.Configuration!.Assets);
        using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "model", "model.bim")));
        ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        var source = ((TOM.MPartitionSource)database.Model.Tables["Dates"].Partitions[0].Source).Expression;
        var function = database.Model.Expressions[ScaffoldCalendarWriter.FunctionBinding(database.Model.Tables["Dates"])!.FunctionName];
        Assert.Contains("List.Dates", function.Expression); Assert.DoesNotContain("File.Contents", function.Expression);
        Assert.Contains(function.Name, source); Assert.DoesNotContain("List.Dates", source);
        Assert.Equal(63, database.Model.Tables["Dates"].Columns.Count);
        Assert.Equal(1606, database.CompatibilityLevel);
    }

    /// <summary>冻结摘要必须逐项匹配候选表的真实名称和源列, 保留别名与混合编号.</summary>
    [Theory]
    [InlineData(true, "aliases")]
    [InlineData(false, "aliases")]
    [InlineData(true, "mixed")]
    [InlineData(false, "mixed")]
    [InlineData(true, "new")]
    [InlineData(false, "new")]
    public async Task CalendarConfirmationFreezesActualColumns(bool fileBased, string scenario)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var modelFile = Path.Combine(root, "project", "model", "model.bim");
        using var database = TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(modelFile));
        if (scenario != "new")
        {
            var table = new TOM.Table { Name = "Dates" }; database.Model.Tables.Add(table);
            if (scenario == "aliases") table.Columns.Add(new TOM.DataColumn { Name = "日期", SourceColumn = "日期", DataType = TOM.DataType.DateTime });
            table.Columns.Add(new TOM.DataColumn { Name = "C01_Dates", SourceColumn = "C01_Dates", DataType = TOM.DataType.DateTime });
            table.Columns.Add(new TOM.DataColumn { Name = "Year", SourceColumn = "ActualYearSource", DataType = TOM.DataType.Int64 });
            ScaffoldCalendarWriter.Write(table, new("full", 2024, 2024, "Dates", Fields: ["Dates", "Year"], NumberedColumns: false), true, null, [], [], default);
            ModelConfigurationOwnership.Mark(table, table.Annotations);
            await File.WriteAllTextAsync(modelFile, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var before = database.Model.Tables.Find("Dates")?.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        if (!fileBased) snapshot = snapshot with { ProjectPath = "model://desktop-fixture", Connection = new("desktop", "fixture") };
        using var compiler = new ModelConfigurationCompiler();
        var options = new ScaffoldCalendarOptions("full", 2024, 2025, "Dates", Fields: ["Dates", "Year", "Month"], NumberedColumns: scenario == "mixed");
        var prepared = await compiler.PrepareAsync(new(Calendar: options), snapshot, default);
        var output = Assert.Single(prepared.Configuration!.CalendarOutputs);
        using var unchanged = database.Clone();
        ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));
        var actual = database.Model.Tables["Dates"].Columns.OfType<TOM.DataColumn>().ToArray();
        Assert.Equal("Dates", output.TableName);
        Assert.Equal(actual.Select(column => column.Name), output.Columns.Select(column => column.Name));
        Assert.Equal(actual.Select(column => column.SourceColumn), output.Columns.Select(column => column.SourceColumn));
        Assert.All(output.Columns, column => Assert.Equal(!before.Contains(column.Name), column.Added));
        if (scenario != "new") Assert.Equal("ActualYearSource", output.Columns.Single(column => column.Field == "Year").SourceColumn);
        if (scenario == "aliases") Assert.Equal(AliasColumns, output.Columns.Select(column => column.Name));
        if (scenario == "mixed") Assert.Equal(MixedColumns, output.Columns.Select(column => column.Name));
        if (scenario == "new") Assert.Equal(NewColumns, output.Columns.Select(column => column.Name));
        var again = await compiler.PrepareAsync(new(Calendar: options), snapshot, default);
        Assert.Equal(prepared.Value, again.Value);
        if (!fileBased && scenario != "new")
        {
            var table = snapshot.Model.Tables.Single(value => value.Name == "Dates");
            var capability = table.CalendarCapability!;
            var modified = capability.OutputColumns!.Select(column => column.Field == "Year" ? column with { SourceColumn = "DifferentFrozenSource" } : column).ToArray();
            var different = snapshot with { Model = snapshot.Model with { Tables = snapshot.Model.Tables.Select(value => value.Name == "Dates" ? value with { CalendarCapability = capability with { OutputColumns = modified } } : value).ToArray() } };
            var mismatched = await compiler.PrepareAsync(new(Calendar: options), different, default);
            Assert.NotEqual(prepared.Value, mismatched.Value);
            using var candidate = new MemoryModelDraft(snapshot, unchanged.Clone(), (_, _, _) => throw new InvalidOperationException("不得进入保存"));
            Assert.Equal("CONFIGURATION_CALENDAR_MAPPING_CHANGED", (await Assert.ThrowsAsync<AppException>(() => candidate.ApplyAsync(mismatched, default))).Code);
            Assert.False(candidate.Snapshot.HasPendingChanges);
        }
    }

    /// <summary>工具向已归属日期表写期间后, 后续日期更新不应被当成人工冲突.</summary>
    [Fact]
    public async Task PeriodMeasuresPreserveVerifiedParentOwnership()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var compiler = new ModelConfigurationCompiler(); using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        async Task Apply(ModelConfigurationRequest request)
        {
            var prepared = await compiler.PrepareAsync(request, snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        }
        var date = ScaffoldCalendar.Generate(new("simple", 2024, 2024, "Dates")).DateColumn;
        await Apply(new(Calendar: new("simple", 2024, 2024, "Dates")));
        await Apply(new(Periods: new("Dates", date, "Dates", ["销售额"], [ScaffoldTimeIntelligence.Templates[0].Id])));
        await Apply(new(Calendar: new("simple", 2025, 2025, "Dates")));
        Assert.Contains(snapshot.Model.Measures, measure => measure.Table == "Dates");
    }

    /// <summary>不同包同名函数不得借用旧生成归属替换旧依赖的语义.</summary>
    [Fact]
    public async Task CrossPackageFunctionCollisionPreservesModelLockAndLicense()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(new CollisionPackages(), runtime.Local));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "1.0.0", ["PkgA.Caller"])], AllowCompatibilityUpgrade: true), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        var originalLock = ReadInstalledLock();
        Assert.Equal("UDF_NAME_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgB", "1.0.0", ["Shared.F"])]), snapshot, default))).Code);
        Assert.Equal(originalLock, ReadInstalledLock());
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
        Assert.Equal("License-PkgA", Assert.Single(ModelConfigurationUdfLocks.Read(ReadInstalledLock()).Packages).LicenseFiles["LICENSE.txt"]);
        Assert.False(Directory.Exists(Path.Combine(root, "project", "config")));
    }

    /// <summary>展示模型形状相同不代表新分区或锁已落盘, 安全指纹必须识别旧文件.</summary>
    [Fact]
    public async Task ConfigurationReadbackRejectsOldPartitionAndAnnotations()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var modelFile = Path.Combine(root, "project", "model", "model.bim");
        using var current = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile));
        current.Model.Tables["销售"].Partitions.Add(new TOM.Partition { Name = "data", Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table({\"Date\"}, {{#date(2024,1,1)}})" } });
        File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(current));
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        Assert.Equal(ModelConfigurationReadback.Fingerprint(current), await ModelConfigurationReadback.FingerprintFileAsync(snapshot, default));
        using var expected = current.Clone();
        ((TOM.MPartitionSource)expected.Model.Tables["销售"].Partitions[0].Source).Expression = "#table({\"Date\"}, {{#date(2025,1,1)}})";
        Assert.Equal(JsonSerializer.Serialize(ModelSnapshotMapper.Map(current)), JsonSerializer.Serialize(ModelSnapshotMapper.Map(expected)));
        Assert.NotEqual(ModelConfigurationReadback.Fingerprint(expected), await ModelConfigurationReadback.FingerprintFileAsync(snapshot, default));
        ((TOM.MPartitionSource)expected.Model.Tables["销售"].Partitions[0].Source).Expression = ((TOM.MPartitionSource)current.Model.Tables["销售"].Partitions[0].Source).Expression;
        expected.Model.Annotations.Add(new TOM.Annotation { Name = "PbiUtils.Configuration.UdfLock.v1", Value = "new-lock" });
        Assert.NotEqual(ModelConfigurationReadback.Fingerprint(expected), await ModelConfigurationReadback.FingerprintFileAsync(snapshot, default));
    }

    /// <summary>同来源同包完整已安装子集可切换精确tag与回退, 不重复创建函数.</summary>
    [Fact]
    public async Task CompleteInstalledPackageSubsetCanSwitchExactVersions()
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(new TestPackages(), runtime.Local));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        foreach (var version in new[] { "1.0.0", "2.0.0", "1.0.0" })
        {
            var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", version, ["PkgA.Answer"])], AllowCompatibilityUpgrade: true), snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
            Assert.Single(snapshot.Model.Functions);
            using var document = JsonDocument.Parse(ReadInstalledLock());
            Assert.Equal(version, document.RootElement.GetProperty("packages")[0].GetProperty("version").GetString());
        }
    }

    /// <summary>缺少旧已安装函数或其他库仍依赖旧版本时, 完整项目保持原字节.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageVersionSwitchRejectsPartialCoverageAndDependentLock(bool dependency)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user")); var provider = new VersionPackages();
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(provider, runtime.Local));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var selection = dependency ? new UdfSelection("daxlib", "PkgB", "1.0.0", ["PkgB.Caller"]) : new UdfSelection("daxlib", "PkgA", "1.0.0", ["PkgA.One", "PkgA.Two"]);
        var prepared = await compiler.PrepareAsync(new(Udfs: [selection], AllowCompatibilityUpgrade: true), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        var originalLock = ReadInstalledLock();
        provider.Latest = "2.0.0";
        Assert.Equal("UDF_VERSION_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "2.0.0", ["PkgA.One"])]), snapshot, default))).Code);
        Assert.Equal(originalLock, ReadInstalledLock());
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
    }

    /// <summary>远端切tag必须重解析全部已安装本地函数, 旧依赖拒绝, 无依赖同批重选可通过.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageVersionSwitchRequiresReparsedLocalFunctions(bool dependency)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user")); var provider = new VersionPackages();
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(provider, runtime.Local));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var local = await runtime.Local.CreateAsync(new(1, "Local.Caller", "1.0.0", dependency ? "() => PkgA.One()" : "() => 42", "caller",
            dependency ? [new("daxlib", "PkgA", "1.0.0", "PkgA.One")] : []), default);
        var localSelection = new UdfSelection("local", local.Id, local.Definition.Version, [local.Definition.Name], Baseline: local.Baseline);
        var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "1.0.0", ["PkgA.One"]), localSelection], AllowCompatibilityUpgrade: true), snapshot, default);
        snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
        var originalLock = ReadInstalledLock();
        provider.Latest = "2.0.0";
        var remote = new UdfSelection("daxlib", "PkgA", "2.0.0", ["PkgA.One"]);
        Assert.Equal("UDF_VERSION_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [remote]), snapshot, default))).Code);
        Assert.Equal(originalLock, ReadInstalledLock());
        Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
        Assert.False(Directory.Exists(Path.Combine(root, "project", "config")));
        if (dependency)
        {
            Assert.Equal("UDF_VERSION_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [remote, localSelection]), snapshot, default))).Code);
            Assert.Equal(originalLock, ReadInstalledLock());
        }
        else
        {
            prepared = await compiler.PrepareAsync(new(Udfs: [remote, localSelection]), snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: ct => compiler.VerifyAsync(prepared, ct));
            Assert.Equal(2, snapshot.Model.Functions.Count);
            using var document = JsonDocument.Parse(ReadInstalledLock());
            Assert.Equal("2.0.0", document.RootElement.GetProperty("packages")[0].GetProperty("version").GetString());
        }
    }

    /// <summary>坏归属仍阻止相邻日期操作; 纯模板明确覆盖可重建本段状态, 准备阶段不写原文件.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":1,\"Roles\":{},\"Retail\":false,\"Assets\":[]}")]
    [InlineData("{\"SchemaVersion\":2,\"Roles\":{},\"Retail\":false,\"Assets\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Roles\":{},\"Retail\":false,\"Assets\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"Roles\":{},\"Retail\":false,\"Assets\":{\"data/a.csv\":\"invalid\"}}")]
    [InlineData("{\"SchemaVersion\":1,\"Roles\":{},\"Retail\":false,\"Assets\":{\"../data/a.csv\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}")]
    public async Task InvalidOwnershipBlocksCalendarButAllowsExplicitTemplateRecovery(string text)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project"));
        var model = Path.Combine(root, "project", "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(model)))
        {
            database.Model.Annotations.Add(new TOM.Annotation { Name = ModelConfigurationOwnership.RolesStamp, Value = text });
            File.WriteAllText(model, TOM.JsonSerializer.SerializeDatabase(database));
        }
        var before = File.ReadAllBytes(model);
        using var compiler = new ModelConfigurationCompiler();
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        Assert.Equal("CONFIGURATION_OWNERSHIP_INVALID", (await Assert.ThrowsAsync<AppException>(() =>
            compiler.PrepareAsync(new(Calendar: new("simple", 2024, 2024)), snapshot, default))).Code);
        var prepared = await compiler.PrepareAsync(new(Measures: [new("owned", "text", "Uncategorized", true, "", "")]), snapshot, default);
        Assert.Equal(before, File.ReadAllBytes(model));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var saved = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));
        Assert.Equal("\n\"text\"", saved.Model.Measures.Single(measure => measure.Name == "owned").Expression);
        using var recovered = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(model));
        Assert.Empty(ModelConfigurationOwnership.ReadRoles(recovered.Model).Roles);
    }

    /// <summary>外部删除/改名/编辑旧已装函数后刷新, 不能由旧锁在安装B时静默恢复A.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("edit")]
    public async Task ChangedInstalledFunctionCannotBeResurrectedFromOldLock(string change)
    {
        var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
        using var runtime = new ScaffoldRuntime(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(new TestPackages(), runtime.Local));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
        var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

        var prepared = await compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgA", "1.0.0", ["PkgA.Answer"])], AllowCompatibilityUpgrade: true), snapshot, default);
        await edits.EditConfigurationAndSaveAsync(prepared, default);
        var model = Path.Combine(root, "project", "model", "model.bim");
        using (var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(model)))
        {
            var function = database.Model.Functions["PkgA.Answer"];
            if (change == "delete") database.Model.Functions.Remove(function);
            else if (change == "rename") function.Name = "Manual.Renamed";
            else function.Expression = "() => 99";
            File.WriteAllText(model, TOM.JsonSerializer.SerializeDatabase(database));
        }
        snapshot = await reader.ReadAsync(entry, default);
        var before = File.ReadAllBytes(model); var oldLock = ReadInstalledLock();
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => compiler.PrepareAsync(new(Udfs: [new("daxlib", "PkgB", "1.0.0", ["PkgB.Answer"])]), snapshot, default))).Code);
        Assert.Equal(before, File.ReadAllBytes(model)); Assert.Equal(oldLock, ReadInstalledLock());
        Assert.Equal("License-PkgA", Assert.Single(ModelConfigurationUdfLocks.Read(oldLock).Packages).LicenseFiles["LICENSE.txt"]);
        Assert.False(Directory.Exists(Path.Combine(root, "project", "config")));
    }

    /// <summary>读取真实已保存 BIM 的安装锁, 测试不以附加文件替代模型提交事实.</summary>
    private string ReadInstalledLock()
    {
        using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "project", "model", "model.bim")));
        return database.Model.Annotations[ModelConfigurationUdfLocks.Annotation].Value;
    }

    private sealed class VersionPackages : IUdfPackageProvider
    {
        internal string Latest { get; set; } = "1.0.0";
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UdfPackageSummary>>([]);
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([Latest]);
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => Task.FromResult(new UdfPackage(packageId, version, new string('a', 64), "fixture", "fixture",
            new Dictionary<string, string> { ["LICENSE.txt"] = "License-" + packageId }, packageId == "PkgB" ? [new("PkgA", "1.0.0")] : [], packageId == "PkgA"
                ? [new("PkgA.One", "() => 1", "", new Dictionary<string, string>()), new("PkgA.Two", "() => 2", "", new Dictionary<string, string>())]
                : [new("PkgB.Caller", "() => PkgA.One()", "", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch));
    }

    private sealed class CollisionPackages : IUdfPackageProvider
    {
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UdfPackageSummary>>([]);
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["1.0.0"]);
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => Task.FromResult(new UdfPackage(packageId, version, new string('a', 64), "fixture", "fixture",
            new Dictionary<string, string> { ["LICENSE.txt"] = "License-" + packageId }, [], packageId == "PkgA"
                ? [new("Shared.F", "() => 1", "", new Dictionary<string, string>()), new("PkgA.Caller", "() => Shared.F()", "", new Dictionary<string, string>())]
                : [new("Shared.F", "() => 2", "", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch));
    }

    private sealed class TestPackages : IUdfPackageProvider
    {
        internal int VersionRequests { get; private set; }
        internal bool Changed { get; set; }
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UdfPackageSummary>>([]);
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) { VersionRequests++; return Task.FromResult<IReadOnlyList<string>>(["1.0.0"]); }
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => Task.FromResult(new UdfPackage(packageId, version,
            new string(Changed ? 'b' : 'a', 64), "fixture", "fixture", new Dictionary<string, string> { ["LICENSE.txt"] = "License-" + packageId }, [],
            [new(packageId + ".Answer", "() => 42", "answer", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
