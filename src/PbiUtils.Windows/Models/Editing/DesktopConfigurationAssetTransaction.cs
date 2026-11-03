// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopConfigurationAssetTransaction.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为原 Desktop 模型的一次提交保留本轮文件回滚材料并更新精确配置资产, 未知提交保留回执且阻止重放.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

namespace PbiUtils.Windows.Models.Editing;

/// <summary>配置文件事务不写 PBIP 定义; prepared 可恢复, commit_started 只能以完整模型及资产回读核验.</summary>
public sealed class DesktopConfigurationAssetTransaction : IDisposable
{
    internal const string CleanupWarning = "模型与配置资产已完成, 但本轮临时槽清理未完成; 已保留清理责任, 不要重复提交。";
    private static readonly JsonSerializerOptions ReceiptJson = new()
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private sealed record Entry(string RelativePath, string? BeforeHash, string? AfterHash, string? BackupFile, string? BeforeIdentity,
        string Slot, string? AfterIdentity = null, DesktopConfigurationAssetState? Held = null,
        bool Retiring = false, DesktopConfigurationAssetState? Retired = null);
    private sealed record Receipt(int SchemaVersion, string Phase, string Root, string BeforeBaseline, string AfterBaseline, Entry[] Entries);
    private readonly string receiptPath;
    private readonly string modelReceiptPath;
    private readonly ModelConfigurationAsset[] assets;
    private readonly FileStream lease;
    private readonly DesktopConfigurationCommitGuard? ownedGuard;
    private readonly List<Entry> changed = [];
    private Receipt receipt;
    private bool started;
    private bool finished;
    /// <summary>准备新轮前重试同根旧清理的原异常, 不阻止新模型提交或更改已完成事实.</summary>
    public Exception? CleanupFailure { get; }

