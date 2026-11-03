// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Projects\ChangeWorkspace.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 私有 staging 准备模型/PBIR 副本, 复核全部文件后原子发布新目录.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;
using PbiUtils.Infrastructure.Scaffolding;
using static PbiUtils.Infrastructure.Projects.ChangeWorkspaceFiles;

using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Projects;

/// <summary>在 staging 准备变更, 支持发布新副本或通过文件事务更新当前 PBIP, 不提交在线模型.</summary>
public sealed partial class ChangeWorkspace(string? userDirectory = null, ILogger<ChangeWorkspace>? logger = null) : IChangeWorkspace, IModelEditWorkspace
{
    private readonly ILogger log = logger ?? NullLogger<ChangeWorkspace>.Instance;
    private sealed record Prepared(string? Source, string Staging, string Entry, Dictionary<string, string> Before, Dictionary<string, string> After);
    private readonly ConcurrentDictionary<string, Prepared> prepared = new();
    private readonly ProjectService projects = new(new ProjectReader());

    /// <summary>复制并在 staging 运行官方序列化, 返回真实完整 diff.</summary>
    public Task<ChangePlan> PrepareAsync(ChangeRequest request, CancellationToken token) => Task.Run(() => PrepareCoreAsync(request, token), token);

    /// <summary>在线程池准备完整项目根的私有副本, 模型绑定仍限制在实际 PBIP 父目录中.</summary>
    /// <param name="request">宿主核实的入口, 输出根及单项操作; 原地输出必须等于固定项目根.</param>
    /// <param name="token">复制, 解析和清单复核时检查的取消令牌.</param>
    /// <param name="edits">内存草稿冻结的整批编辑; 未提供时只使用请求中的单项操作.</param>
    /// <returns>保留根相对嵌套入口与文件差异的准备计划, 不发布或改写原工程.</returns>
    /// <exception cref="AppException">入口, 路径, 模型, 配置资产或完整文件基线不能安全核验.</exception>
    private async Task<ChangePlan> PrepareCoreAsync(ChangeRequest request, CancellationToken token, IReadOnlyList<ModelEditRequest>? edits = null)
    {
        var sourceSnapshot = await projects.InspectAsync(request.ProjectPath, token).ConfigureAwait(false);
        var entry = Path.GetFullPath(sourceSnapshot.ProjectPath);
        var source = PbipProjectLayout.ProjectRoot(entry);
        // 清单和回滚材料相对于完整项目根, PBIP 入口保留其嵌套相对位置; 不能退化为文件名.
        var relativeEntry = Path.GetRelativePath(source, entry);
        var output = Path.GetFullPath(request.OutputPath);
        var parent = Path.GetDirectoryName(output) ?? throw new AppException("INVALID_OUTPUT", "输出需要新的子目录");
        CheckNoLinks(parent);
        if (!request.InPlace && (Directory.Exists(output) || File.Exists(output))) throw new AppException("OUTPUT_CONFLICT", "目标已存在, 禁止覆盖", 3);
        if (request.InPlace && !string.Equals(source, output, StringComparison.OrdinalIgnoreCase)) throw new AppException("INVALID_OUTPUT", "原地修改只能指向当前工程", 3);
        if (!request.InPlace && (Inside(source, output) || Inside(output, source))) throw new AppException("INVALID_OUTPUT", "输出与源工程不能相互包含", 3);
        var before = Inventory(source, token);
        if (before.Keys.Any(f => Path.GetFileName(f) == "unappliedChanges.json"))
            throw new AppException("UNAPPLIED_CHANGES", "源工程存在未应用的更改, 请先在 Desktop 处理", 3);
        var id = Guid.NewGuid().ToString("N");
        // staging 与最终输出同卷, 最后一个目录重命名是发布边界.
        var staging = Path.Combine(parent, ".pbi-utils-stage-" + id);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in before.Keys)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, file), destination, false);
            }
            var stagedEntry = Path.Combine(staging, relativeEntry);
            var pbipDirectory = Path.GetDirectoryName(stagedEntry)!;
            var project = JsonNode.Parse(File.ReadAllText(stagedEntry))!;
            var report = Resolve(pbipDirectory, project["artifacts"]![0]!["report"]!["path"]!.GetValue<string>(), pbipDirectory);
            var binding = JsonNode.Parse(File.ReadAllText(Path.Combine(report, "definition.pbir")))!;
            var model = Resolve(report, binding["datasetReference"]!["byPath"]!["path"]!.GetValue<string>(), pbipDirectory);
            foreach (var edit in edits ?? [new(request.Operation, request.ObjectName, request.Property, request.Value, request.Table, request.PageId, request.Configuration)])
            {
                var mutation = request with { Operation = edit.Operation, ObjectName = edit.ObjectName, Property = edit.Property, Value = edit.Value, Table = edit.Table, PageId = edit.PageId, Configuration = edit.Configuration };
                if (mutation.Operation == "visual.set") PatchVisual(mutation, report, pbipDirectory);
                else MutateModel(mutation, model, sourceSnapshot.ModelFormat);
                if (edit.Configuration is { } configuration)
                {
                    if (!string.Equals(configuration.AssetRoot, source, StringComparison.OrdinalIgnoreCase)) throw new AppException("CONFIGURATION_TARGET_CHANGED", "冻结配置不属于当前工程", 3);
                    foreach (var asset in configuration.Assets)
                    {
                        var destination = ModelConfigurationCompiler.AssetPath(staging, asset.RelativePath);
                        var actual = File.Exists(destination) ? FileHash(destination, token) : null;
                        if (actual != asset.BeforeHash)
                            throw new AppException("STALE_BASELINE", "冻结文件资产与当前基线不一致", 3);
                        if (asset.AfterHash is null)
                        {
                            if (asset.SourceFile is not null || asset.Bytes != 0 || asset.BeforeHash is null)
                                throw new AppException("CONFIGURATION_ASSET_CONFLICT", "冻结删除计划无效, 未修改文件", 3);
                            File.Delete(destination);
                            continue;
                        }
                        if (asset.SourceFile is null) throw new AppException("CONFIGURATION_ASSET_CONFLICT", "冻结文件来源缺失", 3);
                        UserDataStore.Check(asset.SourceFile);
                        if (new FileInfo(asset.SourceFile).Length != asset.Bytes || FileHash(asset.SourceFile, token) != asset.AfterHash)
                            throw new AppException("STALE_BASELINE", "冻结文件资产与当前基线不一致", 3);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(asset.SourceFile, destination, true);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            await projects.InspectAsync(stagedEntry, token);
            var after = Inventory(staging, token);
            AssertSame(before, Inventory(source, token));
            var changes = before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Where(f => before.GetValueOrDefault(f) != after.GetValueOrDefault(f))
                .Select(f => new FileChange(f, before.GetValueOrDefault(f), after.GetValueOrDefault(f), Text(source, f, before), Text(staging, f, after))).ToArray();
            var digest = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sourceSnapshot.ProjectId, before, output, request, changes })));
            var plan = new ChangePlan(id, digest, sourceSnapshot.ProjectId, sourceSnapshot.BaselineId, output, request, changes,
                [request.InPlace ? "直接修改当前 PBIP, 失败使用本轮临时快照恢复" : "仅发布新工程副本, 原工程保持不变", "模型序列化可能产生排版差异, 请逐文件检查", "Desktop 重开、数据刷新和业务数值尚未验证", "PBIR 修改仅验证已支持的坐标字段, 不等于完整 Schema/引用验收"]);
            prepared[id] = new(source, staging, relativeEntry, before, after);
            return plan;
        }
        catch (Exception failure)
        {
            if (!TryDeleteStage(staging)) throw new AppException("STAGING_RETAINED", $"准备失败 ({(failure as AppException)?.Code ?? "EXECUTION_FAILED"}), 临时目录未能安全清理; 未发布副本", 4);
            throw;
        }
    }

    /// <summary>重新验证两个快照并以单次目录移动发布; 不覆盖已存在输出.</summary>
    public Task<ChangeResult> PublishAsync(ChangePlan plan, CancellationToken token, Action? committing = null) => Task.Run(() => PublishCoreAsync(plan, committing, token), token);

    /// <summary>在线程池复核全部文件并发布, 提交后使用独立令牌回读.</summary>
    private async Task<ChangeResult> PublishCoreAsync(ChangePlan plan, Action? committing, CancellationToken token)
    {
        if (!prepared.TryRemove(plan.Id, out var item)) throw new AppException("PLAN_NOT_FOUND", "staging 计划不可用", 3);
        try
        {
            token.ThrowIfCancellationRequested();
            CheckNoLinks(Path.GetDirectoryName(plan.OutputPath)!);
            if (item.Source is not null) AssertSame(item.Before, Inventory(item.Source, token));
            AssertSame(item.After, Inventory(item.Staging, token));
            if (plan.Request.InPlace) return await PublishInPlaceAsync(plan, item, committing, token);
            if (Directory.Exists(plan.OutputPath) || File.Exists(plan.OutputPath)) throw new AppException("OUTPUT_CONFLICT", "目标已经存在, 请重新选择", 3);
            await projects.InspectAsync(Path.Combine(item.Staging, item.Entry), token);
            token.ThrowIfCancellationRequested();
            if (item.Source is not null) AssertSame(item.Before, Inventory(item.Source, token));
            AssertSame(item.After, Inventory(item.Staging, token));
            CheckNoLinks(Path.GetDirectoryName(plan.OutputPath)!);
            CheckNoLinks(item.Staging);
            token.ThrowIfCancellationRequested();
            committing?.Invoke();
            Directory.Move(item.Staging, plan.OutputPath);
            // 发布后不再宣称取消可撤销, 回读使用独立令牌完成一致性检查.
            var snapshot = await projects.InspectAsync(Path.Combine(plan.OutputPath, item.Entry), CancellationToken.None);
            return new(plan.Id, "files_created", snapshot.ProjectPath, snapshot.BaselineId, plan.Warnings);
        }
        finally { if (Directory.Exists(item.Staging)) TryDeleteStage(item.Staging); }
    }

    /// <summary>直接逐文件原子替换当前工程; 失败使用本轮临时原内容恢复.</summary>
    private async Task<ChangeResult> PublishInPlaceAsync(ChangePlan plan, Prepared item, Action? committing, CancellationToken token)
    {
        var source = item.Source ?? throw new AppException("INVALID_OPERATION", "计划缺少源工程");
        var transactionRoot = Path.Combine(userDirectory ?? RuntimeEnvironment.UserDataDirectory(), "transactions", "pbip");
        UserDataStore.Check(transactionRoot);
        Directory.CreateDirectory(transactionRoot);
        var identity = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(plan.Request.ProjectPath).ToUpperInvariant()));
        using var lease = UserDataStore.Lock(Path.Combine(transactionRoot, identity + ".lock"));
        await projects.InspectAsync(Path.Combine(item.Staging, item.Entry), token);
        AssertSame(item.Before, Inventory(source, token));
        AssertSame(item.After, Inventory(item.Staging, token));
        // 本轮回滚仅保存准确的当前文件字节, 正常结束后删除临时材料, 不保留历史定义.
        var rollback = Path.Combine(Path.GetDirectoryName(item.Staging)!, ".pbi-utils-stage-" + Guid.NewGuid().ToString("N"));
        CheckNoLinks(Path.GetDirectoryName(rollback)!); Directory.CreateDirectory(rollback);
        var retainRollback = false;
        try
        {
            foreach (var change in plan.Changes.Where(change => change.BeforeHash is not null))
            {
                token.ThrowIfCancellationRequested();
                var original = Path.Combine(source, change.Path); UserDataStore.Check(original);
                var copy = Path.Combine(rollback, change.Path); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(original, copy);
                if (FileHash(copy, token) != change.BeforeHash) throw new AppException("STALE_BASELINE", "本轮回滚快照与准备基线不一致", 3);
            }
            AssertSame(item.Before, Inventory(source, token));
            token.ThrowIfCancellationRequested();
            committing?.Invoke();
            List<string> written = [];
            try
            {
                foreach (var change in plan.Changes)
                {
                    var destination = Path.Combine(source, change.Path);
                    UserDataStore.Check(destination);
                    var currentHash = File.Exists(destination) ? FileHash(destination, CancellationToken.None) : null;
                    if (currentHash != change.BeforeHash) throw new AppException("STALE_BASELINE", "目标文件在提交期间发生变化", 3);
                    written.Add(change.Path);
                    if (change.AfterHash is null) File.Delete(destination);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        CopyAtomic(Path.Combine(item.Staging, change.Path), destination);
                    }
                }
                AssertSame(item.After, Inventory(source, CancellationToken.None));
                var snapshot = await projects.InspectAsync(plan.Request.ProjectPath, CancellationToken.None);
                return new(plan.Id, "files_updated", snapshot.ProjectPath, snapshot.BaselineId,
                    ["当前 PBIP 已更新; 请在 Desktop 重新打开验证, 未提交在线模型"]);
            }
            catch
            {
                if (!RestoreWrittenFilesFromBackup(source, rollback, plan.Changes, written))
                {
                    retainRollback = true;
                    throw new AppException("RESTORE_REQUIRED", "修改失败, 部分文件存在外部变化或无法恢复。已保留本轮回滚快照与现有内容, 请核对后继续", 4);
                }
                throw new AppException("CHANGE_ROLLED_BACK", "修改失败, 已恢复本轮修改前的文件", 4);
            }
        }
        finally { if (!retainRollback) TryDeleteStage(rollback); }
    }

    /// <summary>恢复本轮已开始替换的文件; 外部内容变化必须保留并交由人工核验.</summary>
    /// <param name="source">已核验的工程根目录.</param>
    /// <param name="changes">本轮冻结的前后文件散列.</param>
    /// <param name="originals">本轮替换前的原始内容.</param>
    /// <param name="written">本轮已经开始写入的相对路径.</param>
    /// <returns>全部可恢复文件均完成时为 true, 外部修改或文件错误为 false.</returns>
    internal static bool RestoreWrittenFiles(string source, IReadOnlyList<FileChange> changes, IReadOnlyDictionary<string, byte[]?> originals, IReadOnlyList<string> written)
        => ChangeWorkspaceFiles.RestoreWrittenFiles(source, changes, originals, written);

    /// <summary>释放本进程明确持有的计划; 无法安全删除时明确报告, 不跟随链接.</summary>
    public void Discard(string planId)
    {
        if (prepared.TryRemove(planId, out var item) && !TryDeleteStage(item.Staging))
            throw new AppException("STAGING_RETAINED", "临时目录未能安全清理, 未改原工程或已发布副本", 4);
    }

    /// <summary>先验证整棵临时树的绝对范围及重解析点, 再逐文件删除; 不使用递归删除.</summary>
    private bool TryDeleteStage(string staging)
    {
        try
        {
            var root = Path.GetFullPath(staging);
            if (!Path.GetFileName(root).StartsWith(".pbi-utils-stage-", StringComparison.Ordinal)) return false;
            if (!Directory.Exists(root)) return true;
            List<string> directories = [], files = [];
            void Collect(string directory)
            {
                if (!Inside(root, directory)) throw new IOException("临时目录越界");
                CheckNoLinks(directory);
                directories.Add(directory);
                foreach (var file in Directory.GetFiles(directory)) { CheckNoLinks(file); files.Add(file); }
                foreach (var child in Directory.GetDirectories(directory)) Collect(child);
            }
            Collect(root);
            foreach (var file in files) { CheckNoLinks(file); File.Delete(file); }
            foreach (var directory in directories.AsEnumerable().Reverse()) { CheckNoLinks(directory); Directory.Delete(directory, false); }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AppException)
        {
            BusinessLog.StagingCleanupIncomplete(log);
            return false;
        }
    }

    /// <summary>退出时只清理由当前实例创建且仍持有的临时目录.</summary>
    public void Dispose()
    {
        foreach (var id in prepared.Keys)
            if (prepared.TryRemove(id, out var item)) TryDeleteStage(item.Staging);
    }

    /// <summary>只修改被批准的单对象属性; 模型 UDF 由当前 TOM 验证兼容性, 不推断查询内函数能力.</summary>
    private static void MutateModel(ChangeRequest request, string modelPath, string format)
    {
        using var db = format == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelPath, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(modelPath, "model.bim")));
        ModelMutation.Apply(db, request);
        if (format == "TMDL") TOM.TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelPath, "definition"));
        else File.WriteAllText(Path.Combine(modelPath, "model.bim"), TOM.JsonSerializer.SerializeDatabase(db));
    }

    /// <summary>PBIR 定点坐标补丁保留未知字段, 不改绑定或版本.</summary>
    private static void PatchVisual(ChangeRequest request, string report, string root)
    {
        if (!double.TryParse(request.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)
            || number < 0 || number > 10000 || (request.Property is "width" or "height" && number == 0))
            throw new AppException("VALIDATION_ERROR", "坐标需为 0–10000, 宽高必须大于零");
        foreach (var id in new[] { request.PageId, request.ObjectName })
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', ':', '.']) >= 0) throw new AppException("VALIDATION_ERROR", "页面或视觉 ID 无效");
        var file = Resolve(report, $"definition/pages/{request.PageId}/visuals/{request.ObjectName}/visual.json", root);
        var visual = JsonNode.Parse(File.ReadAllText(file))!;
        var schema = visual["$schema"]?.GetValue<string>();
        if (schema != "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/2.4.0/schema.json")
            throw new AppException("UNSUPPORTED_SCHEMA", "当前只对 visualContainer 2.4.0 提供坐标副本修改", 3);
        if (visual["name"]?.GetValue<string>() != request.ObjectName || visual["position"] is not JsonObject position)
            throw new AppException("INVALID_REPORT_REFERENCE", "视觉身份或位置节点不一致");
        position[request.Property] = number;
        File.WriteAllText(file, visual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }


}
