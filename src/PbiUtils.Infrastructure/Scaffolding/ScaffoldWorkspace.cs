// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldWorkspace.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 隔离准备并持久冻结 PBIP 清单, 精确批准后同卷发布, 保留提交后的真实事实.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Application.Input;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>组合结果不表示引擎执行成功, 表统计来自实际文件.</summary>
public sealed record ScaffoldComposition(string EntryFile, IReadOnlyList<ScaffoldTableResult> Tables, IReadOnlyList<string> Warnings, int CompatibilityLevel,
    IReadOnlyList<ScaffoldUdfPreview>? UdfFunctions = null);
/// <summary>只向调用方拥有的 staging 组合文件, 不直接发布或修改源模板.</summary>
public interface IScaffoldProjectComposer
{
    /// <summary>输入内容已冻结, 最终根参数必须指向 output 而非 staging.</summary>
    Task<ScaffoldComposition> ComposeAsync(ResolvedPbipTemplate template, ScaffoldRequest request, string staging, string output,
        IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>跨 CLI 进程共享计划, 所有同步磁盘检查在后台进行.</summary>
public sealed class ScaffoldWorkspace(string userRoot, IPbipTemplateCatalog catalog, IScaffoldProjectComposer composer,
    TimeProvider? timeProvider = null, long maximumBytes = 8L * 1024 * 1024 * 1024,
    Func<string, CancellationToken, Task<bool>>? initializeGit = null, ILocalUdfStore? localUdfs = null, IUdfSourceRegistry? udfSources = null,
    Func<CancellationToken, Task<GitCapability>>? probeGit = null) : IScaffoldWorkspace
{
    private readonly ScaffoldWorkspaceFiles workspaceFiles = new(maximumBytes);
    private readonly ScaffoldPlanStore store = new(userRoot);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ProjectReader reader = new();
    private readonly SemaphoreSlim preparation = new(1, 1);
    private string? activeDigest;
    private bool disposed;

    /// <summary>后台读取模板, 组合真实产物并冻结摘要, 取消则删除未提交 staging.</summary>
    public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await preparation.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ScaffoldValidation.Validate(request);
                if (activeDigest is { } previous)
                {
                    await DiscardAsync(previous, cancellationToken);
                    activeDigest = null;
                }
                var cleanupPending = SweepExpired(cancellationToken);
                var target = ScaffoldWorkspaceFiles.Output(output, request.ProjectName);
                if (Directory.Exists(target) || File.Exists(target)) throw Conflict();
                var parent = Path.GetDirectoryName(target)!;
                if (!Directory.Exists(parent)) throw new AppException("OUTPUT_PARENT_MISSING", "请选择已存在的输出父目录", 3);
                UserDataStore.Check(parent);
                if (request.GitInitialize && ScaffoldWorkspaceFiles.HasGitAncestor(parent)) throw new AppException("GIT_NESTED_REPOSITORY", "输出位于已有 Git 仓库内, 请关闭 Git 初始化或选择其他目录", 3);
                // 显式请求保持不变; 能力失败作为准备事实进入摘要, 不静默关闭 Git.
                var gitCapability = request.GitInitialize ? await ProbeGitAsync(cancellationToken) : null;
                var template = await catalog.GetAsync(request.TemplateId, cancellationToken);
                if (template.Info.Baseline != request.TemplateBaseline) throw Stale();
                var id = Guid.NewGuid().ToString("N");
                var staging = ScaffoldWorkspaceFiles.Stage(target, id);
                UserDataStore.Check(staging);
                Directory.CreateDirectory(staging);
                try
                {
                    Report(progress, new(1, "scaffold.preparing"));
                    var composition = await composer.ComposeAsync(template, request, staging, target, progress, cancellationToken);
                    var entry = ScaffoldWorkspaceFiles.Relative(composition.EntryFile);
                    _ = await reader.ReadAsync(Path.Combine(staging, entry), cancellationToken);
                    var files = workspaceFiles.Inventory(staging, cancellationToken);
                    var current = await catalog.GetAsync(request.TemplateId, cancellationToken);
                    if (current.Info.Baseline != request.TemplateBaseline) throw Stale();
                    var source = template.Files.ToDictionary(file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
                    var changes = files.Select(file => ScaffoldWorkspaceFiles.PreviewText(file, source.GetValueOrDefault(file.Path), staging)).ToArray();
                    List<string> warnings = [.. composition.Warnings];
                    if (cleanupPending) warnings.Add("SCAFFOLD_CLEANUP_PENDING");
                    if (gitCapability is { Available: false })
                    {
                        warnings.Add(GitReason(gitCapability));
                        warnings.Add("准备时 Git 不可用, 请安装或检查 Git; 本次仍保留显式 Git 初始化请求");
                    }
                    var plan = new ScaffoldPlan(id, "", clock.GetUtcNow().AddMinutes(15), target, request, entry, changes,
                        composition.Tables, warnings,
                        composition.CompatibilityLevel, composition.UdfFunctions);
                    plan = plan with { Digest = ScaffoldPlanStore.Digest(plan) };
                    cancellationToken.ThrowIfCancellationRequested();
                    using (store.Lease(plan.Digest)) store.Write(new(1, plan, "prepared"));
                    activeDigest = plan.Digest;
                    Report(progress, new(99, "scaffold.previewReady"));
                    return plan;
                }
                catch
                {
                    ScaffoldWorkspaceFiles.DeleteStage(staging);
                    throw;
                }
            }
            finally { preparation.Release(); }
        }, cancellationToken);

