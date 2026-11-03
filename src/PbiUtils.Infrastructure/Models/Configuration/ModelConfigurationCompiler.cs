// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Configuration\ModelConfigurationCompiler.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 准备精确模型配置与流式文件资产计划, 保留来源基线和未知文件.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Udf.Model;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Models.Configuration;

/// <summary>只消费宿主权威快照; 临时资产由本实例拥有, 交付后必须先排空再释放.</summary>
public sealed class ModelConfigurationCompiler(UdfDependencyResolver? resolver = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> stages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ModelConfigurationPlan, byte> plans = new();

    /// <summary>从具体离线 PBIP 入口读取只读配置能力, 文件读取前后核实完整模型基线, 不创建编辑会话.</summary>
    /// <param name="snapshot">宿主已经绑定的具体本机 PBIP 快照, 不接受 model:// 或网页提供的任意路径.</param>
    /// <param name="token">读取模型定义和资产前后生效的取消令牌.</param>
    /// <returns>实际文件配置能力, 缺配置目录时取得可创建的 missing 修订, 不创建目录.</returns>
    /// <exception cref="AppException">目标不是本地 PBIP, 完整基线变化或模型定义不可安全解析.</exception>
    public Task<ModelConfigurationAssetContext> ReadAssetContextAsync(ProjectSnapshot snapshot, CancellationToken token) => Task.Run(async () =>
    {
        if (snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal)
            || !Path.GetExtension(snapshot.ProjectPath).Equals(".pbip", StringComparison.OrdinalIgnoreCase))
            throw new AppException("CONFIGURATION_ASSETS_UNSUPPORTED", "当前目标没有可核实的 PBIP 资产能力", 3);
        var root = PbipProjectLayout.ProjectRoot(snapshot.ProjectPath);
        var reader = new ProjectReader();
        async Task Verify(CancellationToken ct)
        {
            if ((await reader.ReadAsync(snapshot.ProjectPath, ct).ConfigureAwait(false)).BaselineId != snapshot.BaselineId)
                throw new AppException("STALE_BASELINE", "读取配置期间模型已变化, 请重新读取后重试", 3);
        }
        await Verify(token).ConfigureAwait(false);
        if (TemplateMeasureConfigurationFile.Read(root, snapshot.ProjectPath).UnavailableReason == "layout")
            return new ModelConfigurationAssetContext(root, new Dictionary<string, string>(StringComparer.Ordinal), Verify, UnavailableReason: "layout");
        var definition = snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism");
        var modelRoot = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshot.ProjectPath))!, definition.Path))!;
        UserDataStore.Check(modelRoot);
        using var database = snapshot.ModelFormat == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(modelRoot, "model.bim"), token).ConfigureAwait(false));
        var context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, root, snapshot.ProjectPath, Verify);
        await context.Verify(token).ConfigureAwait(false);
        return context;
    }, token);

    /// <summary>后台冻结精确函数、生成文件与前后指纹; 不直接修改原工程.</summary>
    /// <param name="request">宿主核实并独立确认过升级许可的精确配置.</param>
    /// <param name="snapshot">对应目标和完整原基线的权威快照.</param>
    /// <param name="token">函数解析、资产准备和冻结前的取消令牌.</param>
    /// <returns>仅供共享编辑服务消费的冻结请求, 不证明引擎或文件已提交.</returns>
    /// <exception cref="AppException">只读目标、缺失升级确认、包依赖或资产归属无效.</exception>
    /// <exception cref="OperationCanceledException">准备完成前取消, 不返回可消费计划, 已创建的临时资产由本轮清理.</exception>
    public Task<ModelEditRequest> PrepareAsync(ModelConfigurationRequest request, ProjectSnapshot snapshot, CancellationToken token,
        ModelConfigurationAssetContext? context = null) => Task.Run(async () =>
    {
        if (ModelAccessPolicy.IsReadOnly(snapshot)) throw new AppException("READ_ONLY_MODEL", "远端模型只读", 3);
        var frozen = JsonSerializer.Deserialize<ModelConfigurationRequest>(JsonSerializer.Serialize(request))!;
        ModelConfigurationValidation.Validate(frozen);
        var fileBased = !snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal)
            && Path.GetExtension(snapshot.ProjectPath).Equals(".pbip", StringComparison.OrdinalIgnoreCase);
        if (!fileBased && frozen.Sample is not null) throw new AppException("CONFIGURATION_ASSETS_UNSUPPORTED", "当前目标没有可核实的 PBIP 资产能力", 3);
        if (frozen.Measures is null) context = null;
        if (context is not null) await context.Verify(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!fileBased) frozen = NormalizeMeasureMode(frozen, context);
        var resolution = frozen.Udfs is { Length: > 0 }
            ? await (resolver ?? throw new AppException("UDF_PROVIDER_UNAVAILABLE", "未配置函数来源", 3)).ResolveAsync(frozen.Udfs, token).ConfigureAwait(false)
            : new UdfResolution([], []);
        if (snapshot.Connection?.Kind == "desktop" && resolution.Functions.Count > 0 && snapshot.Model.CompatibilityLevel < 1702
            && !frozen.AllowCompatibilityUpgrade)
            throw new AppException("CONFIGURATION_COMPATIBILITY_APPROVAL_REQUIRED", "安装 UDF 需要本次独立同意将兼容级别提升至 1702", 3);
        var assetRoot = fileBased ? PbipProjectLayout.ProjectRoot(snapshot.ProjectPath) : context?.Root;
        string? stage = null;
        try
        {
            var assets = new List<ModelConfigurationAsset>();
            IReadOnlyList<ModelConfigurationCalendarChange> derivedCalendars = [];
            IReadOnlyList<ModelConfigurationCalendarOutput> calendarOutputs = [];
            if (fileBased)
            {
                UserDataStore.Check(snapshot.ProjectPath);
                var reader = new ProjectReader();
                if ((await reader.ReadAsync(snapshot.ProjectPath, token)).BaselineId != snapshot.BaselineId) throw new AppException("STALE_BASELINE", "准备配置前模型已变化", 3);
                var definition = snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism");
                // 定义清单始终相对 PBIP 入口, 资产根可在独立 pbip 目录的上一层.
                var modelRoot = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshot.ProjectPath))!, definition.Path))!;
                UserDataStore.Check(modelRoot);
                using var database = snapshot.ModelFormat == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"))
                    : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(modelRoot, "model.bim"), token).ConfigureAwait(false));
                var ownership = ModelConfigurationOwnership.IsTemplateOnly(frozen) ? ModelConfigurationOwnership.ReadForTemplate(database.Model) : ModelConfigurationOwnership.ReadRoles(database.Model);
                if (frozen.Measures is not null)
                {
                    context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, assetRoot!, snapshot.ProjectPath, _ => Task.CompletedTask);
                    frozen = NormalizeMeasureMode(frozen, context);
                }
                var calendarNames = ownership.Roles.Where(pair => pair.Value == "calendar"
                    && (frozen.Sample is not null || frozen.Calendar?.TableName == pair.Key)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
                if (frozen.Calendar is { } selectedCalendar && database.Model.Tables.Find(selectedCalendar.TableName)?.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null)
                    calendarNames.Add(selectedCalendar.TableName);
                foreach (var name in calendarNames)
                {
                    var calendar = database.Model.Tables.Find(name) ?? throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "已生成日期表已删除", 3);
                    CalendarProgramPlanner.RequireCurrentStorage(calendar);
                }
                if (resolution.Functions.Count > 0)
                {
                    var installed = ModelConfigurationUdfLocks.ExcludeLintRemoved(database.Model, ModelConfigurationUdfLocks.FromModel(database.Model));
                    ModelConfigurationUdfLocks.VerifyInstalled(database.Model, installed);
                    resolution = ModelConfigurationUdfLocks.Merge(installed, resolution);
                }
                stage = Path.Combine(Path.GetTempPath(), "pbi-utils-configuration-" + Guid.NewGuid().ToString("N"));
                UserDataStore.Check(stage); Directory.CreateDirectory(stage); stages.TryAdd(stage, 0);
                var priorColumns = database.Model.Tables.ToDictionary(table => table.Name, table => (IReadOnlySet<string>)table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                var temporary = new ModelConfigurationPlan(snapshot.BaselineId, "preparing", frozen, resolution, [], assetRoot, assetContext: context);
                derivedCalendars = ModelConfigurationMutation.Apply(database, temporary, stage, token);
                if (frozen.Measures is { } definitions)
                {
                    assets.AddRange(TemplateMeasureConfigurationStorage.PrepareAssets(stage, context!, frozen.MeasuresMode, definitions, token));
                }
                var outputNames = derivedCalendars.Select(value => value.TableName).ToHashSet(StringComparer.Ordinal);
                if (frozen.Calendar is { } outputCalendar) outputNames.Add(outputCalendar.TableName);
                calendarOutputs = outputNames.Order(StringComparer.Ordinal).Select(name => ScaffoldCalendarWriter.CaptureOutput(database.Model.Tables[name],
                    priorColumns.GetValueOrDefault(name) ?? new HashSet<string>(StringComparer.Ordinal))).ToArray();
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
                    if (assets.Any(asset => asset.RelativePath == relative)) continue;
                    var destination = AssetPath(assetRoot!, relative);
                    var before = File.Exists(destination) ? ModelConfigurationOwnership.FileHash(destination, token) : null;
                    if (before is not null && ownership.Assets.GetValueOrDefault(relative) != before)
                        throw new AppException("CONFIGURATION_ASSET_CONFLICT", "同名文件没有可验证的生成归属或已被外部修改, 未覆盖", 3);
                    var after = ModelConfigurationOwnership.FileHash(file, token);
                    assets.Add(new(relative, file, before, after, new FileInfo(file).Length));
                }
                foreach (var asset in assets)
                    if (asset.AfterHash is { } hash) ownership.Assets[asset.RelativePath] = hash;
                    else ownership.Assets.Remove(asset.RelativePath);
                if (assets.Count > 0)
                {
                    // 准备模型与真正 Apply 都携带同一冻结资产摘要, 完整 TOM 指纹覆盖资产归属.
                    var next = (ModelConfigurationOwnership.IsTemplateOnly(frozen) ? ModelConfigurationOwnership.ReadForTemplate(database.Model)
                        : ModelConfigurationOwnership.ReadRoles(database.Model)) with
                    { Assets = ownership.Assets };
                    ModelConfigurationOwnership.SetRoles(database.Model, next);
                }
                if ((await reader.ReadAsync(snapshot.ProjectPath, token)).BaselineId != snapshot.BaselineId) throw new AppException("STALE_BASELINE", "准备配置期间模型已变化", 3);
            }
            else if (frozen.Measures is { } definitions && context is not null)
            {
                stage = Path.Combine(Path.GetTempPath(), "pbi-utils-configuration-" + Guid.NewGuid().ToString("N"));
                UserDataStore.Check(stage); Directory.CreateDirectory(stage); stages.TryAdd(stage, 0);
                assets.AddRange(TemplateMeasureConfigurationStorage.PrepareAssets(stage, context, frozen.MeasuresMode, definitions, token));
            }
            if (!fileBased && frozen.Calendar is { } memoryCalendar)
            {
                var existing = snapshot.Model.Tables.SingleOrDefault(table => table.Name == memoryCalendar.TableName);
                // 有效能力只来自完整TOM快照, 不接受网页回传; 最终私有候选仍逐列核对这一冻结结果.
                var prior = existing?.CalendarCapability?.OutputColumns;
                if (existing is not null && prior is null)
                    throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "已有日期表没有可验证的生成归属, 未覆盖", 3);
                var columns = ScaffoldCalendarWriter.PlanColumns(memoryCalendar, prior ?? [],
                    requiredFields: existing?.CalendarCapability?.LockedFields.Select(field => field.Field).ToHashSet(StringComparer.Ordinal));
                var program = ScaffoldCalendarM.Compile(memoryCalendar,
                    columns.Select(column => new ScaffoldCalendarColumn(column.SourceColumn, column.Field)).ToArray(), frozen.Locale,
                    cancellationToken: token);
                calendarOutputs = [new(memoryCalendar.TableName, columns, CalendarProgramPlanner.Capture(program))];
            }
            var digest = ModelConfigurationOwnership.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                snapshot.ProjectId,
                snapshot.BaselineId,
                frozen,
                resolution,
                derivedCalendars,
                calendarOutputs,
                assets = assets.Select(asset => new { asset.RelativePath, asset.BeforeHash, asset.AfterHash, asset.Bytes })
            }));
            // 同步 SDK/文件工作不能即时中断, 但完成后必须再次观察取消, 不把已取消的准备发布为冻结计划.
            token.ThrowIfCancellationRequested();
            var owned = stage;
            // 文件来源行会用于模式切换中旧查值的物化, 与请求一样冻结数组, 不借调用方后续编辑更改候选.
            if (context?.ExistingConfiguration is { } sourceConfiguration)
                context = context with { ExistingConfiguration = sourceConfiguration with { Measures = [.. sourceConfiguration.Measures] } };
            ModelConfigurationPlan? plan = null;
            plan = new ModelConfigurationPlan(snapshot.BaselineId, digest, frozen, resolution, assets.ToArray(), assetRoot, () =>
            {
                if (owned is not null) ReleaseStage(owned);
                if (plan is not null) plans.TryRemove(plan, out _);
            }, derivedCalendars, calendarOutputs, context);
            plans.TryAdd(plan, 0);
            return new ModelEditRequest("configuration.apply", "configuration", "config", digest, Configuration: plan);
        }
        catch { if (stage is not null) ReleaseStage(stage); throw; }
    }, token);

    /// <summary>在冻结和任何模型修改之前按真实文件能力回退模式; 显式修订过期时保留用户输入并拒绝覆盖.</summary>
    /// <param name="request">已经通过共享格式验证的精确请求.</param>
    /// <param name="context">宿主签发或从具体离线 PBIP 解析的文件能力.</param>
    /// <returns>实际可执行的冻结请求; 无 Config 文件能力时只修改为 TMDL.</returns>
    /// <exception cref="AppException">网页或调用方携带的配置修订已经变化.</exception>
    private static ModelConfigurationRequest NormalizeMeasureMode(ModelConfigurationRequest request, ModelConfigurationAssetContext? context)
    {
        if (request.Measures is null) return request;
        if (request.MeasureConfigurationRevision is { } revision && revision != context?.ConfigurationRevision)
            throw new AppException("STALE_BASELINE", "业务配置在读取后已变化, 请重新读取后重试", 3);
        return context?.CanUseMeasureConfig == true ? request : request with { MeasuresMode = "tmdl" };
    }

    /// <summary>确认后复核精确包、本地函数及所有来源基线, 空资产计划也观察取消, 从不再次解析 latest.</summary>
    /// <param name="prepared">本编译器已冻结的完整请求与私有资产计划.</param>
    /// <param name="token">提交开始前有效的核验取消令牌, 同步读取结束后再次检查.</param>
    /// <returns>所有来源与资产均未变化且本次核验未取消时完成.</returns>
    /// <exception cref="AppException">冻结身份、来源基线或私有资产变化.</exception>
    /// <exception cref="OperationCanceledException">核验完成前取消, 不把核验报告为成功.</exception>
    public async Task VerifyAsync(ModelEditRequest prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var plan = prepared.Configuration ?? throw new AppException("CONFIGURATION_PLAN_REQUIRED", "需要宿主冻结的配置", 3);
        if (plan.Digest != prepared.Value) throw new AppException("APPROVAL_MISMATCH", "配置摘要已改变", 3);
        if (plan.Udfs.Functions.Count > 0) await resolver!.VerifyFrozenAsync(plan.Udfs, token).ConfigureAwait(false);
        if (plan.AssetContext is { } context) await context.Verify(token).ConfigureAwait(false);
        foreach (var asset in plan.Assets)
        {
            var target = AssetPath(plan.AssetRoot!, asset.RelativePath);
            var actual = File.Exists(target) ? ModelConfigurationOwnership.FileHash(target, token) : null;
            if (actual != asset.BeforeHash || asset.AfterHash is null && (asset.SourceFile is not null || asset.Bytes != 0)
                || asset.AfterHash is not null && (asset.SourceFile is null || new FileInfo(asset.SourceFile).Length != asset.Bytes
                    || ModelConfigurationOwnership.FileHash(asset.SourceFile, token) != asset.AfterHash))
                throw new AppException("STALE_BASELINE", "文件资产在确认期间已变化, 未提交", 3);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>未交给待保存草稿的准备结果由宿主释放.</summary>
    public void Release(ModelEditRequest prepared)
    {
        if (prepared.Configuration is { } plan && plans.TryRemove(plan, out _)) plan.Dispose();
    }

    /// <summary>在可信工程根内解析规范资产路径, 每次访问前重新检查真实文件系统链接.</summary>
    /// <param name="root">宿主解析的工程根或本实例拥有的 staging 根.</param>
    /// <param name="relative">仅允许 data/config 下的规范相对文件路径.</param>
    /// <returns>工程根内的完整路径.</returns>
    /// <exception cref="AppException">路径越界、不规范或包含不安全链接.</exception>
    internal static string AssetPath(string root, string relative)
    {
        if (!ModelConfigurationOwnership.IsAssetPath(relative))
            throw new AppException("CONFIGURATION_ASSET_PATH_INVALID", "文件资产路径不在允许范围", 3);
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new AppException("CONFIGURATION_ASSET_PATH_INVALID", "文件资产超出工程根目录", 3);
        UserDataStore.Check(path); return path;
    }

    private void ReleaseStage(string stage)
    {
        if (!stages.ContainsKey(stage)) return;
        try
        {
            // 临时根是本实例随机创建的单层目录, 删除前再次拒绝后加链接.
            UserDataStore.Check(stage);
            if (Directory.Exists(stage))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories)) UserDataStore.Check(path);
                Directory.Delete(stage, true);
            }
            stages.TryRemove(stage, out _);
        }
        // 保存后的临时清理失败不得把已提交事实改为待核验; 保留目录并在宿主释放时再尝试.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException) { }
    }

    public void Dispose()
    {
        foreach (var plan in plans.Keys) if (plans.TryRemove(plan, out _)) plan.Dispose();
        foreach (var stage in stages.Keys) ReleaseStage(stage);
    }
}
