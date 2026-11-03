// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldTemplateCatalogTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证嵌入模板和固定用户目录的发现、完整绑定、摘要及链接边界.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>全部用户模板只存在于测试拥有的隔离目录.</summary>
public sealed class ScaffoldTemplateCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-template-" + Guid.NewGuid().ToString("N"));
    private string Templates => Path.Combine(root, "templates", "pbip");
    private ScaffoldTemplateCatalog Catalog => new(root);

    /// <summary>发布程序集独立携带当前空白模板, 不依赖当前目录或开发素材.</summary>
    [Fact]
    public async Task BuiltinsAreEmbeddedAndHaveStableContentBaselines()
    {
        var entries = await Catalog.ListAsync(default);
        Assert.Equal(["builtin:blank"], entries.Select(e => e.Id));
        Assert.Equal(["blank"], entries.Select(e => e.Name));
        foreach (var entry in entries)
        {
            var template = await Catalog.GetAsync(entry.Id, default);
            Assert.Equal(entry.Baseline, template.Info.Baseline);
            Assert.Matches("^[a-f0-9]{64}$", entry.Baseline);
            Assert.Contains(template.Files, f => f.RelativePath == template.EntryFile);
            Assert.Contains(template.Files, f => f.RelativePath == "pbi-utils.template.json");
            Assert.DoesNotContain(template.Files, f => f.RelativePath.Contains("cache.abf", StringComparison.OrdinalIgnoreCase)
                || f.RelativePath.Contains("localSettings.json", StringComparison.OrdinalIgnoreCase) || f.RelativePath.Split('/').Contains(".git"));
            var output = Path.Combine(root, entry.Id.Split(':')[1]);
            foreach (var file in template.Files)
            {
                var target = Path.Combine(output, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, file.Content);
            }
            var snapshot = await new ProjectReader().ReadAsync(Path.Combine(output, template.EntryFile), default);
            Assert.Equal(1606, snapshot.Model.CompatibilityLevel);
            Assert.Empty(snapshot.Model.Tables);
        }
        Assert.False(Directory.Exists(Templates));
    }

    /// <summary>固定已核验的内置模板字节基线与文件数, 防止未经核验的资源变化; 说明文件不进入发布资源.</summary>
    [Theory]
    [InlineData("blank", "d758e756e52cbcfcd8d2782aea87d19dce68a4ab66a92a22b7581f9e7509679d", 16)]
    public async Task BuiltinPlaceholderSnapshotsKeepOriginalBytes(string kind, string baseline, int count)
    {
        var template = await Catalog.GetAsync("builtin:" + kind, default);
        Assert.Equal(kind, template.Info.Name);
        Assert.Equal(baseline, template.Info.Baseline);
        Assert.Equal(count, template.Files.Count);
        Assert.DoesNotContain(typeof(ScaffoldTemplateCatalog).Assembly.GetManifestResourceNames(), name => name.EndsWith("README.md", StringComparison.Ordinal));
    }

    /// <summary>稳定ID绑定目录, 内容变更通过独立基线使旧预览失效.</summary>
    [Fact]
    public async Task UserTemplateKeepsIdAndChangesBaselineWhenContentChanges()
    {
        var folder = Create("custom");
        var first = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        await File.WriteAllTextAsync(Path.Combine(folder, "readme.txt"), "changed");
        var second = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Baseline, second.Baseline);
        Assert.Equal(second.Baseline, (await Catalog.GetAsync(second.Id, default)).Info.Baseline);
        Assert.DoesNotContain(folder, JsonSerializer.Serialize(second), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>机器缓存和Git元数据不成为模板内容或摘要的一部分.</summary>
    [Fact]
    public async Task MachineCachesAndGitAreExcludedWithoutChangingBaseline()
    {
        var folder = Create("custom");
        var first = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Write(folder, ".git/config", "private");
        Write(folder, "model/.pbi/cache.abf", "cache");
        Write(folder, "model/.pbi/localSettings.json", "private");
        Write(folder, "model/.pbi/editorSettings.json", "{}");
        var second = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.NotEqual(first.Baseline, second.Baseline);
        File.Delete(Path.Combine(folder, "model/.pbi/editorSettings.json"));
        var template = await Catalog.GetAsync(first.Id, default);
        Assert.Equal(first.Baseline, template.Info.Baseline);
        Assert.DoesNotContain(template.Files, f => f.RelativePath.Split('/').Any(p => p is ".pbi" or ".git"));
    }

    /// <summary>无效用户模板逐项显示诊断, 不影响有效及内置模板.</summary>
    [Theory]
    [InlineData("missing-entry", "TEMPLATE_ENTRY_MISSING")]
    [InlineData("multiple-entry", "TEMPLATE_ENTRY_AMBIGUOUS")]
    [InlineData("nested-entry", "TEMPLATE_ENTRY_AMBIGUOUS")]
    [InlineData("missing-report", "TEMPLATE_BINDING_INVALID")]
    [InlineData("missing-model", "TEMPLATE_BINDING_INVALID")]
    [InlineData("escaping-binding", "TEMPLATE_PATH_UNSAFE")]
    [InlineData("remote-binding", "TEMPLATE_BINDING_INVALID")]
    public async Task InvalidTemplatesAreDiagnosedAndCannotBeResolved(string problem, string code)
    {
        var folder = Create("invalid");
        switch (problem)
        {
            case "missing-entry": File.Delete(Path.Combine(folder, "测试.pbip")); break;
            case "multiple-entry": File.Copy(Path.Combine(folder, "测试.pbip"), Path.Combine(folder, "other.pbip")); break;
            case "nested-entry": Write(folder, "nested/other.pbip", "{}"); break;
            case "missing-report": File.Delete(Path.Combine(folder, "report/definition.pbir")); break;
            case "missing-model": File.Delete(Path.Combine(folder, "model/definition.pbism")); break;
            case "escaping-binding": Write(folder, "report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../../outside"}}}"""); break;
            case "remote-binding": Write(folder, "report/definition.pbir", """{"version":"4.0","datasetReference":{"byConnection":{"connectionString":"private"}}}"""); break;
        }
        var entry = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.Empty(entry.Baseline);
        Assert.Contains(entry.Warnings, w => w.Contains(code, StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<AppException>(() => Catalog.GetAsync(entry.Id, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>真实目录链接在枚举前拒绝, 不跟随外部内容.</summary>
    [Fact]
    public async Task DirectoryJunctionCannotExposeExternalTemplate()
    {
        var external = Path.Combine(root, "outside");
        PbipFixture.Create(external);
        Directory.CreateDirectory(Templates);
        var link = Path.Combine(Templates, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, external },
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, external);
        try
        {
            var entry = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
            Assert.Empty(entry.Baseline);
            Assert.Contains(entry.Warnings, w => w.Contains("TEMPLATE_PATH_UNSAFE", StringComparison.Ordinal));
            Assert.Equal("TEMPLATE_PATH_UNSAFE", (await Assert.ThrowsAsync<AppException>(() => Catalog.GetAsync(entry.Id, default))).Code);
        }
        finally { Directory.Delete(link); }
        Assert.True(File.Exists(Path.Combine(external, "测试.pbip")));
    }

    /// <summary>取消不触发磁盘发现, 任意路径不能充当模板ID.</summary>
    [Fact]
    public async Task CancellationAndUnknownIdsFailClosed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog.ListAsync(cancellation.Token));
        Assert.Equal("TEMPLATE_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => Catalog.GetAsync("../../outside", default))).Code);
    }

    /// <summary>描述文件必须显式声明受支持版本, 未知字段和重复字段不能悄悄生效.</summary>
    [Theory]
    [InlineData("{\"schemaVersion\":2}", "TEMPLATE_MANIFEST_INVALID")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":true}", "TEMPLATE_MANIFEST_INVALID")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", "TEMPLATE_BINDING_INVALID")]
    [InlineData("{\"schemaVersion\":1,\"name\":42}", "TEMPLATE_MANIFEST_INVALID")]
    public async Task DescriptorRejectsUnknownSchemaFieldsAndDuplicateProperties(string json, string code)
    {
        Write(Create("descriptor"), "pbi-utils.template.json", json);
        var entry = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.Empty(entry.Baseline);
        Assert.Contains(code, entry.Warnings);
        Assert.Equal(code, (await Assert.ThrowsAsync<AppException>(() => Catalog.GetAsync(entry.Id, default))).Code);
    }

    /// <summary>描述名称不能覆盖目录名称或伪造内置模板冲突, 描述变更仍更新内容基线.</summary>
    [Fact]
    public async Task DescriptorCannotOverrideFolderNameOrHostBoundId()
    {
        var folder = Create("descriptor");
        var before = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Write(folder, "pbi-utils.template.json", """{"schemaVersion":1,"name":"blank","description":"只读外部依赖"}""");
        var after = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("descriptor", after.Name);
        Assert.Equal("只读外部依赖", after.Description);
        Assert.NotEqual(before.Baseline, after.Baseline);
        Assert.NotEmpty(after.Baseline);
        Assert.Equal("descriptor", (await Catalog.GetAsync(after.Id, default)).Info.Name);
    }

    /// <summary>忽略大小写的内置同名目录保持独立ID并拒绝选择, 不替换内置模板.</summary>
    [Theory]
    [InlineData("blank", "builtin:blank")]
    [InlineData("BLANK", "builtin:blank")]
    public async Task UserFolderConflictingWithBuiltinIsDiagnosedAndCannotBeResolved(string name, string builtinId)
    {
        var folder = Create(name);
        Write(folder, "pbi-utils.template.json", """{"schemaVersion":1,"name":"not-a-conflict"}""");
        var entries = await Catalog.ListAsync(default);
        var user = entries.Single(e => e.Source == "user");
        var builtin = entries.Single(e => e.Id == builtinId);
        Assert.Empty(user.Baseline);
        Assert.Contains("TEMPLATE_NAME_CONFLICT", user.Warnings);
        Assert.Equal(name, user.Name);
        Assert.NotEqual(builtin.Id, user.Id);
        Assert.NotEmpty(builtin.Baseline);
        Assert.Equal(builtin.Baseline, (await Catalog.GetAsync(builtin.Id, default)).Info.Baseline);
        Assert.Equal(user.Id, (await Catalog.ListAsync(default)).Single(e => e.Source == "user").Id);
        var error = await Assert.ThrowsAsync<AppException>(() => Catalog.GetAsync(user.Id, default));
        Assert.Equal("TEMPLATE_NAME_CONFLICT", error.Code);
        Assert.DoesNotContain(folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前未被内置目录占用的普通名称可作为用户模板, 不继承历史保留名.</summary>
    [Theory]
    [InlineData("page")]
    [InlineData("retail")]
    public async Task NonBuiltinNamesAreAvailableForUserTemplates(string name)
    {
        Create(name);
        var entries = await Catalog.ListAsync(default);
        var user = Assert.Single(entries, entry => entry.Source == "user");
        Assert.Equal(name, user.Name);
        Assert.NotEmpty(user.Baseline);
        Assert.DoesNotContain("TEMPLATE_NAME_CONFLICT", user.Warnings);
        Assert.Equal(user.Baseline, (await Catalog.GetAsync(user.Id, default)).Info.Baseline);
        Assert.Equal("builtin:blank", Assert.Single(entries, entry => entry.Source == "builtin").Id);
    }

    /// <summary>在读取超大文件内容前拒绝, 不放宽模型模板的边界.</summary>
    [Fact]
    public async Task OversizedSourceIsDiagnosedBeforeReadingWholeFile()
    {
        var folder = Create("large");
        using (var stream = File.Create(Path.Combine(folder, "oversized.bin"))) stream.SetLength(32L * 1024 * 1024 + 1);
        var entry = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.Empty(entry.Baseline);
        Assert.Contains("TEMPLATE_LIMIT_EXCEEDED", entry.Warnings);
    }

    /// <summary>模板的未应用查询警告必须保留, 不能把结构可读提升为完整刷新通过.</summary>
    [Fact]
    public async Task SharedReaderWarningsRemainVisibleInUserTemplate()
    {
        Write(Create("unapplied"), "model/.pbi/unappliedChanges.json", "{}");
        var entry = (await Catalog.ListAsync(default)).Single(e => e.Source == "user");
        Assert.NotEmpty(entry.Baseline);
        Assert.Contains(entry.Warnings, w => w.Contains("未应用", StringComparison.Ordinal));
        Assert.Contains("TEMPLATE_EXTERNAL_SOURCES_UNVERIFIED", entry.Warnings);
    }

    /// <summary>在固定目录内创建有效完整夹具.</summary>
    private string Create(string name)
    {
        var folder = Path.Combine(Templates, name);
        PbipFixture.Create(folder);
        return folder;
    }
    /// <summary>写入测试拥有的相对文件.</summary>
    private static void Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    /// <summary>只清理本类创建的测试目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
