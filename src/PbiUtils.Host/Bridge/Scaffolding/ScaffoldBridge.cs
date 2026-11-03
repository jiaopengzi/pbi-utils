// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Scaffolding\ScaffoldBridge.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定新建目录与一次文档草稿, 分发受限模板和函数管理能力.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Host.Bridge.Udf;
using PbiUtils.Host.Bridge.Calendar;
using PbiUtils.Application.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

using static PbiUtils.Host.Bridge.Scaffolding.ScaffoldBridgeParameters;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Infrastructure.Scaffolding.Templates;

namespace PbiUtils.Host.Bridge.Scaffolding;

/// <summary>草稿身份只在当前宿主文档有效, 网页不传任意输出或模板路径.</summary>
public sealed partial class ScaffoldBridge(IPbipTemplateCatalog catalog, ScaffoldService service, string userRoot,
    Func<CancellationToken, Task<string?>> chooseOutput, Func<string, CancellationToken, Task>? openTemplates = null,
    IUdfPackageProvider? packages = null, ILocalUdfStore? local = null,
    Func<string, string, CancellationToken, Task<ScaffoldTemplateDetails>>? inspect = null, Action? release = null, IUdfSourceProvider? sources = null,
    Func<string, string, ScaffoldCalendarOptions, string, CancellationToken, Task<ScaffoldCalendarProgram>>? previewCalendar = null,
    Func<string, string, CancellationToken, Task>? openDirectory = null, Func<string, string, CancellationToken, Task<ProjectSnapshot>>? openDesktop = null)
{
    /// <summary>打开成功同时携带连接快照, 创建入口仅供宿主记录最近文件, 不加入网页回执.</summary>
    internal sealed record DesktopOpenResult(bool Opened, ProjectSnapshot Project, [property: JsonIgnore] string EntryPath);

    private readonly UdfCatalogBridge udfCatalog = new(packages, sources);
    private readonly UdfManagementBridge udfManagement = new(local, sources);
    private readonly CalendarBridge calendarBridge = new(userRoot, previewCalendar);
    private TemplateMeasurePreferences? measurePreferences;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim calendarGate = new(1, 1);
    private readonly Lock sync = new();
    private readonly List<string> invalidated = [];
    private string? draftId;
    private int revision;
    private int generation;
    private ScaffoldRequest? draft;
    private string? parentDirectory;
    private string? planDigest;
    private string? createdPath;
    private string? createdIdentity;
    private bool closed;
    private bool stopping;
    private int calendarPending;
    private TaskCompletionSource calendarDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int udfPending;
    private TaskCompletionSource udfDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? shutdown;

    /// <summary>草稿事务维持独占门闩, 纯日历偏好和内容解析使用独立串行边界.</summary>
    public async Task<object> HandleAsync(string method, JsonElement parameters, IProgress<ScaffoldProgress>? progress,
        CancellationToken token, Action? committing = null)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw Invalid();
        CalendarBridgeBudget.Parameters(method, parameters);
        JsonInputValidation.RejectDuplicates(parameters);
        if (method is "calendar.preferences" or "calendar.savePreferences" or "calendar.parseWorkdays" or "calendar.preview")
            return await HandleCalendarAsync(method, parameters, committing, token);
        if (method is "udf.catalog" or "udf.catalogRefresh")
            return await HandleUdfCatalogAsync(method, parameters, progress, committing, token);
        if (!await gate.WaitAsync(0, token)) throw new AppException("SCAFFOLD_BUSY", "脚手架操作尚未结束", 3);
        try
        {
            int epoch;
            lock (sync) { ObjectDisposedException.ThrowIf(closed || stopping, this); epoch = generation; }
            await DrainInvalidatedAsync();
            switch (method)
            {
                case "scaffold.templates":
                    Empty(parameters);
                    measurePreferences ??= await Task.Run(() => new LocalTemplateMeasurePreferencesStore(userRoot).Read(), token);
                    return new
                    {
                        templates = await catalog.ListAsync(token),
                        defaultMeasures = measurePreferences.Measures,
                        measurePreferencesWarning = measurePreferences.Warning,
                        periods = ScaffoldTimeIntelligence.Templates,
                        calendarFields = ScaffoldCalendarFields.All,
                        templateQueries = ScaffoldTemplateQueries.All,
                        templateDirectory = Path.Combine(userRoot, "templates", "pbip"),
                        git = await new ScaffoldGit().ProbeAsync(token)
                    };
                case "scaffold.templateDetails":
                    var template = Read<TemplateInput>(parameters);
                    if (inspect is null) throw Unsupported();
                    return await inspect(template.TemplateId, template.TemplateBaseline, token);
                case "scaffold.chooseOutput":
                    Empty(parameters);
                    var selected = await chooseOutput(token) ?? throw new AppException("CANCELLED", "已取消选择输出目录", 130);
                    selected = await Task.Run(() =>
                    {
                        var path = Path.GetFullPath(selected);
                        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(path)) throw new AppException("INVALID_OUTPUT", "请选择已存在的本地目录", 3);
                        UserDataStore.Check(path);
                        return path;
                    }, token);
                    EnsureCurrent(epoch);
                    await DiscardCurrentAsync();
                    token.ThrowIfCancellationRequested(); committing?.Invoke();
                    lock (sync) { EnsureCurrent(epoch); parentDirectory = selected; createdPath = null; createdIdentity = null; }
                    return new { directory = selected, selectionId = Guid.NewGuid().ToString("N") };
                case "scaffold.openDirectory":
                case "scaffold.openDesktop":
                    Empty(parameters);
                    var entry = await Task.Run(ReadCreatedTarget, token);
                    EnsureCurrent(epoch); token.ThrowIfCancellationRequested();
                    if (method == "scaffold.openDesktop")
                    {
                        // 等待和读取期间允许取消, 当前模型仅由会话在成功取得完整快照后发布.
                        var connect = openDesktop ?? throw Unsupported();
                        return new DesktopOpenResult(true, await connect(entry.Path, entry.Identity, token), entry.Path);
                    }
                    var opener = openDirectory ?? throw Unsupported();
                    committing?.Invoke();
                    await opener(entry.Path, entry.Identity, token);
                    return new { opened = true };
                case "scaffold.openTemplates":
                    Empty(parameters);
                    if (openTemplates is null) throw Unsupported();
                    var templateRoot = await new TemplateDirectory(userRoot).EnsureReadmeAsync(token);
                    EnsureCurrent(epoch); token.ThrowIfCancellationRequested(); committing?.Invoke();
                    await openTemplates(templateRoot, CancellationToken.None);
                    return new { opened = true };
                case "scaffold.updateDraft":
                    return await UpdateDraftAsync(Read<DraftInput>(parameters), epoch, token);
                case "scaffold.preview":
                    var identity = Read<DraftIdentity>(parameters);
                    var request = Current(identity.DraftId, identity.Revision);
                    var output = Output(request);
                    await DiscardCurrentAsync();
                    // 完整草稿完成分块保存后记忆显式开启的配置; null 不清空用户上次方案.
                    if (request.Measures is { } definitions)
                    {
                        var store = new LocalTemplateMeasurePreferencesStore(userRoot);
                        measurePreferences ??= await Task.Run(store.Read, token);
                        measurePreferences = await Task.Run(() => store.Save(definitions, measurePreferences.Baseline, token, () => EnsureCurrent(epoch)), token);
                    }
                    var plan = await service.PreviewAsync(request, output, progress, token);
                    try { lock (sync) { EnsureCurrent(epoch); planDigest = plan.Digest; } }
                    catch { await service.DiscardAsync(plan.Digest, CancellationToken.None); throw; }
                    return plan;
                case "scaffold.create":
                    var create = Read<CreateInput>(parameters);
                    var creating = Current(create.DraftId, create.Revision);
                    lock (sync) if (planDigest != create.Digest) throw new AppException("INVALID_SCAFFOLD_PLAN", "请先预览当前脚手架配置", 3);
                    var created = await service.CreateAsync(create.Digest, creating, Output(creating), progress, token, () => { EnsureCurrent(epoch); committing?.Invoke(); });
                    await Task.Run(() => BindCreatedPath(created, epoch));
                    return created;
                case "scaffold.discard":
                    var discard = Read<DigestInput>(parameters);
                    lock (sync) if (planDigest != discard.Digest) throw new AppException("INVALID_SCAFFOLD_PLAN", "该计划不属于当前文档", 3);
                    return new { discarded = await DiscardCurrentAsync() };
                case "udf.sources":
                case "udf.sourceSave":
                case "udf.sourceDelete":
                case "udf.localList":
                case "udf.localGet":
                case "udf.localSave":
                case "udf.localDelete":
                    return await udfManagement.HandleAsync(method, parameters, () => { EnsureCurrent(epoch); committing?.Invoke(); }, token);
                default: throw Unsupported();
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>部分编辑先严格校验, 只有当前草稿版本可替换内存内容.</summary>
    private async Task<object> UpdateDraftAsync(DraftInput input, int epoch, CancellationToken token)
    {
        ScaffoldRequest candidate;
        if (input.DraftId is null)
        {
            if (input.Section != "base" || input.Revision is not null) throw Stale();
            var value = Read<BaseInput>(input.Value);
            candidate = new(1, value.TemplateId, value.TemplateBaseline, value.ProjectName, value.Locale, null, null, null, null, null, value.GitInitialize);
        }
        else
        {
            candidate = Current(input.DraftId, input.Revision ?? throw Stale());
            candidate = ScaffoldDraftChanges.Apply(candidate, input.Section, input.Value);
        }
        ScaffoldValidation.Validate(candidate);
        if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length > 4 * 1024 * 1024) throw Invalid();
        await DiscardCurrentAsync();
        token.ThrowIfCancellationRequested();
        lock (sync)
        {
            EnsureCurrent(epoch);
            if (input.DraftId is not null && (draftId != input.DraftId || revision != input.Revision)) throw Stale();
            draftId ??= Guid.NewGuid().ToString("N");
            draft = candidate;
            revision++;
            return new { draftId, revision };
        }
    }

    /// <summary>只返回当前文档及版本的草稿, 不按用户可见名称匹配.</summary>
    private ScaffoldRequest Current(string id, int version)
    {
        lock (sync) { if (draft is null || id != draftId || version != revision) throw Stale(); return draft; }
    }
    /// <summary>输出只从系统选择结果和共享校验后的项目名派生.</summary>
    private string Output(ScaffoldRequest request)
    {
        lock (sync) return Path.Combine(parentDirectory ?? throw new AppException("OUTPUT_NOT_SELECTED", "请先指定项目输出父目录", 3), ScaffoldValidation.ProjectName(request.ProjectName));
    }
    /// <summary>编辑/改目录使既有预览失效, 已发布结果不删除.</summary>
    private async Task<bool> DiscardCurrentAsync()
    {
        string? digest;
        lock (sync) { digest = planDigest; planDigest = null; }
        return digest is not null && await service.DiscardAsync(digest, CancellationToken.None);
    }
    /// <summary>只从实际发布且已核验的回执绑定入口, 绑定失败保留发布事实但不授予打开权限.</summary>
    /// <param name="result">共享创建服务实际返回的回执.</param>
    /// <param name="epoch">发起创建时的当前文档代次.</param>
    private void BindCreatedPath(ScaffoldResult result, int epoch)
    {
        string? path = null, identity = null;
        if (result.ProjectCreated && result.Verified)
        {
            try { path = result.ProjectPath; identity = ReadEntryIdentity(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException or ArgumentException or NotSupportedException)
            { path = null; }
        }
        lock (sync)
        {
            if (closed || stopping || generation != epoch) return;
            createdPath = path; createdIdentity = identity;
        }
    }
    /// <summary>打开前复核原文件对象和本地 PBIP 边界, 被替换、删除或链接的入口一律失败.</summary>
    /// <returns>仍由当前宿主绑定的完整 PBIP 入口及创建时原始文件身份; 无有效权限时抛出稳定应用错误.</returns>
    private (string Path, string Identity) ReadCreatedTarget()
    {
        string path, identity;
        lock (sync) { path = createdPath ?? throw CreatedUnavailable(); identity = createdIdentity ?? throw CreatedUnavailable(); }
        try { if (ReadEntryIdentity(path) != identity) throw CreatedUnavailable(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException or ArgumentException or NotSupportedException)
        { throw CreatedUnavailable(); }
        return (path, identity);
    }
    /// <summary>本地创建入口必须为完整普通 PBIP 文件, 链接和网络目标不能获得权限.</summary>
    /// <param name="path">仅来自实际创建回执的入口.</param>
    /// <returns>文件系统权威对象身份; 路径或对象不安全时拒绝.</returns>
    private static string ReadEntryIdentity(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length < 3
            || path[1] != ':' || path[2] is not ('\\' or '/') || path.AsSpan(2).Contains(':')
            || !Path.GetExtension(path).Equals(".pbip", StringComparison.OrdinalIgnoreCase)) throw CreatedUnavailable();
        return ProjectFileIdentity.Read(path);
    }
    /// <summary>旧文档失效立即撤销身份, 未提交目录在下一次业务或关闭时排空.</summary>
    public void Invalidate()
    {
        lock (sync)
        {
            generation++;
            if (planDigest is not null) invalidated.Add(planDigest);
            planDigest = null; draftId = null; draft = null; revision = 0; parentDirectory = null; createdPath = null; createdIdentity = null;
        }
    }
    /// <summary>串行清理已撤销计划, 不与当前提交竞争.</summary>
    private async Task DrainInvalidatedAsync()
    {
        string[] digests;
        lock (sync) { digests = [.. invalidated.Distinct(StringComparer.Ordinal)]; invalidated.Clear(); }
        foreach (var digest in digests) await service.DiscardAsync(digest, CancellationToken.None);
    }
    /// <summary>关闭为可等待任务, 窗口退出前排空并清理未发布 staging.</summary>
    public Task ShutdownAsync()
    {
        lock (sync)
        {
            if (shutdown is null)
            {
                stopping = true;
                shutdown = CloseAsync(Task.WhenAll(calendarPending == 0 ? Task.CompletedTask : calendarDrained.Task, udfPending == 0 ? Task.CompletedTask : udfDrained.Task));
            }
            return shutdown;
        }
    }
    /// <summary>等待当前处理结束后撤销预览, 最后才释放拥有资源.</summary>
    private async Task CloseAsync(Task pendingCalendar)
    {
        await gate.WaitAsync();
        try { await pendingCalendar; lock (sync) { closed = true; createdPath = null; createdIdentity = null; } await DiscardCurrentAsync(); await DrainInvalidatedAsync(); }
        finally { service.Dispose(); release?.Invoke(); gate.Release(); }
    }
    /// <summary>恢复的旧输入不得重新建立已撤销身份.</summary>
    private void EnsureCurrent(int epoch) { lock (sync) { if (closed || generation != epoch) throw Stale(); } }
    /// <summary>固定旧文档/草稿提示.</summary>
    private static AppException Stale() => new("STALE_SCAFFOLD_DRAFT", "脚手架草稿已变化或文档已失效, 请重新预览", 3);
    /// <summary>已创建事实不能替代当前可核验的打开目标权限.</summary>
    private static AppException CreatedUnavailable() => new("CREATED_PROJECT_NOT_AVAILABLE", "请先创建并核验工程", 3);
}
