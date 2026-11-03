// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpOutcomeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实脚手架故障注入验证 MCP 部分结果、已知失败和可靠回执阻塞, 不访问用户工程.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Execution;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Cli.Mcp;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>使用真实模板、文件事务和账本; 仅在精确阶段注入文件锁或 Git 初始化结果.</summary>
public sealed class McpOutcomeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-outcomes-" + Guid.NewGuid().ToString("N"));
    private string UserRoot => Path.Combine(root, ".pbi-utils");

    /// <summary>准备独占输出父目录, 不触碰实际应用用户根.</summary>
    public McpOutcomeTests() => Directory.CreateDirectory(root);

    /// <summary>工程完整创建但 Git 确定失败时保留已创建事实, 以 failed 结束且后续新写不被误阻塞.</summary>
    [Fact]
    public async Task KnownGitFailureReturnsErrorAndPreservesCreatedProjectWithoutUnknownCommit()
    {
        var (prepared, request, plan) = await PrepareAsync((_, _) => Task.FromResult(false));
        using var workspace = prepared;
        var journal = new ExecutionJournal(UserRoot);
        using (var execution = Start(journal))
        {
            var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default, execution.CommitStarted);
            var response = McpBusinessRuntime.CompleteResult(result, execution);
            Assert.True(response.IsError);
            Assert.Equal("GIT_INIT_FAILED", response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
            Assert.False(response.StructuredContent.Value.GetProperty("needsVerification").GetBoolean());
            Assert.True(response.StructuredContent.Value.GetProperty("result").GetProperty("projectCreated").GetBoolean());
            Assert.True(response.StructuredContent.Value.GetProperty("result").GetProperty("verified").GetBoolean());
            Assert.True(File.Exists(result.ProjectPath));
        }
        var record = Assert.Single(journal.List()).Record;
        Assert.Equal("failed", record.State);
        Assert.Equal("GIT_INIT_FAILED", record.ErrorCode);
        Assert.True(record.CommitStarted);
        journal.RequireResetSafe();
        using var next = Start(journal);
        next.Finish(false, "CANCELLED");
    }

    /// <summary>Git 成功或确定失败后若脚手架回执无法替换, 回执待核验必须优先且阻止后续写入和重置.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScaffoldReceiptFailureRemainsPendingEvenWhenGitAlsoFails(bool gitSucceeded)
    {
        FileStream? lockedReceipt = null;
        string? receiptPath = null;
        var (prepared, request, plan) = await PrepareAsync((_, _) =>
        {
            lockedReceipt = new FileStream(receiptPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(gitSucceeded);
        });
        using var workspace = prepared;
        receiptPath = Path.Combine(UserRoot, "scaffold", "plans", plan.Digest + ".json");
        var journal = new ExecutionJournal(UserRoot);
        try
        {
            using var execution = Start(journal);
            var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default, execution.CommitStarted);
            Assert.Equal("SCAFFOLD_RECEIPT_PENDING", result.ErrorCode);
            Assert.True(result.ProjectCreated);
            Assert.True(result.Verified);
            Assert.Equal(gitSucceeded, result.GitInitialized);
            if (!gitSucceeded) Assert.Contains("GIT_INIT_FAILED", result.Warnings);
            Assert.True(McpBusinessRuntime.CompleteResult(result, execution).IsError);
        }
        finally { lockedReceipt?.Dispose(); }
        await AssertPendingAsync(journal, "SCAFFOLD_RECEIPT_PENDING");
        Assert.True(File.Exists(Path.Combine(plan.OutputPath, plan.EntryFile)));
    }

    /// <summary>提交前验证通过后外部改变实际入口, 发布后完整清单回读失败; 原工程和待核验状态必须保留.</summary>
    [Fact]
    public async Task PublishedProjectWithChangedDefinitionIsNotReportedAsSuccess()
    {
        var (prepared, request, plan) = await PrepareAsync();
        using var workspace = prepared;
        var journal = new ExecutionJournal(UserRoot);
        using (var execution = Start(journal))
        {
            var result = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default, () =>
            {
                execution.CommitStarted();
                var stagedEntry = Path.Combine(root, ".pbi-utils-scaffold-" + plan.Id, plan.EntryFile);
                File.AppendAllText(stagedEntry, "\n ");
            });
            Assert.True(result.ProjectCreated);
            Assert.False(result.Verified);
            Assert.Equal("SCAFFOLD_VERIFICATION_PENDING", result.ErrorCode);
            var response = McpBusinessRuntime.CompleteResult(result, execution);
            Assert.True(response.IsError);
            Assert.True(response.StructuredContent!.Value.GetProperty("result").GetProperty("projectCreated").GetBoolean());
            Assert.Equal("SCAFFOLD_VERIFICATION_PENDING", response.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
            Assert.True(response.StructuredContent.Value.GetProperty("needsVerification").GetBoolean());
        }
        await AssertPendingAsync(journal, "SCAFFOLD_VERIFICATION_PENDING");
        Assert.True(File.Exists(Path.Combine(plan.OutputPath, plan.EntryFile)));
    }

    /// <summary>当前客户端已预览的计划恢复既有提交时不重放 Git, 即使本次没有新的提交回调也必须持久阻止新写.</summary>
    [Fact]
    public async Task RecoveredUnknownGitStateBlocksWritesWithoutASecondCommit()
    {
        var calls = 0;
        var (prepared, request, plan) = await PrepareAsync((_, _) => { calls++; return Task.FromResult(false); });
        using var workspace = prepared;
        // 故障工作区只负责模拟既有提交; 本次恢复必须先由同一 MCP 上下文真实预览, 不借用其他客户端计划.
        Assert.True(await workspace.DiscardAsync(plan.Digest, default));
        await using var runtime = new McpBusinessRuntime(true, UserRoot, NullLoggerFactory.Instance);
        var preview = await runtime.CallAsync(new CallToolRequestParams
        {
            Name = "scaffold_plan",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["request"] = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
                ["output"] = JsonSerializer.SerializeToElement(plan.OutputPath)
            }
        }, default);
        Assert.False(preview.IsError, preview.StructuredContent.ToString());
        plan = preview.StructuredContent!.Value.Deserialize<ScaffoldPlan>(JsonSerializerOptions.Web)!;
        var first = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        Assert.True(first.Verified);
        var receiptPath = Path.Combine(UserRoot, "scaffold", "plans", plan.Digest + ".json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        receipt["state"] = "committing";
        receipt["result"] = null;
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        var journal = new ExecutionJournal(UserRoot);
        var response = await runtime.CallAsync(new CallToolRequestParams
        {
            Name = "scaffold_create",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["approve"] = JsonSerializer.SerializeToElement(plan.Digest),
                ["request"] = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
                ["output"] = JsonSerializer.SerializeToElement(plan.OutputPath)
            }
        }, default);
        Assert.True(response.IsError);
        Assert.Equal("GIT_INIT_NEEDS_VERIFICATION", response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.True(response.StructuredContent.Value.GetProperty("needsVerification").GetBoolean());
        Assert.False(Assert.Single(journal.List()).Record.CommitStarted);
        Assert.Equal(1, calls);
        await AssertPendingAsync(journal, "GIT_INIT_NEEDS_VERIFICATION");
    }

    /// <summary>MCP 自身回执持久化失败仍返回已创建业务事实, 保留 commit_started 供重启后核验.</summary>
    [Fact]
    public async Task JournalFailureDoesNotLoseSuccessfulProjectFacts()
    {
        var (prepared, request, plan) = await PrepareAsync();
        using var workspace = prepared;
        var journal = new ExecutionJournal(UserRoot);
        var executionId = Guid.NewGuid().ToString("N");
        using (var execution = journal.Prepare(executionId, "scaffold_create", JsonSerializer.SerializeToElement(new { }), "local-files"))
        {
            var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default, execution.CommitStarted);
            using var block = new FileStream(Path.Combine(UserRoot, "mcp", "executions", executionId + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var response = McpBusinessRuntime.CompleteResult(created, execution);
            Assert.True(response.IsError);
            Assert.True(response.StructuredContent!.Value.GetProperty("result").GetProperty("projectCreated").GetBoolean());
            Assert.Equal("RECEIPT_SAVE_FAILED", response.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal("commit_started", Assert.Single(journal.List()).Record.State);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", Assert.Throws<AppException>(journal.RequireResetSafe).Code);
    }

    /// <summary>Desktop 通过类型化元数据传回部分结果; CLI 不丢弃原值或把 IPC 通信成功当业务成功.</summary>
    [Theory]
    [InlineData("CONTROLLED_PARTIAL", true)]
    [InlineData("RECEIPT_SAVE_FAILED", true)]
    [InlineData("GIT_INIT_FAILED", false)]
    public void DesktopPartialResultPreservesOriginalFactsAndErrorMarker(string errorCode, bool needsVerification)
    {
        var value = JsonSerializer.SerializeToElement(new { projectId = "controlled", saveResult = new { modelSaved = true, fileSaved = false, desktopSynchronized = true, errorCode = (string?)null } });
        var response = McpBusinessRuntime.CompleteResult(new McpDesktopResult(value, true, errorCode, needsVerification), null);
        Assert.True(response.IsError);
        var structured = response.StructuredContent!.Value;
        Assert.Equal(value.GetRawText(), structured.GetProperty("result").GetRawText());
        Assert.Equal(errorCode, structured.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(needsVerification, structured.GetProperty("needsVerification").GetBoolean());
        Assert.Equal(structured.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
    }

    /// <summary>Desktop 已确定成功仍返回原业务对象, 不增加失败信封或改变保存分项.</summary>
    [Fact]
    public void DesktopSuccessKeepsOriginalResultShape()
    {
        var value = JsonSerializer.SerializeToElement(new { modelSaved = true, fileSaved = false, desktopSynchronized = true });
        var response = McpBusinessRuntime.CompleteResult(new McpDesktopResult(value, false, null, false), null);
        Assert.False(response.IsError);
        Assert.Equal(value.GetRawText(), response.StructuredContent!.Value.GetRawText());
    }

    /// <summary>真实离线快照的部分保存必须失败, 已同步 Desktop 的未保存报表文件不误判为失败.</summary>
    [Fact]
    public async Task ModelSaveClassificationKeepsTargetSpecificCompletionRules()
    {
        var (prepared, request, plan) = await PrepareAsync();
        using var workspace = prepared;
        var created = await workspace.CreateAsync(plan.Digest, request, plan.OutputPath, null, default);
        var project = await new ProjectReader().ReadAsync(created.ProjectPath, default);
        var partial = project with { SaveResult = new(true, false, true, "controlled") };
        Assert.True(BusinessOutcome.From(partial).NeedsVerification);
        Assert.True(McpBusinessRuntime.CompleteResult(partial, null).IsError);
        var desktop = partial with { Connection = new("desktop", "controlled") };
        Assert.False(BusinessOutcome.From(desktop).IsError);
        Assert.False(McpBusinessRuntime.CompleteResult(desktop, null).IsError);
        Assert.True(BusinessOutcome.From(desktop with { HasPendingChanges = true }).NeedsVerification);
    }

    /// <summary>冻结独占目录中的真实内置空模板, Git 测试仅替换初始化器.</summary>
    private async Task<(ScaffoldWorkspace Workspace, ScaffoldRequest Request, ScaffoldPlan Plan)> PrepareAsync(Func<string, CancellationToken, Task<bool>>? git = null)
    {
        var catalog = new ScaffoldTemplateCatalog(UserRoot);
        var template = await catalog.GetAsync("builtin:blank", default);
        var request = new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "OutcomeProject", "zh-cn", null, null, null, null, null, GitInitialize: git is not null);
        var workspace = new ScaffoldWorkspace(UserRoot, catalog, new ScaffoldModelWriter(), initializeGit: git);
        try { return (workspace, request, await workspace.PreviewAsync(request, Path.Combine(root, request.ProjectName), null, default)); }
        catch { workspace.Dispose(); throw; }
    }

    /// <summary>只创建测试专属根中的一次文件执行, 无模型参数或用户内容.</summary>
    private static ExecutionJournal.Execution Start(ExecutionJournal journal)
        => journal.Prepare(Guid.NewGuid().ToString("N"), "scaffold_create", JsonSerializer.SerializeToElement(new { }), "local-files");

    /// <summary>重启实例依然拒绝新写和完整数据重置, 且在首次删除前保留普通用户数据.</summary>
    private async Task AssertPendingAsync(ExecutionJournal journal, string errorCode)
    {
        var record = Assert.Single(journal.List()).Record;
        Assert.Equal("needs_verification", record.State);
        Assert.Equal(errorCode, record.ErrorCode);
        var restarted = new ExecutionJournal(UserRoot);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", Assert.Throws<AppException>(() => Start(restarted)).Code);
        var preserved = Path.Combine(UserRoot, "preserved.txt");
        await File.WriteAllTextAsync(preserved, "untouched");
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", Assert.Throws<AppException>(restarted.RequireResetSafe).Code);
        Assert.Equal("untouched", await File.ReadAllTextAsync(preserved));
    }

    /// <summary>只释放本测试创建且所有文件锁已结束的随机临时根.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
