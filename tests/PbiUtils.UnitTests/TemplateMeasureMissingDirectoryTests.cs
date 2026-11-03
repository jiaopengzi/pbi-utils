// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureMissingDirectoryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证无预建目录的 PBIP/PBIX 配置能力、首次创建和双模式完整往返.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只操作随机隔离文件和离线模型, 不读取 PBIX 二进制或连接用户 Desktop.</summary>
public sealed class TemplateMeasureMissingDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-missing-" + Guid.NewGuid().ToString("N"));
    private static readonly TemplateMeasureDefinition[] Definitions = [new("author", "new value", "Uncategorized", true, "说明", "目录")];

    /// <summary>缺少目录时只读取得 missing 修订, 普通和 pbip 布局均通过真实桥接完成 Config/TMDL/Config 往返.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDirectorySupportsConfigRoundTripThroughBridge(bool nested)
    {
        var project = Path.Combine(root, "project");
        var entry = PbipFixture.Create(nested ? Path.Combine(project, "pbip") : project);
        var directory = Path.Combine(project, "pbi-utils");
        var path = Path.Combine(directory, "config.json");
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler();
        using var bridge = new ModelConfigurationBridge(reader, workspace, compiler);
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);

        var context = await bridge.ContextAsync("current", snapshot, default);
        Assert.True(context.CanUseMeasureConfig);
        Assert.Equal("missing", context.MeasureConfigurationRevision);
        Assert.False(Directory.Exists(directory));

        foreach (var mode in new[] { "config", "tmdl", "config" })
        {
            var request = new ModelConfigurationRequest(Measures: Definitions, MeasuresMode: mode, MeasureConfigurationRevision: context.MeasureConfigurationRevision);
            var input = JsonSerializer.SerializeToElement(new { context.TargetId, context.Project.BaselineId, Section = "measures", Request = request }, JsonSerializerOptions.Web);
            context = await bridge.ApplyAsync(input, context.Project, edits, default);
            Assert.Equal(mode, context.Project.Model.TemplateMeasureConfiguration!.Mode);
            Assert.True(context.CanUseMeasureConfig);
            Assert.Equal(mode == "config", File.Exists(path));
            Assert.Equal(mode == "config", context.Project.Model.Tables.Any(table => table.Name == "PbiUtilsTemplateMeasures"));
            var measure = Assert.Single(context.Project.Model.Measures, measure => measure.Name == "author");
            Assert.Equal("说明", measure.Description);
            Assert.Equal("目录", measure.DisplayFolder);
            if (mode == "config")
            {
                Assert.Contains("FILTER", measure.Expression);
                using var database = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(project, nested ? "pbip/model/model.bim" : "model/model.bim")));
                Assert.True(database.Model.Tables["PbiUtilsTemplateMeasures"].IsHidden);
                Assert.Contains(project, database.Model.Expressions["PbiUtilsProjectRoot"].Expression);
            }
            else Assert.Equal("\n\"new value\"", measure.Expression);
        }
    }

    /// <summary>PBIX 仅作为已核实的文件身份, 任意二进制内容不参与配置读取或根解析.</summary>
    [Fact]
    public void PbixWithoutConfigurationDirectoryCanCreateConfig()
    {
        Directory.CreateDirectory(root);
        var entry = Path.Combine(root, "model.pbix");
        File.WriteAllBytes(entry, [0, 255, 1, 2]);
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };

        var context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, root, entry, _ => Task.CompletedTask);

        Assert.True(context.CanUseMeasureConfig);
        Assert.Equal("missing", context.ConfigurationRevision);
        Assert.False(Directory.Exists(Path.Combine(root, "pbi-utils")));
        Assert.Equal(new byte[] { 0, 255, 1, 2 }, File.ReadAllBytes(entry));
    }

    /// <summary>PBIX 位于名为 pbip 的普通目录时仍使用文件父目录, 不能套用 PBIP 提升规则或任意祖先根.</summary>
    [Fact]
    public void PbixInPbipDirectoryUsesItsOwnParentAndRejectsAnotherRoot()
    {
        var directory = Path.Combine(root, "pbip"); Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, "model.pbix"); File.WriteAllBytes(entry, [0, 255]);
        using var database = new TOM.Database { Name = "test", CompatibilityLevel = 1606, Model = new TOM.Model() };
        Assert.Equal(directory, TemplateMeasureConfigurationStorage.ProjectRoot(entry));
        var correct = TemplateMeasureConfigurationStorage.ReadAssetContext(database, directory, entry, _ => Task.CompletedTask);
        Assert.True(correct.CanUseMeasureConfig);
        var wrong = TemplateMeasureConfigurationStorage.ReadAssetContext(database, root, entry, _ => Task.CompletedTask);
        Assert.False(wrong.CanUseMeasureConfig);
        Assert.Equal("layout", wrong.UnavailableReason);
        Assert.False(Directory.Exists(Path.Combine(directory, "pbi-utils")));
        Assert.False(Directory.Exists(Path.Combine(root, "pbi-utils")));
    }

    /// <summary>首次 Config 在提交前取消不发布 JSON 或目录, 离线草稿可明确放弃且原模型完整基线保持.</summary>
    [Fact]
    public async Task CancellingBeforeFirstConfigCommitDoesNotCreateDirectoryOrChangeModel()
    {
        var project = Path.Combine(root, "project");
        var entry = PbipFixture.Create(project);
        var reader = new ProjectReader(); var snapshot = await reader.ReadAsync(entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler();
        using var edits = new ModelEditService(reader, workspace); edits.Select(snapshot);
        using var cancellation = new CancellationTokenSource();
        var prepared = await compiler.PrepareAsync(new(Measures: Definitions, MeasuresMode: "config", MeasureConfigurationRevision: "missing"), snapshot, default);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.EditConfigurationAndSaveAsync(prepared, cancellation.Token,
                beforeCommit: token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
            Assert.False(Directory.Exists(Path.Combine(project, "pbi-utils")));
            Assert.Equal(snapshot.BaselineId, (await reader.ReadAsync(entry, default)).BaselineId);
            Assert.True(edits.HasPendingChanges);
            Assert.Equal(snapshot.BaselineId, (await edits.DiscardAsync(default)).BaselineId);
            Assert.False(edits.HasPendingChanges);
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>首次创建冻结后外部新增 JSON 不能被旧 missing 修订覆盖, 也不提前创建配置目录.</summary>
    [Fact]
    public async Task MissingDirectoryRevisionRejectsConcurrentFileCreation()
    {
        var entry = PbipFixture.Create(root);
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var context = await compiler.ReadAssetContextAsync(snapshot, default);
        Assert.True(context.CanUseMeasureConfig);
        var prepared = await compiler.PrepareAsync(new(Measures: Definitions, MeasuresMode: "config", MeasureConfigurationRevision: "missing"), snapshot, default);
        try
        {
            Assert.Equal("config", prepared.Configuration!.Request.MeasuresMode);
            Assert.False(Directory.Exists(Path.Combine(root, "pbi-utils")));
            var path = Path.Combine(root, "pbi-utils", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"Custom\":true}");
            var error = await Assert.ThrowsAsync<PbiUtils.Core.AppException>(() => compiler.VerifyAsync(prepared, default));
            Assert.Equal("STALE_BASELINE", error.Code);
            Assert.Equal("{\"schemaVersion\":1,\"Custom\":true}", File.ReadAllText(path));
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>同名普通文件占据目录名或目录占据 JSON 名时保留原对象, 不发放 Config 权限.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationPathTypeCollisionRemainsProtected(bool jsonDirectory)
    {
        var entry = PbipFixture.Create(root);
        var directory = Path.Combine(root, "pbi-utils");
        if (jsonDirectory) Directory.CreateDirectory(Path.Combine(directory, "config.json"));
        else File.WriteAllText(directory, "user file");
        using var compiler = new ModelConfigurationCompiler();
        var context = await compiler.ReadAssetContextAsync(await new ProjectReader().ReadAsync(entry, default), default);
        Assert.False(context.CanUseMeasureConfig);
        Assert.Equal("invalid", context.UnavailableReason);
        if (jsonDirectory) Assert.True(Directory.Exists(Path.Combine(directory, "config.json")));
        else Assert.Equal("user file", File.ReadAllText(directory));
    }

    /// <summary>盘符根本身已有分隔符, 资产路径校验不重复追加分隔符而拒绝合法子路径; 不写入盘符根.</summary>
    [Fact]
    public void DriveRootAllowsContainedConfigurationPath()
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(root))!;
        Assert.Equal(Path.Combine(volume, "pbi-utils", "config.json"), ModelConfigurationCompiler.AssetPath(volume, "pbi-utils/config.json"));
    }

    /// <summary>仅清理本测试拥有的随机根, 不触碰用户工程.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
