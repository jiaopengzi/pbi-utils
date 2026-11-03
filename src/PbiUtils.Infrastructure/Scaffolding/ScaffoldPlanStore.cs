// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldPlanStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 受限持久计划及真实文件清单, 只访问由摘要和宿主派生的固定位置.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

using PbiUtils.Application.Input;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>状态在跨进程锁内推进, 不将 staging 路径作为可编辑输入.</summary>
internal sealed record ScaffoldPlanReceipt(int SchemaVersion, ScaffoldPlan Plan, string State, ScaffoldResult? Result = null);

/// <summary>记录格式和目录由产品控制, 损坏时保留文件并拒绝继续.</summary>
internal sealed class ScaffoldPlanStore(string userRoot)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string root = Path.Combine(Path.GetFullPath(userRoot), "scaffold", "plans");
    private string sweepCursor = Guid.NewGuid().ToString("N");

    /// <summary>每次最多检查64条记录, 轮转游标避免大量已发布记录长期遮挡遗弃预览.</summary>
    internal IReadOnlyList<string> SweepCandidates()
    {
        UserDataStore.Check(root);
        if (!Directory.Exists(root)) return [];
        var names = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(InputValidation.IsDigest)
            .Order(StringComparer.Ordinal).ToArray();
        var candidates = names.Where(name => string.CompareOrdinal(name, sweepCursor) > 0)
            .Concat(names.Where(name => string.CompareOrdinal(name, sweepCursor) <= 0)).Take(64).ToArray();
        if (candidates.Length > 0) sweepCursor = candidates[^1];
        return candidates;
    }

    /// <summary>按摘要取得独占文件锁, 不接受客户端任意文件名.</summary>
    internal FileStream Lease(string digest)
    {
        _ = FilePath(digest);
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        UserDataStore.Check(root);
        return UserDataStore.Lock(Path.Combine(root, digest + ".lock"));
    }

    /// <summary>验证规范记录及其不可变内容摘要.</summary>
    internal ScaffoldPlanReceipt Read(string digest)
    {
        var path = FilePath(digest);
        UserDataStore.Check(path);
        if (!File.Exists(path)) throw Invalid();
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw Invalid();
        return Parse(File.ReadAllBytes(path), digest);
    }

    /// <summary>从正式读取或重置持锁句柄取得的完整字节核验同一计划协议, 不另外打开路径.</summary>
    internal static ScaffoldPlanReceipt Parse(byte[] bytes, string digest)
    {
        if (!InputValidation.IsDigest(digest) || bytes.Length > 32 * 1024 * 1024) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            JsonInputValidation.RejectDuplicates(document.RootElement);
            var receipt = JsonSerializer.Deserialize<ScaffoldPlanReceipt>(bytes, Json) ?? throw Invalid();
            if (receipt.SchemaVersion != 1 || receipt.Plan.Digest != digest || Digest(receipt.Plan) != digest
                || receipt.Plan.Id.Length != 32 || !receipt.Plan.Id.All(char.IsAsciiHexDigit)
                || receipt.State is not ("prepared" or "committing" or "published" or "discarded")) throw Invalid();
            ScaffoldValidation.Validate(receipt.Plan.Request);
            return receipt;
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>完整重置前检查全部持久计划, prepared 可以放弃, committing 必须先恢复核验; 无效记录不能删除追溯材料.</summary>
    internal void RequireResetSafe(CancellationToken token)
    {
        UserDataStore.Check(root);
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            ValidateResetReceipt(Read(Path.GetFileNameWithoutExtension(file)));
        }
    }

    /// <summary>未知发布不能通过清除应用计划绕过恢复, 不检查外部工程或重放发布.</summary>
    internal static void ValidateResetReceipt(ScaffoldPlanReceipt receipt)
    {
        if (receipt.State == "committing") throw new AppException("EXECUTION_NEEDS_VERIFICATION", "仍有模型或文件执行结果待核验或待保存, 请先完成核验再重置", 3);
    }

    /// <summary>调用方持锁时原子写回状态, 不覆盖未知格式.</summary>
    internal void Write(ScaffoldPlanReceipt receipt) => UserDataStore.Write(FilePath(receipt.Plan.Digest), JsonSerializer.SerializeToUtf8Bytes(receipt, Json));

    /// <summary>摘要绑定计划的全部不可变字段, 状态和回执不参与重算.</summary>
    internal static string Digest(ScaffoldPlan plan) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { Digest = "" }, Json)));

    /// <summary>只接受64位小写摘要作为存储键.</summary>
    private string FilePath(string digest)
    {
        if (!InputValidation.IsDigest(digest)) throw Invalid();
        return Path.Combine(root, digest + ".json");
    }

    /// <summary>固定诊断不输出计划正文.</summary>
    private static AppException Invalid() => new("INVALID_SCAFFOLD_PLAN", "脚手架计划已失效或内容无效, 请重新预览", 3);
}
