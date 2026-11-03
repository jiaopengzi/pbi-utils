// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldGitReceiptTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 PBIP Git 部分初始化和未知提交恢复回执的幂等读取及元数据范围.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用真实模板和 composer, 仅 Git 初始化由精确计数的本地回调替代, 不运行真实 Git.</summary>
public sealed class ScaffoldGitReceiptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-git-receipt-" + Guid.NewGuid().ToString("N"));
    private string UserRoot => Path.Combine(root, "user");

    /// <summary>初始化创建元数据后报告失败, 发布回执再次读取仍保留失败事实且不重放初始化.</summary>
    [Fact]
    public async Task PartialGitInitializationUsesPbipDirectoryAndPublishedReplayIsIdempotent()
    {
        var calls = 0;
        string? actualDirectory = null;
        var (preparedWorkspace, request, plan) = await PrepareAsync((directory, _) =>
        {
            calls++;
            actualDirectory = directory;
            WriteGitMetadata(directory);
            return Task.FromResult(false);
        });
        using var workspace = preparedWorkspace;
        var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.Equal(Path.Combine(plan.OutputPath, "pbip"), actualDirectory);
        Assert.True(created.ProjectCreated);
        Assert.True(created.Verified);
        Assert.False(created.GitInitialized);
        Assert.Equal("GIT_INIT_FAILED", created.ErrorCode);
        var gitHead = Path.Combine(plan.OutputPath, "pbip", ".git", "HEAD");
        Assert.True(File.Exists(gitHead));
        Assert.False(Directory.Exists(Path.Combine(plan.OutputPath, ".git")));
        var before = await File.ReadAllBytesAsync(gitHead);
        var replay = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        AssertSameReceipt(created, replay);
        Assert.Equal(1, calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(gitHead));
    }

    /// <summary>已发布目录进入未知提交恢复时只核验 PBIP 元数据, 后续 published 回执仍可读取且不重放 Git.</summary>
    [Fact]
    public async Task CommittingReceiptWithPbipGitRecoversAndReplaysWithoutInitialization()
    {
        var calls = 0;
        var (preparedWorkspace, request, plan) = await PrepareAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(false);
        });
        using var workspace = preparedWorkspace;
        var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(created.ProjectCreated);
        Assert.True(created.Verified);
        WriteGitMetadata(Path.Combine(plan.OutputPath, "pbip"));
        var store = new ScaffoldPlanStore(UserRoot);
        using (store.Lease(plan.Digest))
        {
            var receipt = store.Read(plan.Digest);
            store.Write(receipt with { State = "committing", Result = null });
        }
        var recovered = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(recovered.ProjectCreated);
        Assert.True(recovered.Verified);
        Assert.False(recovered.GitInitialized);
        Assert.Equal("GIT_INIT_NEEDS_VERIFICATION", recovered.ErrorCode);
        Assert.Equal(created.PlanId, recovered.PlanId);
        Assert.Equal(created.ProjectPath, recovered.ProjectPath);
        var replay = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        AssertSameReceipt(recovered, replay);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(plan.OutputPath, "pbip", ".git", "HEAD")));
    }

    /// <summary>显式 Git 请求仅允许固定 PBIP 仓库, 根目录和更深目录新增 Git 元数据仍使已发布清单失效.</summary>
    /// <param name="relative">本轮发布后额外注入的非法 Git 元数据相对位置.</param>
    [Theory]
    [InlineData(".git")]
    [InlineData("pbip/nested/.git")]
    public async Task PublishedReceiptRejectsGitOutsideFixedPbipLocation(string relative)
    {
        var calls = 0;
        var (preparedWorkspace, request, plan) = await PrepareAsync((directory, _) =>
        {
            calls++;
            WriteGitMetadata(directory);
            return Task.FromResult(true);
        });
        using var workspace = preparedWorkspace;
        var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(created.ProjectCreated);
        Assert.True(created.Verified);
        Assert.True(created.GitInitialized);
        var injected = Path.Combine(plan.OutputPath, relative);
        Directory.CreateDirectory(injected);
        await File.WriteAllTextAsync(Path.Combine(injected, "config"), "unapproved metadata");
        var error = await Assert.ThrowsAsync<AppException>(() => workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default));
        Assert.Equal("SCAFFOLD_FILES_CHANGED", error.Code);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(created.ProjectPath));
        Assert.Equal("unapproved metadata", await File.ReadAllTextAsync(Path.Combine(injected, "config")));
    }

    /// <summary>在独占临时目录冻结真实内置工程, 只替换 Git 初始化回调而保留工作区实际事务.</summary>
    /// <param name="initializeGit">只访问传入本地目录的测试初始化器.</param>
    /// <returns>由调用方释放的工作区及已冻结请求和计划.</returns>
    /// <exception cref="AppException">模板, 组合或清单冻结失败, 不返回半成品测试状态.</exception>
    private async Task<(ScaffoldWorkspace Workspace, ScaffoldRequest Request, ScaffoldPlan Plan)> PrepareAsync(Func<string, CancellationToken, Task<bool>> initializeGit)
    {
        Directory.CreateDirectory(root);
        var catalog = new ScaffoldTemplateCatalog(UserRoot);
        var template = await catalog.GetAsync("builtin:blank", default);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "ReceiptProject", "zh-cn", null, null, null, null, null, GitInitialize: true);
        var workspace = new ScaffoldWorkspace(UserRoot, catalog, new ScaffoldModelWriter(), initializeGit: initializeGit);
        try
        {
            var plan = await workspace.PreviewAsync(request, Path.Combine(root, request.ProjectName), null, default);
            return (workspace, request, plan);
        }
        catch { workspace.Dispose(); throw; }
    }

    /// <summary>仅在初始化器收到的测试目录写入最小普通 Git 元数据, 不执行命令或修改用户配置.</summary>
    /// <param name="directory">工作区传给初始化器的具体仓库目录.</param>
    private static void WriteGitMetadata(string directory)
    {
        var git = Path.Combine(directory, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
    }

    /// <summary>回执跨磁盘反序列化后比较业务字段和警告内容, 不依赖列表对象引用相等.</summary>
    /// <param name="expected">前一轮实际返回的发布或恢复回执.</param>
    /// <param name="observed">再次读取同一持久计划的回执.</param>
    private static void AssertSameReceipt(ScaffoldResult expected, ScaffoldResult observed)
    {
        Assert.Equal(expected.PlanId, observed.PlanId);
        Assert.Equal(expected.State, observed.State);
        Assert.Equal(expected.ProjectCreated, observed.ProjectCreated);
        Assert.Equal(expected.Verified, observed.Verified);
        Assert.Equal(expected.GitInitialized, observed.GitInitialized);
        Assert.Equal(expected.ProjectPath, observed.ProjectPath);
        Assert.Equal(expected.BaselineId, observed.BaselineId);
        Assert.Equal(expected.ErrorCode, observed.ErrorCode);
        Assert.Equal(expected.Warnings.ToArray(), observed.Warnings.ToArray());
    }

    /// <summary>只清理本测试创建的唯一临时树, 不触碰现有仓库或用户工作文件.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
