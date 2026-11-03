// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\DesktopModelEditing.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅对系统发现的 Desktop 模型暂存与直接提交, 远端注册不能写入.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Models.Configuration;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader
{
    private readonly Dictionary<string, DesktopSyncTicket> pendingFileSaves = [];

    /// <summary>重试只完成同步和文件保存, 不重复提交已成功的模型编辑.</summary>
    public async Task<ProjectSnapshot> CompleteSaveAsync(ProjectSnapshot saved, Action? committing, CancellationToken token)
    {
        if (!pendingFileSaves.TryGetValue(saved.ProjectPath, out var ticket)) throw new AppException("NO_PENDING_SAVE", "没有可重试的文件保存, 请重新读取模型", 3);
        await using var visibility = await BeginDesktopSyncAsync(ticket);
        var current = await ReadAsync(saved.ProjectPath, token);
        if (current.BaselineId != saved.BaselineId) throw new AppException("STALE_BASELINE", "模型已在外部变化, 请放弃待处理状态后重新读取", 3);
        token.ThrowIfCancellationRequested(); committing?.Invoke();
        return await FinishFileSaveAsync(saved, ticket);
    }

    /// <summary>提交后任何同步错误都返回部分成功, 已提交操作不再留作可重复执行的草稿.</summary>
    /// <param name="saved">本轮已提交的模型快照, 保存失败不能退回可重放草稿.</param>
    /// <param name="ticket">原目标及已经证明的文件/重载阶段, 尾部失败仍保留供只补保存.</param>
    /// <returns>包含真实三阶段成功或部分成功事实的模型快照.</returns>
    private async Task<ProjectSnapshot> FinishFileSaveAsync(ProjectSnapshot saved, DesktopSyncTicket ticket)
    {
        pendingFileSaves[saved.ProjectPath] = ticket;
        using var operation = BusinessLog.Begin(log, "DesktopFile.SyncAndSave");
        ModelSaveResult result;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            result = await DesktopFileSync.CompleteAsync(ticket, saved, deadline.Token);
            // Desktop 重载/保存可能规范化定义, 下一轮必须使用保存后的完整基线.
            saved = await ReadAsync(saved.ProjectPath, CancellationToken.None);
            pendingFileSaves.Remove(saved.ProjectPath);
            operation.Complete();
        }
        catch (Exception e)
        {
            operation.Fail(e);
            var code = e is AppException app ? app.Code : "DESKTOP_SYNC_FAILED";
            var message = e is AppException known ? known.Message : "Desktop 同步或文件保存失败, 请检查 Desktop 窗口后重试";
            result = new(true, ticket.FileSaved, ticket.Reloaded, "模型修改已保存；" + message, code);
        }
        return saved with { HasPendingChanges = false, SaveResult = result };
    }

    /// <summary>PBIP 保存前检查打开窗口中的未保存内容; 文件落盘后自动重载对应工程.</summary>
    /// <param name="baseline">当前已核验的工程基线.</param>
    /// <param name="draft">共享文件通道拥有的完整私有草稿.</param>
    /// <param name="committing">共享文件提交边界的宿主回调.</param>
    /// <param name="token">文件提交前可取消的准备令牌.</param>
    /// <returns>文件提交成功后包含 Desktop 尾部事实的快照.</returns>
    /// <exception cref="AppException">提交前的身份、挂起查询、基线或文件错误; 提交后错误转为部分保存事实.</exception>
    private async Task<ProjectSnapshot> SavePbipAsync(ProjectSnapshot baseline, IModelDraft draft, Action? committing, CancellationToken token)
    {
        var ticket = await DesktopFileSync.PreparePbipAsync(baseline, token);
        await using var visibility = await BeginDesktopSyncAsync(ticket);
        var saved = await draft.SaveAsync(committing, token);
        ticket.FileSaved = true;
        return await FinishFileSaveAsync(saved, ticket);
    }

    /// <summary>在提交前建立宿主可见性保护, 覆盖 TOM 事件引发的 Desktop 激活及其后保存尾部.</summary>
    /// <param name="ticket">已核实的 Desktop 文件票据, 未打开的离线 PBIP 不创建窗口作用域.</param>
    /// <returns>由调用方在提交、同步及权威回读排空后释放的可选作用域; CLI 不提供窗口回调.</returns>
    private async Task<IAsyncDisposable?> BeginDesktopSyncAsync(DesktopSyncTicket ticket) =>
        ticket.Target is { } target && beginDesktopSync is not null ? await beginDesktopSync(target) : null;

    /// <summary>在线模型同步仅绑定进程启动身份, 未保存报表或不可用文件 Bridge 不阻止模型提交.</summary>
    /// <param name="target">本轮重新发现并核实的 Desktop 进程启动身份.</param>
    /// <returns>覆盖提交及权威回读的可选宿主焦点作用域.</returns>
    private async Task<IAsyncDisposable?> BeginDesktopSyncAsync(DesktopProcessTarget target) =>
        beginDesktopSync is not null ? await beginDesktopSync(target) : null;

    /// <summary>包装文件草稿只扩展保存尾部, 保留共享编辑和原子恢复逻辑.</summary>
    private sealed class SynchronizedDraft(IModelDraft inner, Func<IModelDraft, CancellationToken, Action?, Task<ProjectSnapshot>> save) : IModelDraft, IUdfLintDraft, PbiUtils.Application.Models.Configuration.ICalendarProgramDraft
    {
        public ProjectSnapshot Snapshot => inner.Snapshot;
        public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => inner.ApplyAsync(request, token);
        public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => save(inner, token, committing);
        public void Dispose() => inner.Dispose();
        /// <summary>保留底层完整私有 TOM 的只读函数分析, 不进入文件同步.</summary>
        public Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token) =>
            (inner as IUdfLintDraft ?? throw new AppException("UNSUPPORTED", "当前目标不支持完整函数分析", 3)).AnalyzeUdfsAsync(token);
        /// <summary>文件草稿仍从完整受信模型只读编译, 不进入同步或保存尾部.</summary>
        public Task<PbiUtils.Application.Calendar.ScaffoldCalendarProgram> PreviewCalendarAsync(
            PbiUtils.Application.Calendar.ScaffoldCalendarOptions options, string locale, CancellationToken token) =>
            (inner as PbiUtils.Application.Models.Configuration.ICalendarProgramDraft
                ?? throw new AppException("UNSUPPORTED", "当前目标不支持日期代码预览", 3)).PreviewCalendarAsync(options, locale, token);
    }
    /// <summary>报告名来自已发现窗口标题, 不把引擎随机数据库 ID 当作文件名.</summary>
    public static string DesktopModelName(string title)
    {
        const string suffix = " - Power BI Desktop";
        var name = title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title[..^suffix.Length].Trim().TrimEnd('*').Trim() : title.Trim();
        return name.Length == 0 || name.Equals("Power BI Desktop", StringComparison.OrdinalIgnoreCase) ? "未命名报表" : name;
    }
    /// <summary>窗口标题可能在保存时改变, 身份校验始终绑定 PID、引擎启动时间和端口.</summary>
    private static bool SameInstance(PowerBiEndpoint a, PowerBiEndpoint b) => a.ProcessId == b.ProcessId && a.EngineProcessId == b.EngineProcessId && a.EngineStarted == b.EngineStarted && a.Port == b.Port;
    /// <summary>离线编辑交给文件适配器; Desktop 独占原 TOM 会话并在原 Database 校验, 不复制模型.</summary>
    public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
    {
        if (!baseline.ProjectPath.StartsWith("model://", StringComparison.Ordinal))
        {
            var draft = await (fileEdits ?? throw new AppException("UNSUPPORTED", "文件编辑不可用", 3)).CreateDraftAsync(baseline, token);
            return new SynchronizedDraft(draft, (inner, ct, committing) => SavePbipAsync(baseline, inner, committing, ct));
        }
        var target = await DesktopTargetAsync(baseline.ProjectPath, token);
        var assetTransactionRoot = Path.Combine(RuntimeEnvironment.UserDataDirectory(), "transactions", "desktop", baseline.ProjectId);
        if (DesktopConfigurationAssetTransaction.HasPendingTransactions(assetTransactionRoot))
        {
            var assetBinding = await ReadConfigurationBindingAsync(target, token);
            DesktopConfigurationAssetTransaction.VerifyPending(assetTransactionRoot, baseline.BaselineId, assetBinding?.Root);
        }
        return await Task.Run<IModelDraft>(() =>
        {
            var server = ConnectDesktop(target);
            try
            {
                var db = DesktopDatabase(server);
                AssertDesktopBaseline(db, baseline, target);
                token.ThrowIfCancellationRequested();
                return new DesktopModelDraft(baseline, db, server, () => server.BeginTransaction(), () => server.RollbackTransaction(),
                    (edits, committing, committed, ct) => SaveDesktopAsync(baseline, db, server, edits, committing, committed, ct));
            }
            catch { server.Dispose(); throw; }
        }, token);
    }
    /// <summary>只读复核原模型; 已达 1702 时直接通过, 低级别再核绑定 Desktop 的 GA 版本和升级能力.</summary>
    /// <param name="baseline">原目标身份和完整模型基线.</param>
    /// <param name="compatibilityLevel">本次仅支持核验 UDF 所需的 1702.</param>
    /// <param name="token">发现、连接和 SDK 调用边界的取消令牌.</param>
    /// <exception cref="AppException">来源非 Desktop、目标或基线变化、能力未知或不支持时拒绝.</exception>
    public async Task VerifyCompatibilityUpgradeAsync(ProjectSnapshot baseline, int compatibilityLevel, CancellationToken token)
    {
        if (baseline.Connection?.Kind != "desktop") throw new AppException("READ_ONLY_MODEL", "当前来源不是本地 Desktop, 不允许写入", 3);
        var target = await DesktopTargetAsync(baseline.ProjectPath, token);
        using var process = Process.GetProcessById(target.Endpoint!.ProcessId);
        var processTarget = new DesktopProcessTarget(process.Id, process.StartTime.ToUniversalTime());
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var server = ConnectDesktop(target);
            var database = DesktopDatabase(server);
            AssertDesktopBaseline(database, baseline, target);
            RequireDesktopUdfSupport(server, database.CompatibilityLevel, processTarget, compatibilityLevel);
            token.ThrowIfCancellationRequested();
        }, token);
        await DesktopTargetAsync(baseline.ProjectPath, token);
    }

    /// <summary>每次保存重新验证真实本机身份、基线和事务状态; 升档事务同时提交数据库属性与完整模型.</summary>
    /// <param name="baseline">当前宿主绑定的完整模型基线.</param>
    /// <param name="db">专属原连接上的同一 Database, 操作已经在此暂存一次.</param>
    /// <param name="server">拥有本轮已启动事务的同一 TOM 会话.</param>
    /// <param name="edits">已经在原对象校验的精确编辑集合.</param>
    /// <param name="committing">进入不可重放的模型提交边界前调用的宿主回调.</param>
    /// <param name="token">提交前取消令牌; 提交后尾部使用独立完成期限.</param>
    /// <param name="committed">服务器 Commit 确认后通知操作上下文解除事务所有权.</param>
    /// <returns>直接提交原连接数据库后的权威完整回读, 文件持久化由用户在 Desktop 完成.</returns>
    /// <exception cref="AppException">身份或基线未通过时不提交; 提交结果未知时保持待核验.</exception>
    private async Task<ProjectSnapshot> SaveDesktopAsync(ProjectSnapshot baseline, TOM.Database db, TOM.Server server,
        IReadOnlyList<ModelEditRequest> edits, Action? committing, Action committed, CancellationToken token)
    {
        var target = await DesktopTargetAsync(baseline.ProjectPath, token);
        var allowUpgrade = DesktopUdfCompatibility.GetUpgradeApproval(baseline.Model.CompatibilityLevel, db.CompatibilityLevel, edits);
        var hasUdfWrites = DesktopUdfCompatibility.HasUdfWrites(edits);
        using var process = Process.GetProcessById(target.Endpoint!.ProcessId);
        var processTarget = new DesktopProcessTarget(process.Id, process.StartTime.ToUniversalTime());
        // TOM 的 SaveChanges 本身就可能使 Desktop 激活, 不能等已提交后的尾部再判断宿主前台.
        await using var visibility = await BeginDesktopSyncAsync(processTarget);
        var saved = await Task.Run(async () =>
        {
            var transactionRoot = Path.Combine(RuntimeEnvironment.UserDataDirectory(), "transactions", "desktop", baseline.ProjectId);
            UserDataStore.Check(transactionRoot); Directory.CreateDirectory(transactionRoot);
            using var lease = UserDataStore.Lock(Path.Combine(transactionRoot, "save.lock"));
            // 所有模型提交和资产事务共享 guard; 即使草稿早已开启, 也不能穿过另一引擎新产生的未知资产提交.
            using var configurationGuard = await DesktopConfigurationCommitGuard.AcquireAsync(transactionRoot, baseline.BaselineId,
                async ct => (await ReadConfigurationBindingAsync(target, ct))?.Root, token);
            var originalCompatibilityLevel = baseline.Model.CompatibilityLevel;
            var assetPlans = edits.Select(edit => edit.Configuration).OfType<PbiUtils.Application.Models.Configuration.ModelConfigurationPlan>()
                .Where(plan => plan.Assets.Count > 0).ToArray();
            if (assetPlans.Length > 1) throw new AppException("CONFIGURATION_ASSET_CONFLICT", "一次模型提交只能包含一份冻结配置资产计划", 3);
            var assetPlan = assetPlans.SingleOrDefault();
            if (assetPlan is not null && (assetPlan.AssetContext is null || assetPlan.AssetRoot is null
                || !assetPlan.AssetContext.Root.Equals(assetPlan.AssetRoot, StringComparison.OrdinalIgnoreCase)))
                throw new AppException("CONFIGURATION_ASSETS_UNSUPPORTED", "当前模型缺少可核实的本地文件配置资产能力", 3);
            if (assetPlan?.AssetContext is { } assetContext) await assetContext.Verify(token);
            using var assetTransaction = assetPlan is null ? null : new DesktopConfigurationAssetTransaction(assetPlan.AssetRoot!, transactionRoot,
                baseline.BaselineId, ModelConfigurationReadback.Fingerprint(db), assetPlan.Assets, configurationGuard);
            var submissionStarted = false;
            using var operation = BusinessLog.Begin(log, "DesktopModel.Save");
            var transaction = new DesktopModelCommit(db, async ct =>
            {
                await DesktopTargetAsync(baseline.ProjectPath, ct);
                using var currentProcess = Process.GetProcessById(processTarget.ProcessId);
                if (currentProcess.StartTime.ToUniversalTime() != processTarget.Started)
                    throw new AppException("POWER_BI_INSTANCE_CLOSED", "Desktop 实例已关闭或重启, 未保存", 3);
                // 始终使用编辑前级别, 避免本地暂存升档后跳过实际版本和升级能力检查.
                if (hasUdfWrites) RequireDesktopUdfSupport(server, originalCompatibilityLevel, processTarget, 1702);
                if (assetPlan?.AssetContext is { } configurationAssets) await configurationAssets.Verify(ct);
            }, ct => ReadDesktopAuthorityAsync(target, ct),
            () => AcquireDesktopCommitLock(server, db), () =>
            {
                try
                {
                    // Model.SaveChanges 不持久化父 Database 的兼容级别; 已批准升档使用原数据库的完整 Update.
                    if (allowUpgrade) db.Update(Microsoft.AnalysisServices.UpdateOptions.ExpandFull, Microsoft.AnalysisServices.UpdateMode.Update);
                    else db.Model.SaveChanges();
                    server.CommitTransaction();
                    committed();
                }
                catch (Exception)
                {
                    throw new AppException("DESKTOP_SAVE_FAILED", "Desktop 模型提交结果未确认, 请重新读取核验; 不得重复提交", 4);
                }
            }, ct => ReadAsync(baseline.ProjectPath, ct));
            try
            {
                var result = await transaction.SaveAsync(baseline, () =>
                {
                    assetTransaction?.Apply(token);
                    token.ThrowIfCancellationRequested();
                    committing?.Invoke();
                    assetTransaction?.MarkCommitStarted();
                    submissionStarted = true;
                }, token, allowUpgrade);
                try
                {
                    if ((assetTransaction?.Complete() ?? assetTransaction?.CleanupFailure ?? configurationGuard.CleanupFailure) is { } cleanup)
                    {
                        using var cleanupOperation = BusinessLog.Begin(log, "DesktopConfigurationAsset.Cleanup");
                        cleanupOperation.Fail(cleanup);
                        if (!result.Warnings.Contains(DesktopConfigurationAssetTransaction.CleanupWarning, StringComparer.Ordinal))
                            result = result with { Warnings = [.. result.Warnings, DesktopConfigurationAssetTransaction.CleanupWarning] };
                    }
                }
                catch (Exception)
                {
                    throw new AppException("EXECUTION_NEEDS_VERIFICATION", "模型已提交并完整回读, 但配置资产或完成回执未能核实; 请重新读取核验, 不得重复提交", 4);
                }
                operation.Complete();
                return result;
            }
            catch (Exception error)
            {
                if (!submissionStarted)
                {
                    try { assetTransaction?.Rollback(); }
                    catch (Exception)
                    {
                        operation.Fail(error);
                        throw new AppException("EXECUTION_NEEDS_VERIFICATION", "模型尚未提交, 但配置资产恢复未能核实, 请重新读取核验; 不得重复提交", 4);
                    }
                }
                operation.Fail(error); throw;
            }
        }, token);
        if (ChangesCalendarQuery(edits)) saved = saved with { Warnings = [.. saved.Warnings, "日期表数据尚未刷新；请在 Power BI Desktop 显式刷新对应日期表。"] };
        return edits.Any(edit => edit.Configuration?.Request.MeasuresMode == "config" && edit.Configuration.Request.Measures is not null)
            ? saved with { Warnings = [.. saved.Warnings, "模板配置表数据尚未刷新；请在 Power BI Desktop 显式刷新对应配置表。"] } : saved;
    }

    /// <summary>同一原会话的既有事务取得提交共享锁, 允许独立权威读取并阻止外部写提交直到本事务结束.</summary>
    /// <param name="server">原对象操作独占且已 BeginTransaction 的 TOM 会话, 不新开或重启事务.</param>
    /// <param name="database">该会话直接读取并已暂存的原 Database, 只用其受信 ID 定位数据库.</param>
    /// <exception cref="AppException">SDK 异常或 XMLA 返回任何错误时拒绝提交, 不降级为无锁写入.</exception>
    private static void AcquireDesktopCommitLock(TOM.Server server, TOM.Database database)
    {
        try
        {
            XNamespace engine = "http://schemas.microsoft.com/analysisservices/2003/engine";
            var command = new XElement(engine + "Lock", new XElement(engine + "ID", Guid.NewGuid().ToString("D")),
                new XElement(engine + "Object", new XElement(engine + "DatabaseID", database.ID)), new XElement(engine + "Mode", "CommitShared"));
            // Execute 也可能只返回 XmlaError 而不抛异常, 未确认取得锁不能继续保存.
            if (server.Execute(command.ToString(SaveOptions.DisableFormatting)).ContainsErrors) throw LockFailed();
        }
        catch (Exception) { throw LockFailed(); }

        static AppException LockFailed() => new("DESKTOP_MODEL_LOCK_FAILED", "无法锁定当前 Desktop 模型, 未提交; 请重新读取后重试", 4);
    }

    /// <summary>以另一只读连接读取真实服务器, 不受原写会话 dirty 状态或事务快照影响.</summary>
    /// <param name="target">宿主绑定并重新发现的原 Desktop 引擎身份.</param>
    /// <param name="token">连接、同步 SDK 与序列化边界的取消令牌.</param>
    /// <returns>真实数据库身份、完整可写基线, 不创建第二操作模型.</returns>
    private static Task<DesktopModelAuthority> ReadDesktopAuthorityAsync(Target target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var authority = ConnectDesktop(target);
        var database = DesktopDatabase(authority);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Server + "|" + database.ID + "|" + target.Endpoint!.EngineStarted + "|")));
        var result = new DesktopModelAuthority(identity, DesktopDefinitionFingerprint(database));
        token.ThrowIfCancellationRequested();
        return result;
    }, token);

    /// <summary>模型已兼容时不读取版本或支持集合; 低级别先核原 Desktop 产品版本, 再核实际引擎升级支持.</summary>
    /// <param name="server">仅由宿主连接到已核验 Desktop 的 TOM 会话.</param>
    /// <param name="currentLevel">保存前原模型实际级别, 不能使用已升级候选的级别.</param>
    /// <param name="target">原 Desktop 进程与启动身份, 不读取默认安装或网页版本字符串.</param>
    /// <param name="compatibilityLevel">本次 UDF 目标级别.</param>
    /// <exception cref="AppException">版本未知或不足、查询失败、声明不包含精确升级目标时拒绝写入.</exception>
    private static void RequireDesktopUdfSupport(TOM.Server server, int currentLevel, DesktopProcessTarget target, int compatibilityLevel)
    {
        try
        {
            if (compatibilityLevel != 1702) DesktopUdfCompatibility.RequireSupportedLevel([], compatibilityLevel);
            DesktopUdfCompatibility.RequireSupportedEnvironment(currentLevel, () => ReadDesktopProductVersion(target), server.GetSupportedCompatibilityLevels);
        }
        catch (AppException) { throw; }
        catch (Exception) { throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "无法核实当前 Desktop 引擎的兼容级别支持, 未修改模型", 3); }
    }

    /// <summary>从绑定的原 Desktop 进程主模块 ProductVersion 文本解析四段数值, 读不到或身份未知时不猜测.</summary>
    /// <param name="target">宿主从真实已发现实例固定的进程 PID 和启动时间.</param>
    /// <returns>实际主模块 ProductVersion 开头的四段版本, 只允许正式显示后缀; 不使用可能全零的 ProductParts 或 FileVersion.</returns>
    /// <exception cref="AppException">进程重启、替换、跨会话或不再是 Desktop 时拒绝沿用旧身份.</exception>
    private static Version? ReadDesktopProductVersion(DesktopProcessTarget target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            using var self = Process.GetCurrentProcess();
            if (process.ProcessName != "PBIDesktop" || process.SessionId != self.SessionId || process.StartTime.ToUniversalTime() != target.Started)
                throw new AppException("POWER_BI_INSTANCE_CLOSED", "Desktop 实例已关闭或重启, 未保存", 3);
            var metadata = process.MainModule?.FileVersionInfo;
            if (metadata?.OriginalFilename?.Equals("PBIDesktop.exe", StringComparison.OrdinalIgnoreCase) != true
                || metadata.CompanyName != "Microsoft Corporation" || metadata.ProductName != "Microsoft Power BI Desktop") return null;
            var version = DesktopUdfCompatibility.ParseDesktopProductVersion(metadata.ProductVersion);
            using var current = Process.GetProcessById(target.ProcessId);
            if (current.HasExited || current.StartTime.ToUniversalTime() != target.Started)
                throw new AppException("POWER_BI_INSTANCE_CLOSED", "Desktop 实例已关闭或重启, 未保存", 3);
            return version;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    /// <summary>判断本次编辑是否更改日期查询定义, 用于提交后提示用户显式刷新日期表数据.</summary>
    /// <param name="edits">已批准且在原连接模型完成提交的编辑集合.</param>
    /// <returns>包含日期表创建或日期配置输出时为 true, 不触发数据刷新.</returns>
    private static bool ChangesCalendarQuery(IReadOnlyList<ModelEditRequest> edits) => edits.Any(edit => edit.Operation == "calendar.create"
        || edit.Operation == "configuration.apply" && edit.Configuration is { } plan
        && (plan.Request.Calendar is not null || plan.DerivedCalendars.Count != 0 || plan.CalendarOutputs.Count != 0));
    /// <summary>远端或失效句柄不能进入 Desktop 写通道.</summary>
    private async Task<Target> DesktopTargetAsync(string id, CancellationToken token)
    {
        if (!targets.TryGetValue(id, out var target) || target.Endpoint is not { } endpoint) throw new AppException("READ_ONLY_MODEL", "当前来源不是本地 Desktop, 不允许写入", 3);
        if (!(await discovery.DiscoverAsync(token)).Any(e => SameInstance(e, endpoint))) throw new AppException("POWER_BI_INSTANCE_CLOSED", "Desktop 实例已关闭或重启, 未保存", 3);
        return target;
    }
    /// <summary>只使用已发现的回环地址, 短连接超时, 不接受任意地址或凭据.</summary>
    private static TOM.Server ConnectDesktop(Target target)
    {
        var server = new TOM.Server();
        try { server.Connect($"Data Source=localhost:{target.Endpoint!.Port};Connect Timeout=3;Timeout=10;Application Name=pbi-utils;Persist Security Info=false"); return server; }
        catch { server.Dispose(); throw new AppException("POWER_BI_CONNECTION_FAILED", "无法连接当前 Desktop 模型", 3); }
    }
    /// <summary>实例中必须存在唯一模型, 不猜测写入目标.</summary>
    private static TOM.Database DesktopDatabase(TOM.Server server) => server.Databases.Count == 1 ? server.Databases[0] : throw new AppException("MODEL_NOT_UNIQUE", "Desktop 模型不唯一, 未保存", 3);
    /// <summary>比较模型结构基线, 外部变更必须重新读取, 不静默覆盖.</summary>
    private static void AssertDesktopBaseline(TOM.Database db, ProjectSnapshot baseline, Target target)
    {
        var hash = DesktopDefinitionFingerprint(db);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Server + "|" + db.ID + "|" + target.Endpoint!.EngineStarted + "|")));
        if (identity != baseline.ProjectId || hash != baseline.BaselineId) throw new AppException("STALE_BASELINE", "Desktop 模型在编辑期间发生变化, 未覆盖; 请放弃修改后重新读取", 3);
    }
    /// <summary>完整定义指纹包括分区与计算列表达式, 不回传数据源或原始定义.</summary>
    private static string DesktopDefinitionFingerprint(TOM.Database db) => ModelConfigurationReadback.Fingerprint(db);
}
