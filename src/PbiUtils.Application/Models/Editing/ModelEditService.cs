// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\ModelEditService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立客户端模型编辑, 观测基线核对及显式保存和丢弃的共享用例.

using PbiUtils.Application.Udf;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;
using System.Text.Json.Serialization;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Application.Models.Editing;

/// <summary>绑定当前模型的编辑参数, 不接受源、输出路径或连接地址.</summary>
public sealed record ModelEditRequest(string Operation, string ObjectName, string Property, string Value, string? Table = null,
    [property: JsonIgnore] ModelConfigurationPlan? Configuration = null);
/// <summary>后端操作上下文, 保存适配器复核基线后直接写入当前模型或工程, 不创建历史备份.</summary>
public interface IModelDraft : IDisposable, IAsyncDisposable
{
    ProjectSnapshot Snapshot { get; }
    /// <summary>按来源校验暂存; 连接模型失败结束原会话, 离线模型失败保留原草稿.</summary>
    Task ApplyAsync(ModelEditRequest request, CancellationToken token);
    /// <summary>显式提交本轮上下文; 连接模型失败释放原会话, 离线草稿保留既有恢复语义, 提交后不可取消.</summary>
    Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token);
    /// <summary>纯内存草稿默认同步释放; 持有原 TOM 会话的实现必须覆盖并异步排空 SDK 清理.</summary>
    ValueTask IAsyncDisposable.DisposeAsync() { Dispose(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
}
/// <summary>文件与 Desktop 共用草稿工厂, 远端模型必须拒绝.</summary>
public interface IModelEditWorkspace
{
    /// <summary>从已授权快照创建原对象操作上下文或离线草稿, 不提交文件或引擎.</summary>
    Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token);
}
/// <summary>模型已提交后的同步重试, 不得再次应用同一模型操作.</summary>
public interface IModelSaveCompletion
{
    Task<ProjectSnapshot> CompleteSaveAsync(ProjectSnapshot saved, Action? committing, CancellationToken token);
}
/// <summary>集中判断来源读写能力, 不以网页提交的标记授予权限.</summary>
public static class ModelAccessPolicy
{
    /// <summary>只有本地 PBIP 和实际发现的 Desktop 允许编辑.</summary>
    public static bool IsReadOnly(ProjectSnapshot snapshot) => snapshot.Connection?.Kind is { } kind
        ? kind is not ("pbip" or "desktop") : snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal);
}
/// <summary>每个客户端独占草稿和已观测基线; 读取草稿不会提交或覆盖源.</summary>
public sealed class ModelEditService(IProjectReader reader, IModelEditWorkspace workspace, ILogger<ModelEditService>? logger = null) : IProjectReader, IDisposable, IAsyncDisposable
{
    private readonly ILogger log = logger ?? NullLogger<ModelEditService>.Instance;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ProjectSnapshot? selected;
    private IModelDraft? draft;
    private bool requiresVerification;
    private Func<CancellationToken, Task>? configurationBeforeSave;
    private Task? disposal;
    private readonly Lock disposalSync = new();
    private bool HasIncompleteDesktopCommit => selected?.Connection?.Kind == "desktop" && selected.SaveResult is { ModelSaved: true, DesktopSynchronized: false };
    public bool HasPendingChanges => requiresVerification || draft?.Snapshot.HasPendingChanges == true || selected?.SaveResult?.ErrorCode is not null || HasIncompleteDesktopCommit;

