// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Configuration\ModelConfigurationBridge.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定已创建工程和当前模型的配置上下文, 共用原模型确认与保存通道.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Host.Bridge.Configuration;

/// <summary>写入和恢复持有会话 projectGate; 预览经独立只读队列读取受信目标, 不在填写期间持锁.</summary>
public sealed class ModelConfigurationBridge(IProjectReader files, IModelEditWorkspace workspace, ModelConfigurationCompiler compiler,
    Func<ProjectSnapshot, CancellationToken, Task>? verifyCreatedWritable = null,
    Func<ProjectSnapshot, int, CancellationToken, Task<bool>>? confirmUpgrade = null,
    IModelEditWorkspace? currentWorkspace = null) : IDisposable, IAsyncDisposable
{
    private readonly Lock sync = new();
    /// <summary>本配置独占的编辑服务或只读预览会话及其最近一次清理结果, 不接受 borrowed alias.</summary>
    private sealed record OwnedDisposal(IAsyncDisposable Owner, Task Completion);
    private readonly List<OwnedDisposal> ownedDisposals = [];
    private Task? disposal;
    private ProjectSnapshot? created;
    private ModelEditService? createdEdits;
    private ModelEditService? createdTargetEdits;
    private string? createdId;
    private string? currentId;
    private string? currentIdentity;
    private string? createdFileIdentity;
    private string? createdBaseline;
    private int generation;

    /// <summary>关闭确认必须包含独立已创建工程的待保存和待核验状态.</summary>
    public bool HasPendingChanges => createdTargetEdits?.HasPendingChanges == true || createdEdits?.HasPendingChanges == true;

    /// <summary>从当前文档绑定身份取得完整受信 TOM 只读预览, Desktop 直接读取原连接对象, 不触发保存.</summary>
    public async Task<ScaffoldCalendarProgram> PreviewCalendarAsync(string targetId, string baselineId, ScaffoldCalendarOptions options,
        string locale, ProjectSnapshot? current, CancellationToken token)
    {
        ProjectSnapshot snapshot;
        bool isCreated;
        int epoch;
        lock (sync)
        {
            epoch = generation;
            isCreated = createdId is not null && targetId == createdId;
            if (isCreated) snapshot = created ?? throw Stale();
            else
            {
                if (current is null || targetId != currentId || currentIdentity != Identity(current)) throw Stale();
                snapshot = current;
            }
        }
        if (snapshot.BaselineId != baselineId) throw Stale();
        if (snapshot.HasPendingChanges || snapshot.SaveResult?.ErrorCode is not null) throw new AppException("UNSAVED_CHANGES", "请先完成保存或核验后预览日期代码", 3);
        if (ModelAccessPolicy.IsReadOnly(snapshot)) throw new AppException("READ_ONLY_MODEL", "当前远端模型不支持日期配置预览", 3);
        if (isCreated && createdFileIdentity is not null && ProjectFileIdentity.Read(snapshot.ProjectPath) != createdFileIdentity) throw Stale();
        var sameCurrent = current is not null && Identity(current) == Identity(snapshot);
        var source = isCreated && !sameCurrent ? workspace : currentWorkspace ?? workspace;
        var draft = await source.CreateDraftAsync(snapshot, token);
        Exception? primary = null;
        try
        {
            var program = await (draft as ICalendarProgramDraft ?? throw new AppException("UNSUPPORTED", "当前目标不支持日期代码预览", 3))
                .PreviewCalendarAsync(options, locale, token);
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                EnsureEpoch(epoch);
                if (isCreated && (createdId != targetId || created?.BaselineId != baselineId)
                    || !isCreated && (currentId != targetId || currentIdentity != Identity(snapshot))) throw Stale();
            }
            if (isCreated && createdFileIdentity is not null && ProjectFileIdentity.Read(snapshot.ProjectPath) != createdFileIdentity) throw Stale();
            if (isCreated && !sameCurrent && (await files.ReadAsync(snapshot.ProjectPath, token)).BaselineId != baselineId) throw Stale();
            return program;
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposePreviewAsync(draft, primary); }
    }

    /// <summary>只接受实际创建分发器返回的已核验回执; 网页无法直接调用此方法.</summary>
    public void BindCreated(ScaffoldResult result)
    {
        if (!result.ProjectCreated) return;
        if (!result.Verified)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposal is not null, this);
                RetireOwnedEdits();
                createdTargetEdits = null;
                created = null; createdPath = null; createdId = null; createdBaseline = null; createdFileIdentity = null;
            }
            return;
        }
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (createdEdits?.HasPendingChanges == true) throw new AppException("UNSAVED_CHANGES", "已创建工程的保存尚未完成", 3);
            RetireOwnedEdits();
            createdTargetEdits = null;
            created = null;
            createdPath = result.ProjectPath;
            createdBaseline = result.BaselineId;
            try { createdFileIdentity = ProjectFileIdentity.Read(result.ProjectPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException)
            {
                // 已发布事实不能被后续上下文建立失败抹去; 不发放继续写入的身份.
                createdId = null; createdPath = null; createdBaseline = null; createdFileIdentity = null;
                return;
            }
            createdId = Guid.NewGuid().ToString("N");
        }
    }
    private string? createdPath;

    /// <summary>仅供同一宿主分发选择当前模型读取路径, 不返回磁盘路径.</summary>
    public bool IsCreatedTargetId(string id) { lock (sync) return createdId is not null && createdId == id; }

    /// <summary>只在已经绑定的同一离线入口时读取当前模型, 避免无关远端连接参与创建目标操作.</summary>
    public bool SharesCurrentPath(string path) { lock (sync) return createdPath is not null && createdPath.Equals(path, StringComparison.OrdinalIgnoreCase); }

    /// <summary>同文件在当前模型通道保存后同步已创建上下文, 不把普通刷新当保存授权.</summary>
    public void ObserveSaved(ProjectSnapshot snapshot)
    {
        lock (sync)
        {
            if (!SharesCurrentPath(snapshot.ProjectPath)) return;
            created = snapshot; createdBaseline = snapshot.BaselineId;
            if (!snapshot.HasPendingChanges && snapshot.SaveResult?.ErrorCode is null) createdTargetEdits = null;
        }
    }

    /// <summary>发布新工程之前阻止丢失旧工程的待保存回执.</summary>
    public void RequireCreatedSaved()
    {
        if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "已创建工程的保存尚未完成", 3);
    }

    /// <summary>读取当前宿主目标, 同目标刷新保持身份; 换来源或文档必须重新取得上下文.</summary>
    public async Task<ModelConfigurationContext> ContextAsync(string target, ProjectSnapshot? current, CancellationToken token)
    {
        if (target == "created")
        {
            string path, identity;
            int epoch;
            lock (sync) { path = createdPath ?? throw Missing(); identity = createdId ?? throw Missing(); epoch = generation; }
            var pendingEdits = createdTargetEdits ?? createdEdits;
            var snapshot = pendingEdits?.HasPendingChanges == true ? await pendingEdits.ReadAsync(path, token) : await files.ReadAsync(path, token);
            var targetPending = snapshot.HasPendingChanges || snapshot.SaveResult?.ErrorCode is not null;
            lock (sync)
            {
                EnsureEpoch(epoch);
                // 其他工程的草稿不能解除当前目标基线门禁; 入口对象身份在待保存期间也必须保持.
                if (createdId != identity || !targetPending && snapshot.BaselineId != createdBaseline
                    || createdFileIdentity is not null && ProjectFileIdentity.Read(path) != createdFileIdentity) throw Stale();
                created = snapshot;
            }
            var assets = await ReadMeasureAssetsAsync(snapshot, workspace, token, display: true);
            lock (sync) EnsureEpoch(epoch);
            return ModelConfigurationContext.Create(identity, snapshot, true, assets);
        }
        if (target != "current") throw ModelConfigurationParameters.Invalid();
        if (current is null) throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        int currentEpoch;
        lock (sync) currentEpoch = generation;
        token.ThrowIfCancellationRequested();
        var measureAssets = await ReadMeasureAssetsAsync(current, currentWorkspace ?? workspace, token, display: true);
        lock (sync)
        {
            EnsureEpoch(currentEpoch);
            var identity = Identity(current);
            if (identity != currentIdentity) { currentIdentity = identity; currentId = Guid.NewGuid().ToString("N"); }
            return ModelConfigurationContext.Create(currentId!, current, false, measureAssets);
        }
    }

    /// <summary>严格解析单个功能配置, 准备后只提交一次已冻结的变更, 取消与待核验遵守原保存边界.</summary>
    /// <param name="parameters">当前宿主目标与基线绑定的单段配置, 网页升级许可不作为授权.</param>
    /// <param name="current">宿主拥有的当前权威模型快照.</param>
    /// <param name="currentEdits">当前模型共享编辑与保存服务.</param>
    /// <param name="token">提交前准备与确认的取消令牌.</param>
    /// <param name="committing">进入不可重放提交边界时的宿主回调.</param>
    /// <param name="silentMcp">宿主核验的后台请求, 兼容升级由精确请求字段表达, 不打开页面确认.</param>
    /// <returns>保存后的真实目标配置上下文, 不将模型提交等同于文件已保存.</returns>
    /// <exception cref="AppException">目标、基线、权限、引擎能力或独立确认未通过, 或提交结果需核验.</exception>
    public async Task<ModelConfigurationContext> ApplyAsync(JsonElement parameters, ProjectSnapshot? current, ModelEditService? currentEdits,
        CancellationToken token, Action? committing = null, bool silentMcp = false)
    {
        var input = ModelConfigurationParameters.Parse(parameters);
        ProjectSnapshot snapshot;
        bool isCreated;
        int epoch;
        lock (sync)
        {
            epoch = generation;
            isCreated = input.TargetId == createdId && createdId is not null;
            if (isCreated) snapshot = created ?? throw Stale();
            else
            {
                if (input.TargetId != currentId || current is null || currentIdentity != Identity(current)) throw Stale();
                snapshot = current;
            }
        }
        if (ModelAccessPolicy.IsReadOnly(snapshot)) throw new AppException("READ_ONLY_MODEL", "当前远端模型只读", 3);
        if (snapshot.BaselineId != input.BaselineId) throw new AppException("STALE_BASELINE", "模型已变化, 请重新读取配置目标", 3);
        if (snapshot.HasPendingChanges || snapshot.SaveResult?.ErrorCode is not null) throw new AppException("UNSAVED_CHANGES", "上次保存未完成, 请先完成保存或核验", 3);
        ModelConfigurationParameters.ValidateSection(input);
        var sameCurrent = current is not null && Identity(current) == Identity(snapshot);
        var targetEdits = currentEdits;
        if (isCreated && !sameCurrent)
        {
            if (createdFileIdentity is not null && ProjectFileIdentity.Read(snapshot.ProjectPath) != createdFileIdentity) throw Stale();
            if (verifyCreatedWritable is not null) await verifyCreatedWritable(snapshot, token);
            var fresh = await files.ReadAsync(snapshot.ProjectPath, token);
            if (fresh.BaselineId != snapshot.BaselineId || fresh.ProjectId != snapshot.ProjectId) throw new AppException("STALE_BASELINE", "工程已被外部修改, 未写入", 3);
            createdEdits ??= new(files, workspace);
            if (createdEdits.HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "已创建工程的保存尚未完成", 3);
            createdEdits.Select(fresh);
            targetEdits = createdEdits;
        }
        if (targetEdits is null) throw new AppException("UNSUPPORTED", "当前宿主不支持模型配置写入", 3);
        lock (sync) EnsureEpoch(epoch);
        var request = input.Request with { AllowCompatibilityUpgrade = false };
        if (request.Udfs is { Length: > 0 } && snapshot.Connection?.Kind == "desktop" && snapshot.Model.CompatibilityLevel < 1702)
        {
            // 已满足模型级别直接通过; 低模型才核实绑定 Desktop 的版本和本次升级能力.
            var capability = currentWorkspace as IModelCompatibilityUpgradeWorkspace
                ?? throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "无法核实当前 Desktop 引擎的 UDF 保存能力, 请重新连接并更新 Power BI Desktop", 3);
            await capability.VerifyCompatibilityUpgradeAsync(snapshot, 1702, token);
            token.ThrowIfCancellationRequested();
            lock (sync) EnsureEpoch(epoch);
        }
        if (request.Udfs is { Length: > 0 } && snapshot.Model.CompatibilityLevel < 1702)
        {
            // 独立确认同时适用于本地文件和已核实的新引擎, 不由普通保存偏好代替.
            if (silentMcp && !input.Request.AllowCompatibilityUpgrade)
                throw new AppException("COMPATIBILITY_UPGRADE_REQUIRED", "此操作需要提升模型兼容级别; 请在精确请求中明确设置 allowCompatibilityUpgrade", 3);
            if (!silentMcp && (confirmUpgrade is null || !await confirmUpgrade(snapshot, 1702, token)))
                throw new AppException("CANCELLED", "未确认兼容级别提升, 未写入模型", 130);
            token.ThrowIfCancellationRequested();
            lock (sync) EnsureEpoch(epoch);
            request = request with { AllowCompatibilityUpgrade = true };
        }
        var sourceWorkspace = isCreated && !sameCurrent ? workspace : currentWorkspace ?? workspace;
        // 相邻段也读取独立展示来源, 避免成功响应把“本轮未处理模板”伪装成目录失效.
        // 非 measures 的 compiler 会丢弃该上下文, 不将 JSON 修订绑定到日期、UDF 或样例计划.
        var assetCapability = await ReadMeasureAssetsAsync(snapshot, sourceWorkspace, token, display: request.Measures is null);
        token.ThrowIfCancellationRequested();
        lock (sync) EnsureEpoch(epoch);
        // 配置文件可编辑, 但旧草稿不能借本轮新读到的修订覆盖用户在其他窗口或编辑器中的新内容.
        if (request.Measures is not null && assetCapability?.CanUseMeasureConfig == true
            && request.MeasureConfigurationRevision != assetCapability.ConfigurationRevision)
            throw new AppException("STALE_CONFIGURATION_SOURCE", "模板配置文件已变化, 请重新读取配置后再写入", 3);
        var prepared = await compiler.PrepareAsync(request, snapshot, token, assetCapability);
        var commitStarted = false;
        try
        {
            // 仅进入真实编辑阶段后绑定恢复服务, 准备失败和升级取消不留下可切到其他工程的别名.
            token.ThrowIfCancellationRequested();
            lock (sync) { EnsureEpoch(epoch); if (isCreated) createdTargetEdits = targetEdits; }
            var saved = await targetEdits.EditConfigurationAndSaveAsync(prepared, token,
                () => { committing?.Invoke(); commitStarted = true; }, async ct =>
            {
                lock (sync) EnsureEpoch(epoch);
                if (isCreated && createdFileIdentity is not null && ProjectFileIdentity.Read(snapshot.ProjectPath) != createdFileIdentity) throw Stale();
                await compiler.VerifyAsync(prepared, ct);
                if (isCreated && !sameCurrent && verifyCreatedWritable is not null) await verifyCreatedWritable(snapshot, ct);
            });
            // 只规范化配置 JSON 也会真实提交, 模型指纹可能不变; 以本轮提交边界而非指纹判断是否仍可取消.
            var noChanges = !commitStarted && saved.BaselineId == snapshot.BaselineId;
            lock (sync)
            {
                // 无变化路径不会经过 beforeCommit, 仍需拒绝已撤销目标或取消的准备结果.
                // 已完成的真实提交继续排空回执, 但不能把旧目标重新绑定到新文档.
                if (noChanges) { token.ThrowIfCancellationRequested(); EnsureEpoch(epoch); }
                if (isCreated && generation == epoch) { created = saved; createdBaseline = saved.BaselineId; if (!targetEdits.HasPendingChanges) createdTargetEdits = null; }
            }
            // 成功提交后的展示事实来自已核验冻结回执, 不再发新的文件/引擎请求把成功误报成失败.
            // 外部编辑发生在提交后时, 客户端仍保留本轮 AfterHash, 下一次读取会发现新修订而不是覆盖它.
            var savedAssets = AppliedMeasureAssets(assetCapability, prepared.Configuration!);
            return ModelConfigurationContext.Create(input.TargetId, saved, isCreated, savedAssets) with { NoChanges = noChanges };
        }
        finally
        {
            if (!targetEdits.HasPendingChanges)
            {
                lock (sync) if (isCreated && ReferenceEquals(createdTargetEdits, targetEdits)) createdTargetEdits = null;
                compiler.Release(prepared);
            }
        }
    }

    /// <summary>只完成既有待保存草稿或明确丢弃内存修改, 不重新准备或应用配置.</summary>
    public async Task<ModelConfigurationContext> RecoverAsync(JsonElement parameters, bool discard, ProjectSnapshot? current,
        ModelEditService? currentEdits, CancellationToken token, Action? committing = null)
    {
        var input = ModelConfigurationParameters.Recovery(parameters);
        var isCreated = IsCreatedTargetId(input.TargetId);
        if (!isCreated && (current is null || input.TargetId != currentId || currentIdentity != Identity(current))) throw Stale();
        var service = isCreated ? createdTargetEdits ?? createdEdits : currentEdits;
        var path = isCreated ? createdPath : current?.ProjectPath;
        if (service is null || path is null) throw Stale();
        var pending = await service.ReadAsync(path, token);
        if (pending.BaselineId != input.BaselineId) throw new AppException("STALE_BASELINE", "待保存状态已变化, 请重新读取", 3);
        if (!discard && isCreated && verifyCreatedWritable is not null) await verifyCreatedWritable(pending, token);
        var result = discard ? await service.DiscardAsync(token)
            : await service.SaveAsync(token, committing);
        if (isCreated) { created = result; createdBaseline = result.BaselineId; if (!service.HasPendingChanges) createdTargetEdits = null; }
        // 恢复结果已权威回读; 不在成功后追加可失败/取消的文件探测, 来源由下次独立读取或写入预检核实.
        return ModelConfigurationContext.Create(input.TargetId, result, isCreated, measureSourceRead: false);
    }

    /// <summary>source 只来自宿主已绑定的工作区; 返回只读文件配置事实, 不从后缀发权限, display 仅将归属错误降为不可用而不吞身份变化.</summary>
    private async Task<ModelConfigurationAssetContext?> ReadMeasureAssetsAsync(ProjectSnapshot snapshot, IModelEditWorkspace source,
        CancellationToken token, bool display = false)
    {
        if (snapshot.HasPendingChanges || snapshot.SaveResult?.ErrorCode is not null || ModelAccessPolicy.IsReadOnly(snapshot)) return null;
        try
        {
            if (source is IModelConfigurationAssetWorkspace authority) return await authority.GetConfigurationAssetsAsync(snapshot, token);
            // 离线/created 入口必须是宿主绑定的具体本机文件, 配置编译器核实根和完整基线; 不要求预建目录.
            if (Path.GetExtension(snapshot.ProjectPath).Equals(".pbip", StringComparison.OrdinalIgnoreCase)
                && File.Exists(snapshot.ProjectPath)) return await compiler.ReadAssetContextAsync(snapshot, token);
            return null;
        }
        catch (AppException error) when (display && error.Code is "CONFIGURATION_OBJECT_CONFLICT" or "CONFIGURATION_OWNERSHIP_INVALID")
        { return null; }
    }

    /// <summary>以 plan 实际模式和已提交资产摘要投影来源, 返回本轮写入后修订; source 不可用时仍不可发放 Config 能力.</summary>
    private static ModelConfigurationAssetContext? AppliedMeasureAssets(ModelConfigurationAssetContext? source, ModelConfigurationPlan plan)
    {
        if (source?.CanUseMeasureConfig != true || plan.Request.Measures is null) return source;
        var asset = plan.Assets.SingleOrDefault(item => item.RelativePath == "pbi-utils/config.json");
        var revision = asset is null ? source.ConfigurationRevision : asset.AfterHash ?? "missing";
        var configuration = source.ExistingConfiguration;
        if (plan.Request.MeasuresMode == "config") configuration = new("config", plan.Request.Measures);
        // TMDL 只移除本功能段; 文件因自定义段保留时, 其真实 Config 来源是空集合而非文件缺失.
        else if (asset is not null) configuration = asset.AfterHash is null ? null : new("config", []);
        return source with { ConfigurationRevision = revision, ExistingConfiguration = configuration };
    }

    /// <summary>完整目标身份不依赖名称, 大小写仅针对宿主固定路径.</summary>
    private static string Identity(ProjectSnapshot snapshot) => snapshot.ProjectId + "|" + snapshot.ProjectPath.ToUpperInvariant();
    /// <summary>文档变化撤销所有句柄; 已保存项目不会被删除.</summary>
    public void Invalidate() { lock (sync) { generation++; currentId = null; currentIdentity = null; createdId = null; createdPath = null; created = null; createdBaseline = null; createdFileIdentity = null; } }
    /// <summary>提交前复核文档仍持有此目标.</summary>
    private void EnsureEpoch(int epoch) { if (disposal is not null || generation != epoch) throw Stale(); }
    private static AppException Stale() => new("STALE_CONFIGURATION_TARGET", "配置目标已失效, 请重新读取", 3);
    private static AppException Missing() => new("CREATED_PROJECT_NOT_AVAILABLE", "请先创建并核验工程", 3);

    /// <summary>结束临时只读会话; 清理失败保留 owner 供关闭重试, 不覆盖预览的校验或取消首因.</summary>
    /// <param name="owned">本次预览独占创建的完整模型上下文.</param>
    /// <param name="primary">主体失败首因, null 表示预览主体已经成功.</param>
    /// <exception cref="Exception">主体成功但清理失败时传播实际清理异常, 不返回未确认释放的成功.</exception>
    private async Task DisposePreviewAsync(IModelDraft owned, Exception? primary)
    {
        var cleanup = Task.Run(async () => await owned.DisposeAsync());
        try { await cleanup; }
        catch
        {
            lock (sync) ownedDisposals.Add(new(owned, cleanup));
            if (primary is null) throw;
            primary.Data["ModelDraftCleanupFailed"] = true;
        }
    }

    /// <summary>在持有配置锁时转交自建服务的清理任务, 成功任务可回收, 未完成或失败任务留待关闭排空.</summary>
    /// <remarks>createdTargetEdits 可能只是 currentEdits 的借用别名, 不能在此单独释放.</remarks>
    private void RetireOwnedEdits()
    {
        if (createdEdits is not { } owned) return;
        createdEdits = null;
        ownedDisposals.RemoveAll(item => item.Completion.IsCompletedSuccessfully);
        ownedDisposals.Add(new(owned, Task.Run(async () => await owned.DisposeAsync())));
    }

    /// <summary>调用方排空请求后撤销身份并启动 owned 服务清理, 同步入口不执行 SDK 或文件重 I/O.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposal is not null && (!disposal.IsCompleted || disposal.IsCompletedSuccessfully)) return;
            // 只重试此前请求或关闭已失败的清理; 本轮新启动任务即使很快失败也不自动重试.
            for (var index = 0; index < ownedDisposals.Count; index++)
            {
                var item = ownedDisposals[index];
                if (!item.Completion.IsCompleted || item.Completion.IsCompletedSuccessfully) continue;
                _ = item.Completion.Exception;
                ownedDisposals[index] = item with { Completion = Task.Run(async () => await item.Owner.DisposeAsync()) };
            }
            if (disposal is null)
            {
                Invalidate();
                RetireOwnedEdits();
                createdTargetEdits = null;
            }
            disposal = Task.WhenAll(ownedDisposals.Select(item => item.Completion));
        }
    }

    /// <summary>排空当前及已撤销的自建服务; 失败后显式再次调用只重试未确认 owner, 不释放借用服务.</summary>
    /// <returns>本次显式释放的唯一完成任务, 失败不会丢弃已启动的其他清理.</returns>
    public ValueTask DisposeAsync() { Dispose(); lock (sync) return new(disposal!); }
}