    /// <summary>后台独占消费计划; 提交后只回读事实, 不重放或因取消撤销已发布工程.</summary>
    /// <param name="beforeCommit">完整复核后等待的可靠回执钩子; 失败或取消保持未提交计划.</param>
    public Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress,
        CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null) => Task.Run(async () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ScaffoldValidation.Validate(request);
            using var lease = store.Lease(digest);
            var receipt = store.Read(digest);
            var plan = receipt.Plan;
            var target = ScaffoldWorkspaceFiles.Output(output, request.ProjectName);
            if (!string.Equals(target, plan.OutputPath, StringComparison.OrdinalIgnoreCase)
                || JsonSerializer.Serialize(request, ScaffoldPlanStore.Json) != JsonSerializer.Serialize(plan.Request, ScaffoldPlanStore.Json))
                throw new AppException("SCAFFOLD_APPROVAL_MISMATCH", "创建参数与已预览计划不同, 请重新预览", 3);
            if (receipt.State == "published" && receipt.Result is { } saved)
            {
                if (saved.PlanId != plan.Id || saved.ProjectPath != Path.GetFullPath(Path.Combine(target, plan.EntryFile)) || !saved.ProjectCreated) throw Verification();
                // 显式初始化可能只完成部分元数据或处于恢复核验, 不能因回执为 false 而重放或拒绝原产物.
                ScaffoldWorkspaceFiles.AssertInventory(plan, workspaceFiles.Inventory(target, cancellationToken, allowGeneratedGit: plan.Request.GitInitialize));
                return saved;
            }
            if (receipt.State == "discarded") throw Expired();
            if (receipt.State == "committing") return await RecoverAsync(receipt, progress);
            var staging = ScaffoldWorkspaceFiles.Stage(target, plan.Id);
            if (clock.GetUtcNow() > plan.ExpiresAt)
            {
                ScaffoldWorkspaceFiles.DeleteStage(staging);
                store.Write(receipt with { State = "discarded" });
                throw Expired();
            }
            await ValidateCommitAsync(plan, staging, cancellationToken);
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken);
                // 回执等待不能冻结用户目录或来源; 完整复核后才进入不可取消的发布边界.
                await ValidateCommitAsync(plan, staging, cancellationToken);
            }
            committing?.Invoke();
            store.Write(receipt with { State = "committing" });
            Report(progress, new(99, "scaffold.publishing", false));
            Directory.Move(staging, target);
            return await FinishAsync(plan, progress);
        }, cancellationToken);

    /// <summary>复核计划、来源、实际 staging 和最终目标; 可等待回执前后复用同一规则, 不标记已提交.</summary>
    /// <param name="plan">已批准且完整冻结的计划.</param>
    /// <param name="staging">当前计划独占的真实待发布目录.</param>
    /// <param name="cancellationToken">提交前响应的取消令牌.</param>
    /// <returns>核验完成任务; 过期、来源变化、清单变化及冲突均拒绝发布.</returns>
    private async Task ValidateCommitAsync(ScaffoldPlan plan, string staging, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() > plan.ExpiresAt) throw Expired();
        var target = plan.OutputPath;
        var request = plan.Request;
        UserDataStore.Check(Path.GetDirectoryName(target)!);
        if (Directory.Exists(target) || File.Exists(target)) throw Conflict();
        if ((await catalog.GetAsync(request.TemplateId, cancellationToken)).Info.Baseline != request.TemplateBaseline) throw Stale();
        foreach (var function in plan.UdfFunctions?.Where(function => function.Source == "local") ?? [])
        {
            if (localUdfs is null || (await localUdfs.GetAsync(function.PackageId, cancellationToken)).Baseline != function.Baseline)
                throw new AppException("STALE_LOCAL_UDF", "本地函数已变化, 请重新选择并预览", 3);
        }
        // 包内容已冻结, 此处只复核来源配置而不重新联网解析函数或 latest.
        foreach (var source in (plan.UdfFunctions ?? []).Where(function => function.Source.StartsWith("source:", StringComparison.Ordinal)).GroupBy(function => function.Source))
        {
            var baseline = source.First().SourceBaseline;
            if (udfSources is null || !InputValidation.IsDigest(baseline) || source.Any(function => function.SourceBaseline != baseline)) throw StaleSource();
            try { if ((await udfSources.GetAsync(source.Key, cancellationToken)).Baseline != baseline) throw StaleSource(); }
            catch (AppException exception) when (exception.Code == "UDF_SOURCE_MISSING") { throw StaleSource(); }
        }
        ScaffoldWorkspaceFiles.AssertInventory(plan, workspaceFiles.Inventory(staging, cancellationToken));
        _ = await reader.ReadAsync(Path.Combine(staging, plan.EntryFile), cancellationToken);
        ScaffoldWorkspaceFiles.AssertInventory(plan, workspaceFiles.Inventory(staging, cancellationToken));
        UserDataStore.Check(staging);
        UserDataStore.Check(Path.GetDirectoryName(target)!);
        if (plan.Request.GitInitialize && ScaffoldWorkspaceFiles.HasGitAncestor(Path.GetDirectoryName(target)!))
            throw new AppException("GIT_NESTED_REPOSITORY", "输出位于已有 Git 仓库内, 请关闭 Git 初始化或选择其他目录", 3);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>已准备计划的来源变化只能重新预览, 不改写或重放已经发布的事实.</summary>
    private static AppException StaleSource() => new("STALE_UDF_SOURCE", "函数来源已变化或删除, 请重新选择并预览", 3);

    /// <summary>丢弃只删除对应未发布 staging, 永不删除最终目录.</summary>
    public Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = store.Lease(digest);
        var receipt = store.Read(digest);
        if (receipt.State != "prepared") return false;
        ScaffoldWorkspaceFiles.DeleteStage(ScaffoldWorkspaceFiles.Stage(receipt.Plan.OutputPath, receipt.Plan.Id));
        store.Write(receipt with { State = "discarded" });
        return true;
    }, cancellationToken);

    /// <summary>只停止接收; CLI 预览必须跨进程保留, 桌面主动关闭另调用 DiscardAsync.</summary>
    public void Dispose() => disposed = true;

    /// <summary>只在独占记录锁内清理已验证归属且过期的 prepared 树, 失败保留记录以供重试.</summary>
    private bool SweepExpired(CancellationToken token)
    {
        var pending = false;
        foreach (var digest in store.SweepCandidates())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var lease = store.Lease(digest);
                var receipt = store.Read(digest);
                if (receipt.State != "prepared" || clock.GetUtcNow() <= receipt.Plan.ExpiresAt) continue;
                var output = ScaffoldWorkspaceFiles.Output(receipt.Plan.OutputPath, receipt.Plan.Request.ProjectName);
                ScaffoldWorkspaceFiles.DeleteStage(ScaffoldWorkspaceFiles.Stage(output, receipt.Plan.Id));
                store.Write(receipt with { State = "discarded" });
            }
            catch (AppException error) when (error.Code is "USER_DATA_BUSY" or "INVALID_SCAFFOLD_PLAN")
            {
                // 其他进程正在消费或未知记录不属于可自动回收的树.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException)
            {
                pending = true;
            }
        }
        return pending;
    }

    /// <summary>未知提交先验证最终完整清单, 缺失或不符时冻结, 不重新移动或生成.</summary>
    private async Task<ScaffoldResult> RecoverAsync(ScaffoldPlanReceipt receipt, IProgress<ScaffoldProgress>? progress)
    {
        try
        {
            if (!Directory.Exists(receipt.Plan.OutputPath)) throw Verification();
            ScaffoldWorkspaceFiles.AssertInventory(receipt.Plan, workspaceFiles.Inventory(receipt.Plan.OutputPath, CancellationToken.None, allowGeneratedGit: receipt.Plan.Request.GitInitialize));
        }
        catch (Exception error) when (error is AppException or IOException or UnauthorizedAccessException) { throw Verification(); }
        return await FinishAsync(receipt.Plan, progress, recovering: true);
    }

    /// <summary>发布后保留模型回读与 Git 分项, 所有结果不再使用原取消令牌.</summary>
    private async Task<ScaffoldResult> FinishAsync(ScaffoldPlan plan, IProgress<ScaffoldProgress>? progress, bool recovering = false)
    {
        var projectPath = Path.GetFullPath(Path.Combine(plan.OutputPath, plan.EntryFile));
        string? baseline = null;
        var verified = false;
        var git = false;
        string? errorCode = null;
        var warnings = plan.Warnings.ToList();
        try
        {
            ScaffoldWorkspaceFiles.AssertInventory(plan, workspaceFiles.Inventory(plan.OutputPath, CancellationToken.None, allowGeneratedGit: recovering && plan.Request.GitInitialize));
            var snapshot = await reader.ReadAsync(projectPath, CancellationToken.None);
            baseline = snapshot.BaselineId;
            verified = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            errorCode = "SCAFFOLD_VERIFICATION_PENDING";
            warnings.Add("项目已创建, 定义回读未完成, 请核对后继续; 不会重新发布");
        }
        if (plan.Request.GitInitialize && verified)
        {
            string? gitFailureReason = null;
            try
            {
                // 未知提交恢复只验证模型, 不执行现有目录内的 Git 或重放初始化.
                if (!recovering)
                {
                    var capability = await ProbeGitAsync(CancellationToken.None);
                    if (capability.Available) git = await (initializeGit ?? new ScaffoldGit().InitializeAsync)(Path.Combine(plan.OutputPath, "pbip"), CancellationToken.None);
                    else gitFailureReason = GitReason(capability);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { git = false; }
            if (!git)
            {
                errorCode = recovering ? "GIT_INIT_NEEDS_VERIFICATION" : gitFailureReason ?? "GIT_INIT_FAILED";
                if (gitFailureReason is not null && !warnings.Contains(gitFailureReason, StringComparer.Ordinal)) warnings.Add(gitFailureReason);
                warnings.Add(recovering ? "项目已创建, Git 初始化状态需核验, 不会自动重试"
                    : "项目已创建, Git 未初始化; 请安装或检查 Git 后在已有工程的 pbip 目录中手动初始化, 不要重复创建工程");
            }
        }
        var result = new ScaffoldResult(plan.Id, verified ? "published" : "verification_pending", true, verified, git,
            projectPath, baseline, plan.Tables, warnings, errorCode);
        try { store.Write(new(1, plan, "published", result)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException)
        {
            warnings.Add("项目已创建, 发布回执未能保存, 下次将先核验现有产物");
            // 回执失败必须保持待核验, 不能被已确定的 Git 故障遮蔽; 原业务故障仍保留.
            if (errorCode is not null && !warnings.Contains(errorCode, StringComparer.Ordinal)) warnings.Add(errorCode);
            result = result with { Warnings = warnings.ToArray(), ErrorCode = "SCAFFOLD_RECEIPT_PENDING" };
        }
        Report(progress, new(verified ? 100 : 99, "scaffold.created", false));
        return result;
    }

    /// <summary>CLI 与桌面复用同一能力; 注入的初始化器默认自带能力, 不依赖测试机器的 Git.</summary>
    private Task<GitCapability> ProbeGitAsync(CancellationToken token) => probeGit is not null ? probeGit(token)
        : initializeGit is not null ? Task.FromResult(new GitCapability(true)) : new ScaffoldGit().ProbeAsync(token);

    /// <summary>只允许稳定能力原因进入计划和回执, 不记录外部程序诊断或路径.</summary>
    private static string GitReason(GitCapability capability) => capability.Reason is "GIT_NOT_FOUND" or "GIT_PROBE_FAILED" or "GIT_PROBE_TIMEOUT"
        ? capability.Reason : "GIT_PROBE_FAILED";

    /// <summary>进度观察者失败不得改报已提交业务结果.</summary>
    private static void Report(IProgress<ScaffoldProgress>? progress, ScaffoldProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    /// <summary>已有目标不覆盖.</summary>
    private static AppException Conflict() => new("OUTPUT_CONFLICT", "输出目录已存在, 请选择新的项目目录", 3);
    /// <summary>模板基线失效不得沿用原预览.</summary>
    private static AppException Stale() => new("STALE_TEMPLATE", "模板内容已变化, 请刷新并重新预览", 3);
    /// <summary>过期计划不能重放.</summary>
    private static AppException Expired() => new("SCAFFOLD_PLAN_EXPIRED", "脚手架预览已过期或已丢弃, 请重新预览", 3);
    /// <summary>未知提交先核验, 不自动重新创建.</summary>
    private static AppException Verification() => new("SCAFFOLD_NEEDS_VERIFICATION", "项目提交状态需核验, 请检查已生成目录, 不会重复创建", 3);
}
