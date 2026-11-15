// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationFilesystemTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立验证真实 PBIP 大文件保存边界与跨节连续配置.

using System.Security.Cryptography;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>实际 PBIP 操作在本用例专属目录进行, 与其他配置夹具独立调度.</summary>
[Collection(ModelConfigurationFilesystemTests.CollectionName)]
public sealed class ModelConfigurationFilesystemTests : IDisposable
{
    internal const string CollectionName = "Model configuration filesystem";
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-configuration-files-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>仅删除当前用例拥有的工程和用户目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