    /// <summary>取得同工程资产独占锁并持久化原字节及 prepared 回执, 不修改工程文件.</summary>
    /// <param name="root">官方 Desktop 文件绑定已核实的工程资产根.</param>
    /// <param name="transactionRoot">本模型本轮文件事务所在宿主用户目录.</param>
    /// <param name="beforeBaseline">提交前完整在线模型基线.</param>
    /// <param name="afterBaseline">原 TOM 对象暂存后的完整预期基线.</param>
    /// <param name="assets">宿主冻结的新增、替换或精确删除计划.</param>
    /// <param name="commitGuard">可借用本轮模型提交已持有的 guard; 省略时本事务自行取得并释放.</param>
    /// <exception cref="AppException">未核验回执、资产变化、非法路径或独占锁失败.</exception>
    public DesktopConfigurationAssetTransaction(string root, string transactionRoot, string beforeBaseline, string afterBaseline,
        IReadOnlyList<ModelConfigurationAsset> assets, DesktopConfigurationCommitGuard? commitGuard = null)
    {
        this.assets = [.. assets];
        UserDataStore.Check(root); UserDataStore.Check(transactionRoot);
        Directory.CreateDirectory(transactionRoot);
        ownedGuard = commitGuard is null ? DesktopConfigurationCommitGuard.AcquireUnverified(transactionRoot) : null;
        (commitGuard ?? ownedGuard)!.RequireActive(transactionRoot);
        var lockPath = Path.Combine(Path.GetDirectoryName(transactionRoot)!, Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())) + ".assets.lock");
        try { lease = UserDataStore.Lock(lockPath); }
        catch { ownedGuard?.Dispose(); throw; }
        receiptPath = SharedReceiptPath(transactionRoot, root);
        modelReceiptPath = Path.Combine(transactionRoot, "configuration-assets.json");
        string? directory = null;
        var persistAttempted = false;
        try
        {
            VerifyPendingCore(transactionRoot, beforeBaseline, root);
            CleanupFailure = RetryCompletedCleanupCore(transactionRoot, root);
            if (this.assets.Length == 0 || this.assets.Select(asset => asset.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != this.assets.Length) throw Invalid();
            directory = Path.Combine(transactionRoot, "assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var entries = this.assets.Select((asset, index) =>
            {
                var target = AssetPath(root, asset.RelativePath);
                RequireBefore(target, asset);
                var identity = FileIdentity(target);
                // 每条资产都预留本轮 GUID 目录中的独立备份槽; 新增文件无旧字节, 槽路径仍区分相同计划的不同轮次.
                var backup = Path.Combine(directory, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
                if (asset.BeforeHash is not null)
                {
                    var bytes = File.ReadAllBytes(target);
                    if (Hash(bytes) != asset.BeforeHash) throw Stale();
                    UserDataStore.Write(backup, bytes);
                    if (FileHash(backup) != asset.BeforeHash) throw new AppException("BACKUP_VERIFY_FAILED", "配置资产备份未通过核验, 未提交", 4);
                }
                RequireBefore(target, asset);
                if (FileIdentity(target) != identity) throw Stale();
                return new Entry(asset.RelativePath, asset.BeforeHash, asset.AfterHash, backup, identity, Guid.NewGuid().ToString("N"));
            }).ToArray();
            receipt = new(3, "prepared", Path.GetFullPath(root), beforeBaseline, afterBaseline, entries);
            persistAttempted = true;
            Persist();
        }
        catch (Exception error)
        {
            try
            {
                // 尚未尝试持久回执的准备失败没有恢复责任, 仅清理本构造函数创建且仍无链接的唯一目录.
                if (!persistAttempted && directory is not null)
                {
                    var full = Path.GetFullPath(directory);
                    if (!Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(transactionRoot)), StringComparison.OrdinalIgnoreCase)) throw Invalid();
                    UserDataStore.Check(full); Directory.Delete(full, true);
                }
            }
            catch (Exception cleanup) { error.Data["DesktopConfigurationPreparationCleanupFailed"] = cleanup; }
            finally { lease.Dispose(); ownedGuard?.Dispose(); }
            throw;
        }
    }

    /// <summary>提交前复核整批源文件和目标后原子替换或精确删除, 失败由调用方在提交前回滚.</summary>
    /// <param name="token">每个文件副作用之前检查的取消令牌.</param>
    /// <exception cref="AppException">目标或冻结源变化时不覆盖; 已写文件由 Rollback 准确恢复.</exception>
    public void Apply(CancellationToken token)
    {
        if (started || finished || changed.Count != 0) throw Invalid();
        foreach (var asset in assets) { token.ThrowIfCancellationRequested(); RequirePreparedBefore(asset); }
        foreach (var asset in assets)
        {
            token.ThrowIfCancellationRequested();
            var target = AssetPath(receipt.Root, asset.RelativePath);
            RequirePreparedBefore(asset);
            // 先登记尝试, 原子替换已生效但尾部清理抛错时仍能按前/后值精确恢复.
            var index = Array.FindIndex(receipt.Entries, entry => entry.RelativePath == asset.RelativePath);
            var entry = receipt.Entries[index];
            changed.Add(entry);
            var files = Files(receipt.Root, entry);
            if (asset.AfterHash is not null)
            {
                var bytes = File.ReadAllBytes(asset.SourceFile!);
                if (bytes.LongLength != asset.Bytes || Hash(bytes) != asset.AfterHash) throw Stale();
                var output = files.Stage(bytes);
                entry = entry with { AfterIdentity = output.Identity };
                receipt.Entries[index] = entry; changed[^1] = entry; Persist();
            }
            token.ThrowIfCancellationRequested();
            RequirePreparedBefore(asset);
            if (entry.BeforeHash is not null)
            {
                // 槽责任已在初始prepared持久. Move后先保存实际移出事实, 再比较原值; 外部新文件可无覆盖归还.
                entry = entry with { Held = files.Retain() };
                receipt.Entries[index] = entry; changed[^1] = entry; Persist();
                if (entry.Held != Before(entry)) throw Stale();
            }
            token.ThrowIfCancellationRequested();
            if (Output(entry) is { } staged) files.Publish(staged);
            if (DesktopConfigurationAssetFile.Read(target) != Output(entry)) throw Stale();
        }
    }

    /// <summary>所有资产就绪后持久记录不可重放边界; 成功返回后才能调用原 TOM 提交.</summary>
    public void MarkCommitStarted()
    {
        RequireAfter(receipt);
        receipt = receipt with { Phase = "commit_started" }; Persist(); started = true;
    }

    /// <summary>完整模型已回读后再次核验所有文件并保存完成事实, 不重做副作用.</summary>
    public Exception? Complete()
    {
        if (!started) throw Invalid();
        RequireAfter(receipt);
        receipt = receipt with { Phase = "completed" }; Persist(); finished = true;
        // completed已可靠持久, 后续槽清理失败只保留责任并告警, 不重新标记模型未知或重放提交.
        return TryCleanupSlots(Path.GetDirectoryName(modelReceiptPath)!, receipt);
    }

    /// <summary>仅模型提交前恢复本轮实际已修改且仍等于本轮输出的文件, 用户并发字节绝不覆盖.</summary>
    /// <exception cref="AppException">本轮已写文件发生外部变化或备份损坏时保留 prepared 回执供核验.</exception>
    public void Rollback()
    {
        if (started || finished) return;
        foreach (var attempted in changed.AsEnumerable().Reverse())
        {
            var index = Array.FindIndex(receipt.Entries, entry => entry.RelativePath == attempted.RelativePath);
            Restore(receipt.Root, receipt.Entries[index], updated => { receipt.Entries[index] = updated; Persist(); });
        }
        receipt = receipt with { Phase = "rolled_back" }; Persist(); finished = true;
    }

    /// <summary>只解除文件锁; 已进入提交的未知结果保留原始回执和资产, 不做自动回滚.</summary>
    public void Dispose() { lease.Dispose(); ownedGuard?.Dispose(); }

    /// <summary>只读检测本模型或同宿主工程是否有未知资产事务; 普通 CRUD 无在途记录时不增加 Desktop Bridge 依赖.</summary>
    /// <param name="transactionRoot">宿主当前模型的事务目录.</param>
    /// <returns>需要进一步核实真实 PBIP 绑定时为 true; 损坏记录保守要求核验.</returns>
    public static bool HasPendingTransactions(string transactionRoot)
    {
        var parent = Path.GetDirectoryName(transactionRoot)!;
        UserDataStore.Check(parent);
        if (!Directory.Exists(parent)) return false;
        var paths = Directory.EnumerateFileSystemEntries(parent, "*.assets.json", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(transactionRoot, "configuration-assets.json"));
        foreach (var path in paths)
        {
            try
            {
                var current = ReadOptionalReceipt(path);
                if (current is null) continue;
                if (IsPending(current) && ReadVerifiedReceipt(transactionRoot, current) is null) return true;
            }
            catch (AppException) { return true; }
        }
        return false;
    }

    /// <summary>只检查宿主是否存在已完成的待清理证明, 不将它作为未知模型提交或读取其他工程资产.</summary>
    public static bool HasCompletedCleanupCandidates(string transactionRoot)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transactionRoot))!, "configuration-assets-verified");
        UserDataStore.Check(directory);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.cleanup.json", SearchOption.TopDirectoryOnly).Any();
    }

    /// <summary>仅消费官方已核实当前根的completed清理责任; 失败返回原异常供告警, 不阻断已知模型或重放业务.</summary>
    public static Exception? RetryCompletedCleanup(string transactionRoot, string verifiedRoot, DesktopConfigurationCommitGuard? commitGuard = null)
    {
        using var owned = commitGuard is null ? DesktopConfigurationCommitGuard.AcquireUnverified(transactionRoot) : null;
        (commitGuard ?? owned)!.RequireActive(transactionRoot);
        return RetryCompletedCleanupCore(transactionRoot, verifiedRoot);
    }

    /// <summary>拒绝未核验的历史提交; 只有完整模型和全部资产均与预期相符才只读确认成功.</summary>
    /// <param name="transactionRoot">宿主本模型事务目录.</param>
    /// <param name="baseline">独立权威连接刚读取的完整模型基线.</param>
    /// <param name="verifiedRoot">当前官方文件绑定确定的工程根, 不允许代替另一根的本引擎未决记录.</param>
    /// <param name="commitGuard">可借用已持有的最终提交锁, 否则本方法自行持有并释放.</param>
    /// <exception cref="AppException">未知或损坏回执、资产变化及无法证明的模型结果.</exception>
    public static Exception? VerifyPending(string transactionRoot, string baseline, string? verifiedRoot = null, DesktopConfigurationCommitGuard? commitGuard = null)
    {
        var model = ReadOptionalReceipt(Path.Combine(transactionRoot, "configuration-assets.json"));
        var current = verifiedRoot is null ? model : ReadOptionalReceipt(SharedReceiptPath(transactionRoot, verifiedRoot));
        if (!IsPending(model) && !IsPending(current) && (verifiedRoot is null || current?.Phase != "completed")) return null;
        if (verifiedRoot is null)
        {
            throw NeedsVerification();
        }
        using var owned = commitGuard is null ? DesktopConfigurationCommitGuard.AcquireUnverified(transactionRoot) : null;
        (commitGuard ?? owned)!.RequireActive(transactionRoot);
        var lockPath = Path.Combine(Path.GetDirectoryName(transactionRoot)!, Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(verifiedRoot).ToUpperInvariant())) + ".assets.lock");
        using var held = UserDataStore.Lock(lockPath);
        VerifyPendingCore(transactionRoot, baseline, verifiedRoot);
        return RetryCompletedCleanupCore(transactionRoot, verifiedRoot);
    }

    /// <summary>调用方持有真实工程资产锁后核验回执, 持久路径不能替代当前官方绑定.</summary>
    private static void VerifyPendingCore(string transactionRoot, string baseline, string verifiedRoot)
    {
        var modelPath = Path.Combine(transactionRoot, "configuration-assets.json");
        var model = ReadOptionalReceipt(modelPath);
        // 另一引擎已核验的不可变终态不随 latest shared 被下一轮覆盖; 先消除本引擎的旧假未知.
        if (IsPending(model) && ReadVerifiedReceipt(transactionRoot, model!) is { } known)
        {
            CompleteMatchingModelReceipt(modelPath, model, known);
            model = known;
        }
        if (IsPending(model) && !Path.GetFullPath(verifiedRoot).Equals(model!.Root, StringComparison.OrdinalIgnoreCase)) throw NeedsVerification();
        var path = SharedReceiptPath(transactionRoot, verifiedRoot);
        var previous = ReadOptionalReceipt(path);
        if (previous is null) { if (IsPending(model)) throw NeedsVerification(); return; }
        if (IsPending(model) && !SamePlan(model!, previous)) throw NeedsVerification();
        if (!IsPending(previous)) { CompleteMatchingModelReceipt(modelPath, model, previous); return; }
        if (!Path.GetFullPath(verifiedRoot).Equals(previous.Root, StringComparison.OrdinalIgnoreCase)) throw NeedsVerification();
        foreach (var entry in previous.Entries)
        {
            _ = AssetPath(verifiedRoot, entry.RelativePath);
            if (entry.BackupFile is not null && (!entry.BackupFile.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(transactionRoot))!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(Path.GetDirectoryName(entry.BackupFile))?.StartsWith("assets-", StringComparison.Ordinal) != true)) throw NeedsVerification();
        }
        if (previous.Phase == "commit_started" && previous.AfterBaseline == baseline)
        {
            RequireAfter(previous);
            var verified = previous with { Phase = "completed" };
            PreserveVerifiedReceipt(transactionRoot, verified);
            UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(verified));
            CompleteMatchingModelReceipt(modelPath, model, verified);
            return;
        }
        // prepared 是持久的未提交证明, 恢复仅处理仍等于本轮前/后值的固定资产.
        if (previous.Phase == "prepared" && previous.BeforeBaseline == baseline)
        {
            foreach (var entry in previous.Entries.Reverse())
            {
                var index = Array.FindIndex(previous.Entries, candidate => candidate.RelativePath == entry.RelativePath);
                Restore(previous.Root, entry, updated =>
                {
                    previous.Entries[index] = updated;
                    UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(previous));
                });
            }
            var verified = previous with { Phase = "rolled_back" };
            PreserveVerifiedReceipt(transactionRoot, verified);
            UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(verified));
            CompleteMatchingModelReceipt(modelPath, model, verified);
            return;
        }
        throw NeedsVerification();
    }

    /// <summary>有记录时完整解析, 不将损坏或读取失败降级为无事务.</summary>
    private static Receipt? ReadOptionalReceipt(string path)
    {
        UserDataStore.Check(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & FileAttributes.Directory) != 0) throw NeedsVerification();
        return ReadReceipt(path);
    }

    /// <summary>未持久化确定终态的记录仍须核验, 不由当前绑定目录是否有文件决定.</summary>
    private static bool IsPending(Receipt? value) => value is not null && value.Phase is not ("completed" or "rolled_back");

    /// <summary>核对同一冻结计划全部身份和条目, 防止别的工程或新一轮回执替旧未知提交背书.</summary>
    private static bool SamePlan(Receipt left, Receipt right) => left.Root.Equals(right.Root, StringComparison.OrdinalIgnoreCase)
        && left.BeforeBaseline == right.BeforeBaseline && left.AfterBaseline == right.AfterBaseline
        && left.Entries.Select(PlanEntry).SequenceEqual(right.Entries.Select(PlanEntry));

    /// <summary>冻结计划不含逐文件移动和回滚事实, 终态证明仍绑定同轮唯一槽与完整前后定义.</summary>
    private static Entry PlanEntry(Entry entry) => entry with { AfterIdentity = null, Held = null, Retiring = false, Retired = null };

    /// <summary>只在 guard 和工程锁内同步本引擎的匹配未知记录, 不触碰其他引擎或不同轮次.</summary>
    private static void CompleteMatchingModelReceipt(string path, Receipt? model, Receipt verified)
    {
        if (!IsPending(model)) return;
        if (IsPending(verified) || !SamePlan(model!, verified)) throw NeedsVerification();
        UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(verified));
    }

    /// <summary>每个冻结计划拥有独立终态证明, phase 不参与键, 不以可变 latest 文件代替历史事实.</summary>
    private static string VerifiedReceiptPath(string transactionRoot, Receipt value, bool cleanup = false) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transactionRoot))!,
        "configuration-assets-verified", RootReceiptKey(value.Root) + "." + Hash(JsonSerializer.SerializeToUtf8Bytes(value with
        { Phase = "completed", Entries = [.. value.Entries.Select(PlanEntry)] })) + (cleanup ? ".cleanup.json" : ".json"));

    /// <summary>只接受同一计划的完整终态证明; 缺失返回 null, 损坏或串计划不授予已完成事实.</summary>
    private static Receipt? ReadVerifiedReceipt(string transactionRoot, Receipt value)
    {
        var verified = ReadOptionalReceipt(VerifiedReceiptPath(transactionRoot, value)) ?? ReadOptionalReceipt(VerifiedReceiptPath(transactionRoot, value, true));
        if (verified is not null && (IsPending(verified) || !SamePlan(value, verified))) throw NeedsVerification();
        return verified;
    }

    /// <summary>持有全局 guard 时原子保留已核验终态, 不允许后续计划覆盖或改写既有事实.</summary>
    private static void PreserveVerifiedReceipt(string transactionRoot, Receipt value)
    {
        if (IsPending(value)) throw NeedsVerification();
        var path = VerifiedReceiptPath(transactionRoot, value, value.Phase == "completed");
        if (ReadVerifiedReceipt(transactionRoot, value) is { } previous)
        {
            if (!SamePlan(previous, value) || previous.Phase != value.Phase) throw NeedsVerification();
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UserDataStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(value));
    }

    /// <summary>损坏、过大或未知阶段的持久记录一律阻断, 不从部分 JSON 推断已完成.</summary>
    private static Receipt ReadReceipt(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw NeedsVerification();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonInputValidation.RejectDuplicates(document.RootElement);
            var value = document.RootElement.Deserialize<Receipt>(ReceiptJson);
            if (value is not { SchemaVersion: 3, Entries: not null, Root: not null, BeforeBaseline: not null, AfterBaseline: not null }
                || value.Phase is not ("prepared" or "commit_started" or "completed" or "rolled_back") || value.Entries.Any(entry => entry is null)) throw NeedsVerification();
            // 未决事务必须有不可复用的本轮备份槽身份, 不能凭相同前后摘要借用另一轮的终态证明.
            if (IsPending(value) && (value.Entries.Length == 0 || value.Entries.Any(entry => entry.BackupFile is null))) throw NeedsVerification();
            if (value.Entries.Any(entry => (entry.BeforeHash is null) != (entry.BeforeIdentity is null)
                || (entry.AfterHash is null && entry.AfterIdentity is not null) || !Guid.TryParseExact(entry.Slot, "N", out _)
                || string.IsNullOrWhiteSpace(entry.RelativePath) || (entry.Retired is not null && !entry.Retiring)
                || (entry.Held is { } held && (string.IsNullOrWhiteSpace(held.Hash) || string.IsNullOrWhiteSpace(held.Identity)))
                || (entry.Retired is { } retired && (string.IsNullOrWhiteSpace(retired.Hash) || string.IsNullOrWhiteSpace(retired.Identity))))) throw NeedsVerification();
            if (value.Entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Entries.Length
                || value.Entries.Select(entry => entry.Slot).Distinct(StringComparer.Ordinal).Count() != value.Entries.Length) throw NeedsVerification();
            return value;
        }
        catch (JsonException) { throw NeedsVerification(); }
    }

    /// <summary>将当前回执原子持久到宿主用户目录, I/O失败原样阻止后续阶段.</summary>
    private void Persist()
    {
        if (!IsPending(receipt)) PreserveVerifiedReceipt(Path.GetDirectoryName(modelReceiptPath)!, receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        UserDataStore.Write(receiptPath, bytes);
        UserDataStore.Write(modelReceiptPath, bytes);
    }

    /// <summary>按真实工程根隔离持久回执, 同一 PBIP 的多个 Desktop 引擎共享未知提交阻断.</summary>
    private static string SharedReceiptPath(string transactionRoot, string root) => Path.Combine(Path.GetDirectoryName(transactionRoot)!,
        Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())) + ".assets.json");

    /// <summary>恢复已验证的单个本轮文件; 输出或备份漂移时拒绝覆盖.</summary>
    private static void Restore(string root, Entry entry, Action<Entry> persist)
    {
        var files = Files(root, entry);
        var held = DesktopConfigurationAssetFile.Read(files.HeldPath);
        if (held is not null && entry.Held is null)
        {
            entry = entry with { Held = held }; persist(entry);
        }
        if (held is not null && held != entry.Held) throw NeedsVerification();
        var target = DesktopConfigurationAssetFile.Read(files.Target);
        var stage = DesktopConfigurationAssetFile.Read(files.StagePath);
        var output = Output(entry);
        // 先持久回滚捕获责任, 再移出真实目标, 最后核验实际被移出的内容; 末检后的外部替换同样保全.
        if (target is not null && output is not null && target == output)
        {
            if (stage is not null) throw NeedsVerification();
            entry = entry with { Retiring = true }; persist(entry);
            stage = files.RetireOutput();
            entry = entry with { Retired = stage }; persist(entry);
            target = null;
        }
        if (entry.Retiring && stage is not null && stage != output)
        {
            if (entry.Retired is not null && entry.Retired != stage) throw NeedsVerification();
            entry = entry with { Retired = stage }; persist(entry);
            if (target is null) files.ReturnStage(stage);
            throw NeedsVerification();
        }
        if (held is not null)
        {
            if (target is not null) throw NeedsVerification();
            files.RestoreHeld(held);
        }
        else if (entry.BeforeHash is not null && target != Before(entry) && target != entry.Held) throw NeedsVerification();
        else if (entry.BeforeHash is null && target is not null && stage is null) throw NeedsVerification();
        if (stage is not null)
        {
            if (output is null || stage != output) throw NeedsVerification();
            files.DeleteStage(output);
        }
    }

    /// <summary>提交完成后清除仍可证明为本轮原文件/输出的唯一槽, 失败保留completed回执中的责任.</summary>
    private static void CleanupSlots(Receipt value)
    {
        foreach (var entry in value.Entries)
        {
            var files = Files(value.Root, entry);
            if (DesktopConfigurationAssetFile.Read(files.HeldPath) is { } held)
            {
                if (entry.Held != held || held != Before(entry)) throw NeedsVerification();
                files.DeleteHeld(held);
            }
            if (DesktopConfigurationAssetFile.Read(files.StagePath) is { } stage)
            {
                if (Output(entry) != stage) throw NeedsVerification();
                files.DeleteStage(stage);
            }
        }
    }

    /// <summary>清理失败只返回原异常与已持久责任, 已完成模型事实不回退为未知状态.</summary>
    private static Exception? TryCleanupSlots(string transactionRoot, Receipt value)
    {
        try { CleanupSlots(value); MarkCleanupComplete(transactionRoot, value); return null; }
        catch (Exception cleanup) { cleanup.Data["DesktopConfigurationAssetCleanupPending"] = true; return cleanup; }
    }

    /// <summary>持有全局guard时仅枚举当前根前缀的未消费证明, latest覆盖不丢旧责任, 单轮失败不掩盖其他可清理轮.</summary>
    private static Exception? RetryCompletedCleanupCore(string transactionRoot, string verifiedRoot)
    {
        Exception? first = null;
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transactionRoot))!, "configuration-assets-verified");
            UserDataStore.Check(directory);
            if (!Directory.Exists(directory)) return null;
            foreach (var path in Directory.EnumerateFiles(directory, RootReceiptKey(verifiedRoot) + ".*.cleanup.json", SearchOption.TopDirectoryOnly).ToArray())
            {
                try
                {
                    var known = ReadOptionalReceipt(path) ?? throw NeedsVerification();
                    if (known.Phase != "completed" || !Path.GetFullPath(verifiedRoot).Equals(known.Root, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFullPath(path).Equals(VerifiedReceiptPath(transactionRoot, known, true), StringComparison.OrdinalIgnoreCase)) throw NeedsVerification();
                    if (TryCleanupSlots(transactionRoot, known) is { } cleanup) RecordCleanupFailure(ref first, cleanup);
                }
                catch (Exception cleanup)
                {
                    RecordCleanupFailure(ref first, cleanup);
                }
            }
        }
        catch (Exception cleanup) { RecordCleanupFailure(ref first, cleanup); }
        return first;
    }

    /// <summary>保留最先清理异常的原始堆栈并附加后续错误, 不重新throw该对象或以最后失败覆盖首因.</summary>
    private static void RecordCleanupFailure(ref Exception? first, Exception cleanup)
    {
        cleanup.Data["DesktopConfigurationAssetCleanupPending"] = true;
        if (first is null) first = cleanup;
        else
        {
            var additional = first.Data["DesktopConfigurationAdditionalCleanupFailed"] as List<Exception> ?? [];
            additional.Add(cleanup); first.Data["DesktopConfigurationAdditionalCleanupFailed"] = additional;
        }
    }

    /// <summary>所有可证明槽已清理后仅更名同内容终态证明, 删除候选身份而不删除业务/未知事实.</summary>
    private static void MarkCleanupComplete(string transactionRoot, Receipt value)
    {
        var pending = VerifiedReceiptPath(transactionRoot, value, true);
        var clean = VerifiedReceiptPath(transactionRoot, value);
        var queued = ReadOptionalReceipt(pending);
        if (queued is null) return;
        if (queued.Phase != "completed" || !SamePlan(queued, value)) throw NeedsVerification();
        if (ReadOptionalReceipt(clean) is { } completed)
        {
            if (completed.Phase != "completed" || !SamePlan(completed, value)) throw NeedsVerification();
            UserDataStore.Check(pending); File.Delete(pending);
        }
        else File.Move(pending, clean, false);
    }

    /// <summary>工程根只作宿主内部索引, 不泄露路径或把历史根代替当前官方绑定.</summary>
    private static string RootReceiptKey(string root) => Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()));

    /// <summary>原资产缺失用null表示, 存在则同时核对原字节和文件身份.</summary>
    private static DesktopConfigurationAssetState? Before(Entry entry) => entry.BeforeHash is null ? null : new(entry.BeforeHash, entry.BeforeIdentity!);
    /// <summary>输出在rename前持久身份后才可认领, 未完成的stage不以文件名或相同字节证明归属.</summary>
    private static DesktopConfigurationAssetState? Output(Entry entry) => entry.AfterHash is null || entry.AfterIdentity is null ? null : new(entry.AfterHash, entry.AfterIdentity);

    /// <summary>核验目标前值与冻结源全文, 删除只能以 null 源和 null 后值表达.</summary>
    private static void RequireBefore(string target, ModelConfigurationAsset asset)
    {
        if (FileHash(target) != asset.BeforeHash) throw Stale();
        if (asset.AfterHash is null)
        {
            if (asset.SourceFile is not null || asset.BeforeHash is null || asset.Bytes != 0) throw Invalid();
        }
        else
        {
            if (asset.SourceFile is null) throw Invalid();
            UserDataStore.Check(asset.SourceFile);
            if (!File.Exists(asset.SourceFile) || new FileInfo(asset.SourceFile).Length != asset.Bytes || FileHash(asset.SourceFile) != asset.AfterHash) throw Stale();
        }
    }

    /// <summary>同时核对准备时的卷/文件身份和原字节, 同内容替换不能沿用已确认的目标.</summary>
    private void RequirePreparedBefore(ModelConfigurationAsset asset)
    {
        var target = AssetPath(receipt.Root, asset.RelativePath);
        RequireBefore(target, asset);
        if (FileIdentity(target) != receipt.Entries.Single(entry => entry.RelativePath == asset.RelativePath).BeforeIdentity) throw Stale();
    }

    /// <summary>复核所有资产输出, 任一差异不得将未知提交标记为完成.</summary>
    private static void RequireAfter(Receipt value)
    {
        foreach (var entry in value.Entries)
        {
            var target = AssetPath(value.Root, entry.RelativePath);
            if (FileHash(target) != entry.AfterHash || FileIdentity(target) != entry.AfterIdentity
                || (entry.AfterHash is not null && entry.AfterIdentity is null)) throw NeedsVerification();
        }
    }

    /// <summary>只解析本功能 JSON 或配置 CSV, 不允许 PBIP 模型或无关文件副作用.</summary>
    private static string AssetPath(string root, string relative)
    {
        if (relative != "pbi-utils/config.json" && !(relative.StartsWith("data/config/", StringComparison.Ordinal)
            && relative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && relative[12..].Length > 4 && !relative[12..].Contains('/'))
            || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is ".." or "." or "")) throw Invalid();
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        UserDataStore.Check(path); return path;
    }

    /// <summary>文件不存在返回 null, 普通读取错误不降级为不存在.</summary>
    private static string? FileHash(string path)
        => DesktopConfigurationAssetFile.Read(path)?.Hash;
    /// <summary>仅对已核实的普通文件读取稳定卷/文件身份, 缺失保持 null.</summary>
    private static string? FileIdentity(string path) => DesktopConfigurationAssetFile.Read(path)?.Identity;
    /// <summary>仅从允许资产目标与冻结GUID派生本事务两个同目录槽.</summary>
    private static DesktopConfigurationAssetFile Files(string root, Entry entry) => new(AssetPath(root, entry.RelativePath), entry.Slot);
    /// <summary>计算精确原字节摘要, 不记录任何业务正文.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>固定输入错误不包含任何实际路径.</summary>
    private static AppException Invalid() => new("CONFIGURATION_ASSET_PATH_INVALID", "配置资产计划无效或路径不在允许范围", 3);
    /// <summary>确认后漂移必须重新准备, 不能复用旧资产计划.</summary>
    private static AppException Stale() => new("STALE_BASELINE", "配置资产在确认后发生变化, 未覆盖", 3);
    /// <summary>未知提交只允许核验, 不能自动回滚或重放.</summary>
    private static AppException NeedsVerification() => new("EXECUTION_NEEDS_VERIFICATION", "上次配置资产或模型提交结果尚未核实, 请重新读取核验; 不得重复提交", 4);
}
