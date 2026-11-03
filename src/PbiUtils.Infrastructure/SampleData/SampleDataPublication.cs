// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\SampleDataPublication.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证示例文件基线并事务发布两子树, 回滚失败保留恢复现场.

using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;
using static PbiUtils.Infrastructure.SampleData.SampleOutputDirectory;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>只接受生成 owner 冻结的基线和提交回调, 不重新生成或跳过取消边界.</summary>
internal static class SampleDataPublication
{
    /// <summary>散列两个产物子树的路径, 空目录和文件字节, 防止生成期间覆盖外部修改.</summary>
    internal static string Fingerprint(string root, CancellationToken cancellationToken)
    {
        ValidateOutput(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "data", "pbip" })
        {
            var directory = Path.Combine(root, name);
            hash.AppendData(Encoding.UTF8.GetBytes(name + (Directory.Exists(directory) ? "/present\0" : "/absent\0")));
            if (!Directory.Exists(directory)) continue;
            foreach (var entry in Walk(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, entry) + (Directory.Exists(entry) ? "/\0" : "\0")));
                if (File.Exists(entry))
                {
                    using var stream = File.OpenRead(entry);
                    hash.AppendData(SHA256.HashData(stream));
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>提交前拒绝外部修改和取消, 供异步回执前后复核同一基线.</summary>
    /// <param name="root">宿主冻结的安全输出目录.</param>
    /// <param name="baseline">开始生成前冻结的完整两子树指纹.</param>
    /// <param name="cancellationToken">尚未进入提交时响应的取消令牌.</param>
    /// <exception cref="AppException">外部修改导致基线变化时拒绝提交.</exception>
    internal static void ValidateBaseline(string root, string baseline, CancellationToken cancellationToken)
    {
        if (Fingerprint(root, cancellationToken) != baseline) throw new AppException("SAMPLE_BASELINE_CHANGED", "示例目录在生成期间发生改变, 已保留原目录, 请刷新后重试.", 4);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>基线相同才移入产物; 两子树失败按相反顺序恢复, 恢复失败保留现场.</summary>
    internal static void Publish(string root, string stage, string baseline, Action? beginCommit, ref bool retainStage, CancellationToken cancellationToken)
    {
        ValidateBaseline(root, baseline, cancellationToken);
        beginCommit?.Invoke();
        Directory.CreateDirectory(root);
        var moved = new List<(string Target, string Backup, bool Installed)>();
        try
        {
            foreach (var name in new[] { "data", "pbip" })
            {
                var target = Path.Combine(root, name);
                var source = Path.Combine(stage, name);
                var backup = Path.Combine(stage, "rollback-" + name);
                if (Directory.Exists(target)) Directory.Move(target, backup);
                moved.Add((target, backup, false));
                if (Directory.Exists(source))
                {
                    Directory.Move(source, target);
                    moved[^1] = (target, backup, true);
                }
            }
        }
        catch
        {
            try
            {
                foreach (var (Target, Backup, Installed) in moved.AsEnumerable().Reverse())
                {
                    if (Installed) Directory.Move(Target, Path.Combine(stage, "failed-" + Path.GetFileName(Target)));
                    if (Directory.Exists(Backup)) Directory.Move(Backup, Target);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                retainStage = true;
                throw new AppException("SAMPLE_RECOVERY_REQUIRED", "发布与自动恢复均未完成, 已保留恢复目录, 请勿重新生成并检查目标目录.", 4);
            }
            throw;
        }
    }
}
