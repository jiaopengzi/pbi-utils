// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfLintTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : UDF 调用词法、完整根覆盖、依赖闭包、基线与真实 PBIP 清理保存测试.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Code.Dax;
using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class UdfLintTests
{
    /// <summary>有效全集可超过128项, 仍受8192名称及既有30000字符结构化输入预算保护.</summary>
    [Fact]
    public void DeleteBatchAllowsLargerBoundedSets()
    {
        var names = Enumerable.Range(0, 129).Select(i => "Lib.F" + i).ToArray();
        Assert.Equal(129, UdfDeleteBatch.Parse(JsonSerializer.Serialize(new UdfDeleteBatch("p", "b", names), JsonSerializerOptions.Web)).Names.Count);
        var oversized = Enumerable.Range(0, 400).Select(i => "Lib." + new string('x', 80) + i).ToArray();
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => UdfDeleteBatch.Parse(JsonSerializer.Serialize(new UdfDeleteBatch("p", "b", oversized), JsonSerializerOptions.Web))).Code);
    }
    /// <summary>真实调用不受空白、注释和大小写影响; 字符串、表列引用及名称子串不构成调用.</summary>
    [Fact]
    public void CallsIgnoreLiteralsCommentsAndSubstrings()
    {
        Assert.True(DaxFormatter.TryReadFunctionCalls("lib /*x*/ . Used /*y*/ (1) + \"Lib.Unused()\" + 'Lib.Unused'[C] + [Lib.Unused] // Lib.Unused()\n-- Lib.Unused()\n+ OtherLib.Unused()", out var calls));
        Assert.Contains("LIB.USED", calls);
        Assert.DoesNotContain("Lib.Unused", calls);
        Assert.Contains("OtherLib.Unused", calls);
    }

    /// <summary>不完整或暂不支持的词法不返回可删候选, 取消继续传播.</summary>
    [Theory]
    [InlineData("Lib.Used(")]
    [InlineData("\"unterminated")]
    [InlineData("/* unterminated")]
    [InlineData("Lib.'Used'()")]
    [InlineData("Lib::Used()")]
    [InlineData("Lib.Used(?)")]
    public void UnknownExpressionsBlockCleanup(string expression)
    {
        using var db = Database();
        db.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Root", Expression = expression });
        Assert.False(UdfModelLint.Analyze(db, Snapshot(db)).Complete);
        Assert.Empty(UdfModelLint.Analyze(db, Snapshot(db)).Unused);
    }

    /// <summary>所有模型 DAX 根保护函数及传递依赖; 无根递归环可整批清理, M 同名文本不伪造引用.</summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("column")]
    [InlineData("table")]
    [InlineData("role")]
    [InlineData("calculation")]
    [InlineData("format")]
    [InlineData("detail")]
    [InlineData("kpi")]
    [InlineData("selection-none")]
    [InlineData("selection-multiple")]
    [InlineData("selection-none-format")]
    [InlineData("selection-multiple-format")]
    public void CompleteModelRootsProtectDependencies(string root)
    {
        using var db = Database();
        var table = db.Model.Tables[0];
        switch (root)
        {
            case "measure": table.Measures.Add(new TOM.Measure { Name = "Root", Expression = "lib.used()" }); break;
            case "column": table.Columns.Add(new TOM.CalculatedColumn { Name = "C", DataType = TOM.DataType.Int64, Expression = "Lib.Used()" }); break;
            case "table": table.Partitions.Add(new TOM.Partition { Name = "P", Source = new TOM.CalculatedPartitionSource { Expression = "{Lib.Used()}" } }); break;
            case "role":
                var role = new TOM.ModelRole { Name = "R" }; db.Model.Roles.Add(role);
                role.TablePermissions.Add(new TOM.TablePermission { Name = table.Name, Table = table, FilterExpression = "Lib.Used() > 0" }); break;
            case "calculation": table.CalculationGroup = new TOM.CalculationGroup(); table.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "I", Expression = "Lib.Used()" }); break;
            case "format": table.Measures.Add(new TOM.Measure { Name = "Root", Expression = "1", FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "Lib.Used()" } }); break;
            case "detail": table.DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "{Lib.Used()}" }; break;
            case "kpi": table.Measures.Add(new TOM.Measure { Name = "Root", Expression = "1", KPI = new TOM.KPI { TargetExpression = "Lib.Used()" } }); break;
            case "selection-none":
            case "selection-multiple":
            case "selection-none-format":
            case "selection-multiple-format":
                table.CalculationGroup = new TOM.CalculationGroup();
                var selection = new TOM.CalculationGroupExpression
                {
                    Expression = root.EndsWith("format", StringComparison.Ordinal) ? "SELECTEDMEASURE()" : "Lib.Used()",
                    FormatStringDefinition = new TOM.FormatStringDefinition { Expression = root.EndsWith("format", StringComparison.Ordinal) ? "Lib.Used()" : "\"0\"" }
                };
                if (root.StartsWith("selection-none", StringComparison.Ordinal)) table.CalculationGroup.NoSelectionExpression = selection;
                else table.CalculationGroup.MultipleOrEmptySelectionExpression = selection;
                break;
        }
        db.Model.Expressions.Add(new TOM.NamedExpression { Name = "M", Kind = TOM.ExpressionKind.M, Expression = "let text = \"Lib.CycleA()\" in text" });
        table.Partitions.Add(new TOM.Partition { Name = "M", Source = new TOM.MPartitionSource { Expression = "let t = #table({}, {}) in t" } });
        var result = UdfModelLint.Analyze(db, Snapshot(db));
        Assert.True(result.Complete);
        Assert.Equal(["Lib.CycleA", "Lib.CycleB", "Lib.Unused"], result.Unused);
    }

    /// <summary>完整候选集合一次删除, 拒绝删除仍被使用的函数和未用闭包的部分集合.</summary>
    [Fact]
    public void DeleteRequiresEntireUnusedClosure()
    {
        using var db = Database();
        db.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Root", Expression = "Lib.Used()" });
        Assert.Equal("UDF_LINT_UNSAFE", Assert.Throws<AppException>(() => UdfModelLint.Delete(db, new("p", "b", ["Lib.CycleA"]))).Code);
        Assert.Equal(5, db.Model.Functions.Count);
        var unused = UdfModelLint.Analyze(db, Snapshot(db)).Unused;
        UdfModelLint.Delete(db, new("p", "b", unused));
        Assert.Equal(["Lib.Dependency", "Lib.Used"], db.Model.Functions.Select(f => f.Name).Order());
    }

    /// <summary>真实文件分析不写 staging/备份, 取消或基线失效不删; 直接保存后权威回读且不生成长期历史备份.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupUsesExistingPbipSaveAndBaseline(bool tmdl)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-lint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = PbipFixture.Create(Path.Combine(root, "project"));
            var modelFile = Path.Combine(root, "project", "model", "model.bim");
            using (var db = Database())
            {
                db.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Root", Expression = "Lib.Used()" });
                if (tmdl)
                {
                    TOM.TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(root, "project", "model", "definition")); File.Delete(modelFile);
                    File.WriteAllText(Path.Combine(root, "project", "model", "definition.pbism"), """{"version":"4.2"}""");
                }
                else File.WriteAllText(modelFile, TOM.JsonSerializer.SerializeDatabase(db));
            }
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            var reader = new ProjectReader();
            using var edits = new ModelEditService(reader, workspace);
            var baseline = await reader.ReadAsync(entry, default); edits.Select(baseline);
            var preview = await edits.AnalyzeUdfsAsync(baseline.ProjectId, baseline.BaselineId, default);
            Assert.True(preview.Complete); Assert.Equal(3, preview.Unused.Count); Assert.False(edits.HasPendingChanges);
            Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
            ModelEditRequest request = new("udf.deleteUnused", "UDF", "items", JsonSerializer.Serialize(new UdfDeleteBatch(baseline.ProjectId, baseline.BaselineId, preview.Unused), JsonSerializerOptions.Web));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.EditAndSaveAsync(request, cancellation.Token));
            Assert.Equal(baseline.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
            Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.AnalyzeUdfsAsync("other", baseline.BaselineId, default))).Code);
            var saved = await edits.EditAndSaveAsync(request, default);
            Assert.Equal(2, saved.Model.Functions.Count);
            Assert.Equal(saved.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
            Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
            Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.EditAndSaveAsync(request, default))).Code);
            edits.Select(saved with { Connection = new("ssas-xmla", "Remote") });
            Assert.Equal("READ_ONLY_MODEL", (await Assert.ThrowsAsync<AppException>(() => edits.AnalyzeUdfsAsync(saved.ProjectId, saved.BaselineId, default))).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>lint 删除历史仅对应准确旧锁, 不复活函数; 普通外部删除继续被原保护拒绝.</summary>
    [Fact]
    public void ManagedLintDeletionPreservesLockAndDoesNotResurrectFunctions()
    {
        using var db = Database();
        var functions = db.Model.Functions.Select(f => new ResolvedUdfFunction(new(f.Name, f.Expression, "", new Dictionary<string, string>()), "local", f.Name, "1.0.0", "baseline", false)).ToArray();
        foreach (var function in db.Model.Functions) ModelConfigurationOwnership.Mark(function, function.Annotations);
        var installed = new UdfResolution(functions, []);
        ModelConfigurationUdfLocks.PersistModel(db.Model, installed);
        var originalLock = db.Model.Annotations[ModelConfigurationUdfLocks.Annotation].Value;
        UdfModelLint.Delete(db, new("p", "b", UdfModelLint.Analyze(db, Snapshot(db)).Unused));
        Assert.Equal(originalLock, db.Model.Annotations[ModelConfigurationUdfLocks.Annotation].Value);
        var remaining = ModelConfigurationUdfLocks.ExcludeLintRemoved(db.Model, ModelConfigurationUdfLocks.FromModel(db.Model));
        Assert.Empty(remaining.Functions);
        ModelConfigurationUdfLocks.PersistModel(db.Model, remaining);
        Assert.Null(db.Model.Annotations.Find("PbiUtils.Configuration.UdfLintRemoved.v1"));
        db.Model.Functions.Add(new TOM.Function { Name = "External", Expression = "() => 1" });
        ModelConfigurationUdfLocks.PersistModel(db.Model, new([new(new("External", "() => 1", "", new Dictionary<string, string>()), "local", "External", "1.0.0", "b", false)], []));
        db.Model.Functions.Remove(db.Model.Functions[0]);
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.VerifyInstalled(db.Model, ModelConfigurationUdfLocks.ExcludeLintRemoved(db.Model, ModelConfigurationUdfLocks.FromModel(db.Model)))).Code);
    }

    /// <summary>真实模型保留安装锁直到下轮精确导入; lint 删除的另一函数不会被旧模型锁复活.</summary>
    [Fact]
    public async Task ManagedFileCleanupCanImportAgainWithoutResurrection()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-lint-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = PbipFixture.Create(Path.Combine(root, "project")); var reader = new ProjectReader();
            using var runtime = new ScaffoldRuntime(Path.Combine(root, "user"));
            var one = await runtime.Local.CreateAsync(new(1, "Local.One", "1.0.0", "() => 1", "", []), default);
            var two = await runtime.Local.CreateAsync(new(1, "Local.Two", "1.0.0", "() => 2", "", []), default);
            using var compiler = new ModelConfigurationCompiler(new UdfDependencyResolver(new NoPackages(), runtime.Local));
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user")); using var edits = new ModelEditService(reader, workspace);
            var snapshot = await reader.ReadAsync(entry, default); edits.Select(snapshot);

            static PbiUtils.Application.Udf.UdfSelection Select(LocalUdfEntry item) => new("local", item.Id, item.Definition.Version, [item.Definition.Name], Baseline: item.Baseline);
            var prepared = await compiler.PrepareAsync(new(Udfs: [Select(one), Select(two)], AllowCompatibilityUpgrade: true), snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
            var modelFile = Path.Combine(root, "project", "model", "model.bim");
            /// <summary>读取当前实际保存的模型锁, 不借助已取消的磁盘旁文件.</summary>
            string ReadLock()
            {
                using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile));
                return database.Model.Annotations[ModelConfigurationUdfLocks.Annotation].Value;
            }
            var lockBefore = ReadLock();
            var preview = await edits.AnalyzeUdfsAsync(snapshot.ProjectId, snapshot.BaselineId, default);
            snapshot = await edits.EditAndSaveAsync(new("udf.deleteUnused", "UDF", "items", JsonSerializer.Serialize(new UdfDeleteBatch(snapshot.ProjectId, snapshot.BaselineId, preview.Unused), JsonSerializerOptions.Web)), default);
            Assert.Empty(snapshot.Model.Functions); Assert.Equal(lockBefore, ReadLock());
            prepared = await compiler.PrepareAsync(new(Udfs: [Select(one)]), snapshot, default);
            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default);
            Assert.Equal("Local.One", Assert.Single(snapshot.Model.Functions).Name);
            Assert.DoesNotContain("Local.Two", ReadLock(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "project", "config")));
            Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class NoPackages : IUdfPackageProvider
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不访问远端包");
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不访问远端包");
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不访问远端包");
    }

    /// <summary>建立五函数依赖夹具, 包含一个无根递归环.</summary>
    private static TOM.Database Database()
    {
        var db = new TOM.Database { Name = "Lint", CompatibilityLevel = 1702, Model = new TOM.Model() };
        db.Model.Tables.Add(new TOM.Table { Name = "T" });
        foreach (var pair in new[] { ("Lib.Used", "() => Lib.Dependency()"), ("Lib.Dependency", "() => 1"), ("Lib.CycleA", "() => Lib.CycleB()"), ("Lib.CycleB", "() => Lib.CycleA()"), ("Lib.Unused", "() => 0") })
            db.Model.Functions.Add(new TOM.Function { Name = pair.Item1, Expression = pair.Item2 });
        return db;
    }
    /// <summary>仅为纯分析测试绑定目标身份, 不替代真实完整模型.</summary>
    private static ProjectSnapshot Snapshot(TOM.Database db) => new("lint.pbip", "p", "b", "BIM", "", "", null, ModelSnapshotMapper.Map(db), [], [], []);
}
