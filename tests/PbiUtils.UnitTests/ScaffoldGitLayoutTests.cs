// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldGitLayoutTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证新建工程仅在 PBIP 目录初始化 Git, 固定忽略规则独立于初始化开关.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅操作本测试随机根和新建工作区, 不修改仓库或用户模板的忽略规则.</summary>
public sealed class ScaffoldGitLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-git-layout-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实模板无论开关状态均冻结忽略文件, 关闭时不探测或初始化 Git.</summary>
    /// <param name="kind">实际内置模板来源.</param>
    /// <param name="initialize">本次是否显式请求初始化.</param>
    [Theory]
    [InlineData("blank", false)]
    [InlineData("blank", true)]
    public async Task AlwaysFreezesDesktopIgnoreRules(string kind, bool initialize)
    {
        Directory.CreateDirectory(root);
        var catalog = new ScaffoldTemplateCatalog(Path.Combine(root, "user"));
        var template = await catalog.GetAsync("builtin:" + kind, default);
        var probes = 0;
        var initializations = 0;
        using var workspace = new ScaffoldWorkspace(Path.Combine(root, "user"), catalog, new ScaffoldModelWriter(),
            initializeGit: (_, _) => { initializations++; return Task.FromResult(false); },
            probeGit: _ => { probes++; return Task.FromResult(new GitCapability(true)); });
        var request = Request(template) with { GitInitialize = initialize };
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "Project"), null, default);
        Assert.Equal(1, plan.Files.Count(file => file.Path == "pbip/.gitignore"));
        var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.Equal("**/.pbi/localSettings.json\n**/.pbi/cache.abf\n", await File.ReadAllTextAsync(Path.Combine(plan.OutputPath, "pbip", ".gitignore")));
        Assert.False(File.Exists(Path.Combine(plan.OutputPath, ".gitignore")));
        Assert.Equal(initialize ? 2 : 0, probes);
        Assert.Equal(initialize ? 1 : 0, initializations);
        Assert.True(result.Verified);
    }

    /// <summary>真实 Git 仓库只包含 PBIP 子树, 重复请求不改变已冻结文件或再次初始化.</summary>
    [Fact]
    public async Task InitializesRealRepositoryOnlyInsidePbipAndReplaysReceipt()
    {
        Directory.CreateDirectory(root);
        var catalog = new ScaffoldTemplateCatalog(Path.Combine(root, "user"));
        var template = await catalog.GetAsync("builtin:blank", default);
        using var workspace = new ScaffoldWorkspace(Path.Combine(root, "user"), catalog, new ScaffoldModelWriter());
        var request = Request(template) with { GitInitialize = true };
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "Project"), null, default);
        var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(result.GitInitialized);
        Assert.True(File.Exists(Path.Combine(plan.OutputPath, "pbip", ".git", "HEAD")));
        Assert.False(Directory.Exists(Path.Combine(plan.OutputPath, ".git")));
        Assert.False(File.Exists(Path.Combine(plan.OutputPath, "pbip", ".git", "index")));
        var replay = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.Equal(result.PlanId, replay.PlanId);
        Assert.Equal(result.ProjectPath, replay.ProjectPath);
        Assert.True(replay.GitInitialized);
        await File.AppendAllTextAsync(Path.Combine(plan.OutputPath, "pbip", ".gitignore"), "changed/\n");
        var stale = await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default));
        Assert.Equal("SCAFFOLD_FILES_CHANGED", stale.Code);
    }

    /// <summary>模板既有规则和原始字节前缀保持, 只在输出补齐缺失的两条规则且不重复.</summary>
    /// <param name="existing">模板拥有的忽略内容, 包括不同换行和末行无换行.</param>
    [Theory]
    [InlineData("custom/\r\n**/.pbi/localSettings.json\r\n")]
    [InlineData("custom/")]
    [InlineData("**/.pbi/localSettings.json\n**/.pbi/cache.abf\n")]
    public async Task PreservesTemplateIgnoreRulesAndAddsOnlyMissingPatterns(string existing)
    {
        var builtin = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var bytes = Encoding.UTF8.GetBytes(existing);
        var template = builtin with { Files = [.. builtin.Files, new(".gitignore", bytes)] };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template), stage, Path.Combine(root, "Project"), null, default);
        var actual = await File.ReadAllBytesAsync(Path.Combine(stage, "pbip", ".gitignore"));
        Assert.Equal(bytes, actual[..bytes.Length]);
        var lines = Encoding.UTF8.GetString(actual).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Single(lines, line => line == "**/.pbi/localSettings.json");
        Assert.Single(lines, line => line == "**/.pbi/cache.abf");
        Assert.Equal(Encoding.UTF8.GetBytes(existing), template.Files.Single(file => file.RelativePath == ".gitignore").Content);
        Assert.False(File.Exists(Path.Combine(stage, ".gitignore")));
    }

    /// <summary>预览后父级出现仓库时提交前拒绝, 不把已冻结工程初始化为嵌套仓库.</summary>
    [Fact]
    public async Task RejectsGitAncestorCreatedAfterPreview()
    {
        Directory.CreateDirectory(root);
        var catalog = new ScaffoldTemplateCatalog(Path.Combine(root, "user"));
        var template = await catalog.GetAsync("builtin:blank", default);
        var initialized = false;
        using var workspace = new ScaffoldWorkspace(Path.Combine(root, "user"), catalog, new ScaffoldModelWriter(),
            initializeGit: (_, _) => { initialized = true; return Task.FromResult(true); });
        var request = Request(template) with { GitInitialize = true };
        var plan = await workspace.PreviewAsync(request, Path.Combine(root, "Project"), null, default);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var error = await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default));
        Assert.Equal("GIT_NESTED_REPOSITORY", error.Code);
        Assert.False(initialized);
        Assert.False(Directory.Exists(plan.OutputPath));
    }

    /// <summary>构造仅打开可选 Git 开关的合法请求.</summary>
    private static ScaffoldRequest Request(ResolvedPbipTemplate template) => new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", null, null, null, null, null);

    /// <summary>仅清理本测试独占随机目录, 包括测试创建的空 Git 元数据.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