    /// <summary>在与 工具/手工编辑相同的门闩内执行当前模型的受控只读操作, 不建立草稿或授予写权限.</summary>
    /// <param name="projectId">调用方看到的完整模型身份.</param>
    /// <param name="baselineId">调用方看到的完整结构基线.</param>
    /// <param name="action">持锁执行的只读用例; 如发布外部文件, 须在发布前自行再次权威核验.</param>
    /// <param name="token">取得门闩和操作发布前的取消令牌.</param>
    /// <returns>只读用例的真实结果, 用例发布完成后不再执行可能误报失败的尾部核验.</returns>
    /// <exception cref="AppException">模型忙、未选择、待保存、身份或基线过期时拒绝.</exception>
    public async Task<T> ExecuteReadAsync<T>(string projectId, string baselineId, Func<ProjectSnapshot, CancellationToken, Task<T>> action, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            var baseline = selected ?? throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
            if (HasPendingChanges || baseline.HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "请先完成模型保存或核验", 3);
            if (baseline.ProjectId != projectId || baseline.BaselineId != baselineId) throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重试", 3);
            var fresh = await reader.ReadAsync(baseline.ProjectPath, token);
            token.ThrowIfCancellationRequested();
            if (fresh.ProjectPath != baseline.ProjectPath || fresh.ProjectId != projectId || fresh.BaselineId != baselineId || !ModelConnectionInfo.SameTarget(fresh.Connection, baseline.Connection))
                throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重试", 3);
            if (fresh.HasPendingChanges || fresh.SaveResult?.ErrorCode is not null || fresh.SaveResult is { ModelSaved: true, DesktopSynchronized: false })
                throw new AppException("UNSAVED_CHANGES", "请先完成模型保存或核验", 3);
            return await action(fresh, token);
        }
        finally { gate.Release(); }
    }

    /// <summary>绑定当前目标与完整基线, 在受信目标上下文只读分析; Desktop 读取原连接对象, 与编辑串行.</summary>
    public async Task<UdfLintResult> AnalyzeUdfsAsync(string projectId, string baselineId, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            var baseline = RequireWritable();
            if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "请先完成保存或核验后分析函数", 3);
            baseline = await RefreshWritableAsync(token);
            if (baseline.ProjectId != projectId || baseline.BaselineId != baselineId)
                throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新分析函数", 3);
            var candidate = await workspace.CreateDraftAsync(baseline, token);
            Exception? primary = null;
            try
            {
                var result = await (candidate as IUdfLintDraft ?? throw new AppException("UNSUPPORTED", "当前目标不支持完整函数分析", 3)).AnalyzeUdfsAsync(token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception error) { primary = error; throw; }
            finally { await ReleaseDraftAsync(candidate, primary); }
        }
        finally { gate.Release(); }
    }

    /// <summary>配置在目标操作上下文验证并复核冻结来源后直接提交; 连接模型在原对象暂存, 无变化不提交.</summary>
    /// <param name="request">已冻结且绑定当前模型的配置请求.</param>
    /// <param name="token">准备和提交开始前的取消令牌.</param>
    /// <param name="committing">宿主进入不可重放提交阶段的回调.</param>
    /// <param name="beforeCommit">提交前再次核验冻结来源的回调, 失败不提交.</param>
    /// <returns>实际保存并回读的模型快照, 无变化时返回当前基线.</returns>
    /// <exception cref="AppException">目标、基线、配置或恢复状态不允许写入.</exception>
    public Task<ProjectSnapshot> EditConfigurationAndSaveAsync(ModelEditRequest request, CancellationToken token,
        Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null) => RunAsync("ModelEdit.Configuration", async () =>
    {
        var baseline = RequireWritable();
        if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "上次保存未完成, 请先处理后继续配置", 3);
        baseline = await RefreshWritableAsync(token);
        Validate(request, baseline);
        if (request.Operation != "configuration.apply") throw new AppException("UNSUPPORTED_OPERATION", "此入口只接受冻结的配置", 3);
        IModelDraft? candidate = await workspace.CreateDraftAsync(baseline, token);
        Exception? primary = null;
        try
        {
            await candidate.ApplyAsync(request, token);
            // 同步适配工作完成后仍须观察取消, 无变化也不能跳过取消和本轮操作上下文排空.
            token.ThrowIfCancellationRequested();
            if (!candidate.Snapshot.HasPendingChanges) return baseline;
            configurationBeforeSave = beforeCommit;
            draft = candidate; candidate = null;
            return await SaveCoreAsync(committing, token);
        }
        catch (Exception error) { primary = error; throw; }
        finally { if (candidate is not null) await ReleaseDraftAsync(candidate, primary); }
    }, token);

    /// <summary>一次操作完成原对象校验与直接提交, 不额外确认或创建历史备份; 连接模型准备失败释放本次会话, 不保留可重放草稿.</summary>
    /// <param name="request">绑定当前模型的精确手工修改.</param>
    /// <param name="token">准备和提交开始前的取消令牌.</param>
    /// <param name="committing">宿主进入不可重放提交阶段的回调.</param>
    /// <returns>提交后的权威模型快照及实际保存阶段.</returns>
    /// <exception cref="AppException">请求、目标、基线或恢复状态不允许写入.</exception>
    public Task<ProjectSnapshot> EditAndSaveAsync(ModelEditRequest request, CancellationToken token, Action? committing = null)
        => RunAsync("ModelEdit.EditAndSave", async () =>
        {
            var baseline = RequireWritable();
            if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "上次操作未完成, 请先完成保存或重新读取核验", 3);
            baseline = await RefreshWritableAsync(token);
            Validate(request, baseline);
            IModelDraft? operation = await workspace.CreateDraftAsync(baseline, token);
            Exception? primary = null;
            try
            {
                await operation.ApplyAsync(request, token);
                draft = operation; operation = null;
                return await SaveCoreAsync(committing, token);
            }
            catch (Exception error) { primary = error; throw; }
            finally { if (operation is not null) await ReleaseDraftAsync(operation, primary); }
        }, token);

    /// <summary>外部工具原对象暂存后确认真实 before/after; 一次授权只作用于当前运行和基线, 回执持久后才提交.</summary>
    /// <param name="request">外部工具提出的精确修改参数, 不包含授权.</param>
    /// <param name="runId">宿主创建的本轮运行身份.</param>
    /// <param name="workspaceId">宿主绑定的当前工作区身份.</param>
    /// <param name="policy">仅负责 外部工具精确提案确认及一次授权的策略.</param>
    /// <param name="token">提交开始前的取消令牌.</param>
    /// <param name="committing">宿主提交阶段回调.</param>
    /// <param name="beforeCommit">实际提交前可靠写入业务回执的回调.</param>
    /// <param name="phase">准备和授权阶段通知.</param>
    /// <returns>权威回读的修改结果, 不将 Desktop 模型提交等同于报表文件保存.</returns>
    /// <exception cref="AppException">提案无变化、授权拒绝/失效或目标状态不允许写入.</exception>
    public Task<ProjectSnapshot> EditAuthorizedAndSaveAsync(ModelEditRequest request, string runId, string workspaceId, ToolWritePolicy policy,
        CancellationToken token, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null, Action<string>? phase = null)
        => RunAsync("ModelEdit.AuthorizedEditAndSave", async () =>
        {
            var baseline = RequireWritable();
            if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存, 不得重复修改", 3);
            baseline = await RefreshWritableAsync(token);
            Validate(request, baseline);
            IModelDraft? candidate = await workspace.CreateDraftAsync(baseline, token);
            Exception? primary = null;
            try
            {
                await candidate.ApplyAsync(request, token);
                var proposal = ToolWriteProposal.Create(runId, workspaceId, baseline, candidate.Snapshot, request);
                if (proposal.Changes.Count == 0) throw new AppException("MODEL_NO_CHANGES", "提案没有实际属性变化, 未提交模型", 3);
                phase?.Invoke("confirming");
                var grant = await policy.AuthorizeAsync(proposal, token);
                phase?.Invoke("preparing");
                var fresh = await reader.ReadAsync(baseline.ProjectPath, token);
                grant.Consume(runId, workspaceId, fresh, request);
                if (beforeCommit is not null) await beforeCommit(token);
                token.ThrowIfCancellationRequested();
                draft = candidate; candidate = null;
                return await SaveCoreAsync(committing, token);
            }
            catch (Exception error) { primary = error; throw; }
            finally { if (candidate is not null) await ReleaseDraftAsync(candidate, primary); }
        }, token);

    /// <summary>当前来源由后端绑定, 远端即使伪造网页请求也不能编辑.</summary>
    private ProjectSnapshot RequireWritable()
    {
        var baseline = selected ?? throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        if (ModelAccessPolicy.IsReadOnly(baseline)) throw new AppException("READ_ONLY_MODEL", "当前远端模型只读", 3);
        return baseline;
    }

    /// <summary>准备阶段核对当前 Desktop 定义与此客户端已观测基线, 不自动接纳其他客户端或外部更改.</summary>
    /// <param name="token">读取及接收结果前的取消令牌.</param>
    /// <returns>完整结构仍一致的权威快照, 允许更新不影响结构的文件观察信息.</returns>
    /// <exception cref="AppException">读取失败、身份/基线变化或待处理状态时拒绝准备, 必须显式重读后重新提出修改.</exception>
    private async Task<ProjectSnapshot> RefreshWritableAsync(CancellationToken token)
    {
        var baseline = RequireWritable();
        if (baseline.Connection?.Kind != "desktop" || HasPendingChanges || draft is not null || baseline.HasPendingChanges) return baseline;
        var fresh = await reader.ReadAsync(baseline.ProjectPath, token);
        token.ThrowIfCancellationRequested();
        if (fresh.BaselineId != baseline.BaselineId)
            throw new AppException("STALE_BASELINE", "模型已被其他客户端或外部程序修改, 请重新读取并检查修改后再操作", 3);
        return ObserveDesktopSnapshot(baseline, fresh);
    }

    /// <summary>仅接收同一 Desktop 目标的完整只读结果, 不把刷新当成切换目标或解除恢复状态.</summary>
    /// <param name="baseline">门闩内原目标, 其来源和完整实例身份已经由宿主绑定.</param>
    /// <param name="fresh">实际读取结果; 来源、路径、身份或待处理事实异常时不接收.</param>
    /// <returns>更新后的共享编辑基线, 同基线保留已有准确保存事实.</returns>
    /// <exception cref="AppException">目标变化或读取结果不适合新操作时拒绝, 保留原选择.</exception>
    private ProjectSnapshot ObserveDesktopSnapshot(ProjectSnapshot baseline, ProjectSnapshot fresh)
    {
        if (fresh.ProjectId != baseline.ProjectId || fresh.ProjectPath != baseline.ProjectPath || !ModelConnectionInfo.SameTarget(fresh.Connection, baseline.Connection)
            || fresh.HasPendingChanges || fresh.SaveResult?.ErrorCode is not null || fresh.SaveResult is { ModelSaved: true, DesktopSynchronized: false })
            throw new AppException("STALE_BASELINE", "配置目标已失效, 请重新读取", 3);
        selected = baseline.SaveResult is not null && baseline.BaselineId == fresh.BaselineId ? fresh with { SaveResult = baseline.SaveResult } : fresh;
        return selected;
    }

    /// <summary>成功连接才选中快照; 门闩与未保存状态通过后才提交宿主关联状态, 失败保留原模型.</summary>
    /// <param name="snapshot">新的权威快照, null 表示断开.</param>
    /// <param name="beforeSelect">选择成功前提交宿主绑定状态, 不在模型忙或有待保存内容时调用.</param>
    public void Select(ProjectSnapshot? snapshot, Action? beforeSelect = null)
    {
        if (!gate.Wait(0)) throw new AppException("BUSY", "模型操作正在执行, 请稍后切换", 3);
        try
        {
            if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "当前模型有未保存的修改, 请先保存或放弃修改", 3);
            beforeSelect?.Invoke();
            draft?.Dispose(); draft = null; selected = snapshot;
        }
        finally { gate.Release(); }
    }
    /// <summary>工具 和刷新读取当前内存版本; 无草稿的同目标 Desktop 读取同步共享基线, 其他目标保持真实只读.</summary>
    public async Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new AppException("BUSY", "模型操作正在执行, 请稍后刷新", 3);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (draft is { } current && current.Snapshot.ProjectPath == projectPath)
            {
                if (requiresVerification)
                {
                    var observed = await reader.ReadAsync(projectPath, cancellationToken);
                    return observed with { HasPendingChanges = true, SaveResult = new(false, false, false, "模型会话或提交结果待核验, 不得重复保存模型修改", "EXECUTION_NEEDS_VERIFICATION") };
                }
                return current.Snapshot;
            }
            if (selected is { SaveResult.ErrorCode: not null } pending && pending.ProjectPath == projectPath) return pending;
            var fresh = await reader.ReadAsync(projectPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is { Connection.Kind: "desktop", HasPendingChanges: false } baseline && baseline.ProjectPath == projectPath
                && !HasPendingChanges && draft is null) return ObserveDesktopSnapshot(baseline, fresh);
            return selected is { SaveResult: not null } saved && saved.ProjectPath == projectPath && saved.BaselineId == fresh.BaselineId
                ? fresh with { SaveResult = saved.SaveResult } : fresh;
        }
        finally { gate.Release(); }
    }
    /// <summary>校验来源和白名单后累积暂存; 已提交但待保存或核验时拒绝新草稿, 不重放模型提交.</summary>
    public Task<ProjectSnapshot> StageAsync(ModelEditRequest request, CancellationToken token) => RunAsync("ModelEdit.Stage", async () =>
    {
        if (requiresVerification) throw new AppException("EXECUTION_NEEDS_VERIFICATION", "模型会话或提交结果待核验, 暂不接受新的修改", 3);
        if (selected?.SaveResult?.ErrorCode is not null || HasIncompleteDesktopCommit) throw new AppException("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存或核验", 3);
        var baseline = RequireWritable();
        baseline = await RefreshWritableAsync(token);
        Validate(request, baseline);
        draft ??= await workspace.CreateDraftAsync(baseline, token);
        try { await draft.ApplyAsync(request, token); }
        catch (Exception error)
        {
            // Desktop 原会话失败后已经丢弃本地状态, 不能再次复用它; 离线草稿仍保留原有原子失败语义.
            if (baseline.Connection?.Kind == "desktop" && await ReleaseDraftAsync(draft, error)) draft = null;
            throw;
        }
        return draft.Snapshot;
    }, token);
    /// <summary>直接保存本轮修改; 连接模型提交前失败结束会话, 未知提交拒绝重放, 离线保留原恢复语义.</summary>
    /// <param name="token">提交开始前的取消令牌.</param>
    /// <param name="committing">宿主进入提交阶段的回调.</param>
    /// <returns>保存或完成既有文件事务尾部后的权威快照.</returns>
    /// <exception cref="AppException">无待保存状态或原提交结果未知时拒绝.</exception>
    public Task<ProjectSnapshot> SaveAsync(CancellationToken token, Action? committing = null) => RunAsync("ModelEdit.Save", async () =>
    {
        if (requiresVerification) throw new AppException("EXECUTION_NEEDS_VERIFICATION", "上次模型会话或提交结果待核验, 不得重试模型修改; 请先回读核验", 3);
        if (draft is null && selected?.SaveResult?.ErrorCode is not null && workspace is IModelSaveCompletion completion)
        {
            selected = await completion.CompleteSaveAsync(selected, committing, token);
            return selected;
        }
        if (draft is null || !HasPendingChanges) throw new AppException("NO_PENDING_CHANGES", "没有待保存的修改", 3);
        return await SaveCoreAsync(committing, token);
    }, token);
    /// <summary>由编辑锁持有者提交并更新基线; 连接模型提交前失败结束原会话, 未知提交保持只读核验.</summary>
    private async Task<ProjectSnapshot> SaveCoreAsync(Action? committing, CancellationToken token)
    {
        if (draft is null) throw new AppException("NO_PENDING_CHANGES", "没有待保存的修改", 3);
        var commitStarted = false;
        try
        {
            if (configurationBeforeSave is not null) await configurationBeforeSave(token);
            var saved = await draft.SaveAsync(() => { committing?.Invoke(); commitStarted = true; }, token);
            await draft.DisposeAsync(); draft = null; selected = saved; configurationBeforeSave = null;
            return saved;
        }
        catch (Exception error)
        {
            // SDK 提交已经开始但没有确定结果, 保留核验状态; 所有入口均不得重放草稿.
            if (commitStarted) requiresVerification = true;
            else if (selected?.Connection?.Kind == "desktop")
            {
                // 连接模型执行即时操作, 未提交的原会话不作为待保存业务状态; 用户可修改输入后重新提交.
                if (draft is not null && await ReleaseDraftAsync(draft, error)) draft = null;
                configurationBeforeSave = null;
            }
            throw;
        }
    }
    /// <summary>明确放弃后回读当前源; 回读失败保留草稿.</summary>
    public Task<ProjectSnapshot> DiscardAsync(CancellationToken token) => RunAsync("ModelEdit.Discard", async () =>
    {
        var baseline = selected ?? throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        var fresh = await reader.ReadAsync(baseline.ProjectPath, token);
        token.ThrowIfCancellationRequested();
        if (draft is not null) await ReleaseDraftAsync(draft, null);
        draft = null; selected = fresh; requiresVerification = false; configurationBeforeSave = null;
        return fresh;
    }, token);
    /// <summary>结束当前候选且保留业务首错; 未确认释放仍由本服务拥有, 只允许回读或关闭重试清理.</summary>
    /// <param name="owned">门闩内唯一拥有的候选或操作会话, 失败后不可复用为新草稿.</param>
    /// <param name="primary">正在传播的业务或取消异常; 清理失败只附加安全事实, 不替换首错.</param>
    /// <returns>本次是否已确认资源释放; 没有业务首错时清理失败如实抛出.</returns>
    private async Task<bool> ReleaseDraftAsync(IModelDraft owned, Exception? primary)
    {
        try { await owned.DisposeAsync(); return true; }
        catch
        {
            draft = owned; requiresVerification = true;
            if (primary is null) throw;
            primary.Data["ModelDraftCleanupFailed"] = true;
            return false;
        }
    }
    /// <summary>单窗口串行编辑, 所有入口复用唯一日志与取消边界.</summary>
    private Task<ProjectSnapshot> RunAsync(string operation, Func<Task<ProjectSnapshot>> action, CancellationToken token) => BusinessLog.RunAsync<ProjectSnapshot>(log, operation, async () =>
    {
        if (!await gate.WaitAsync(0, token)) throw new AppException("BUSY", "模型编辑正在执行", 3);
        try { token.ThrowIfCancellationRequested(); return await action(); }
        finally { gate.Release(); }
    });
    /// <summary>验证模型操作白名单、输入预算和批次基线, 拒绝报表写入; SDK 再验证对象与兼容级别.</summary>
    private static void Validate(ModelEditRequest request, ProjectSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectName) || request.ObjectName.Length > 200 || request.Value is null || request.Value.Length > 30000
            || request.Table?.Length > 200) throw new AppException("VALIDATION_ERROR", "对象名或变更内容不合法");
        string[] allowed = request.Operation switch
        {
            "measure.batch" => ["items"],
            "table.deleteBatch" => ["items"],
            "udf.deleteUnused" => ["items"],
            "configuration.apply" when request.Configuration is not null => ["config"],
            "measure.set" => ["description", "displayFolder", "formatString", "expression"],
            "table.set" => ["description"],
            "udf.set" => ["expression"],
            "calendar.create" or "period.generate" => ["config"],
            _ => throw new AppException("UNSUPPORTED_OPERATION", "当前来源不支持此修改", 3)
        };
        if (!allowed.Contains(request.Property)) throw new AppException("UNSUPPORTED_PROPERTY", "此属性不可修改", 3);
        ModelEditValueLimits.Validate(request.Operation, request.Property, request.Value);
        if (request.Operation == "measure.batch" && MeasureBatch.Parse(request.Value).BaselineId != snapshot.BaselineId)
            throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新选择度量值", 3);
        if (request.Operation == "table.deleteBatch")
        {
            var batch = TableDeleteBatch.Parse(request.Value);
            if (batch.ProjectId != snapshot.ProjectId || batch.BaselineId != snapshot.BaselineId)
                throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新选择要删除的表", 3);
        }
        if (request.Operation == "udf.deleteUnused")
        {
            var batch = UdfDeleteBatch.Parse(request.Value);
            if (batch.ProjectId != snapshot.ProjectId || batch.BaselineId != snapshot.BaselineId)
                throw new AppException("STALE_BASELINE", "模型已变化, 请重新分析函数", 3);
        }
        if (request.Operation == "configuration.apply" && (request.Configuration!.BaselineId != snapshot.BaselineId || request.Value != request.Configuration.Digest))
            throw new AppException("STALE_BASELINE", "配置准备后模型已变化, 请重新核验", 3);
    }
    /// <summary>关闭开始释放而不提交; 原 TOM 会话的异步 SDK 清理由 DisposeAsync 排空, 不阻塞 UI.</summary>
    public void Dispose() => StartDisposal();
    /// <summary>异步等待本窗口操作上下文结束, 不以后台未完成清理宣告关闭完成.</summary>
    public ValueTask DisposeAsync() => new(StartDisposal());
    /// <summary>同次关闭共享任务; 仅后续明确释放可重试失败清理, 不重新执行编辑或提交.</summary>
    private Task StartDisposal()
    {
        lock (disposalSync)
        {
            if (disposal is null || disposal.IsCompleted && !disposal.IsCompletedSuccessfully)
                disposal = Task.Run(DisposeOwnedAsync, CancellationToken.None);
            return disposal;
        }
    }
    /// <summary>只释放本服务拥有的上下文, 调用方已经停止请求并排空模型操作.</summary>
    private async Task DisposeOwnedAsync()
    {
        var owned = draft;
        if (owned is not null) { await owned.DisposeAsync(); draft = null; }
    }
}
