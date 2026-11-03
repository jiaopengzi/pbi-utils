// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Projects\ChangeWorkspaceFiles.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用 PBIP 文件快照、路径核验与逐文件恢复, 不持有准备计划或提交状态.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Projects;
using System.Security.Cryptography;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Projects;

/// <summary>文件操作只消费 owner 提供的已冻结事实, 不决定模型写入权限或提交时机.</summary>
internal static class ChangeWorkspaceFiles
{
    /// <summary>大资产逐块复制到同目录临时文件后替换, 不将 CSV 整块载入内存.</summary>
    internal static void CopyAtomic(string from, string destination)
    {
        UserDataStore.Check(from); UserDataStore.Check(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output, 65536); output.Flush(true); }
            UserDataStore.Check(destination); UserDataStore.Check(temporary); File.Move(temporary, destination, true);
        }
        finally { UserDataStore.Check(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>回滚从本轮已验证的磁盘备份流式恢复, 外部改写继续保留并报待核验.</summary>
    internal static bool RestoreWrittenFilesFromBackup(string source, string backup, IReadOnlyList<FileChange> changes, IReadOnlyList<string> written)
    {
        var complete = true;
        var byPath = changes.ToDictionary(change => change.Path, StringComparer.Ordinal);
        foreach (var file in written.Reverse())
        {
            try
            {
                var destination = Path.Combine(source, file); UserDataStore.Check(destination);
                var change = byPath[file];
                var current = File.Exists(destination) ? FileHash(destination, CancellationToken.None) : null;
                if (current == change.BeforeHash) continue;
                if (current != change.AfterHash) { complete = false; continue; }
                if (change.BeforeHash is null) { if (File.Exists(destination)) File.Delete(destination); continue; }
                var original = Path.Combine(backup, file); UserDataStore.Check(original);
                if (!File.Exists(original) || FileHash(original, CancellationToken.None) != change.BeforeHash) { complete = false; continue; }
                CopyAtomic(original, destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException) { complete = false; }
        }
        return complete;
    }

    /// <summary>恢复本轮已开始替换的文件; 外部内容变化必须保留并交由人工核验.</summary>
    internal static bool RestoreWrittenFiles(string source, IReadOnlyList<FileChange> changes, IReadOnlyDictionary<string, byte[]?> originals, IReadOnlyList<string> written)
    {
        var complete = true;
        var byPath = changes.ToDictionary(change => change.Path, StringComparer.Ordinal);
        foreach (var file in written.Reverse())
        {
            try
            {
                var destination = Path.Combine(source, file);
                UserDataStore.Check(destination);
                var change = byPath[file];
                var current = File.Exists(destination) ? Hash(File.ReadAllBytes(destination)) : null;
                // 替换未发生或已恢复的文件不再触碰; 锁住的第 N 文件不会阻断其他文件恢复.
                if (current == change.BeforeHash) continue;
                // 只有仍等于本次写入结果的文件允许恢复, 外部新内容必须原样保留.
                if (current != change.AfterHash) { complete = false; continue; }
                if ((originals[file] is { } original ? Hash(original) : null) != change.BeforeHash) { complete = false; continue; }
                if (originals[file] is { } bytes) UserDataStore.Write(destination, bytes);
                else if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException) { complete = false; }
        }
        return complete;
    }

    /// <summary>从完整项目根枚举相对文件, 排除缓存和本地设置; 同级 data CSV 单独计入资产预算.</summary>
    /// <param name="root">由 PBIP 入口固定解析的完整项目根, 不以模型清单相对根替代.</param>
    /// <param name="token">目录枚举和逐块计算指纹时检查的取消令牌.</param>
    /// <returns>相对于项目根的完整文件指纹, 文件新增和删除均参与并发复核.</returns>
    /// <exception cref="AppException">链接或预算不安全, 不返回部分文件清单.</exception>
    internal static Dictionary<string, string> Inventory(string root, CancellationToken token)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        long size = 0;
        long definitionBytes = 0;
        void Walk(string directory)
        {
            token.ThrowIfCancellationRequested();
            CheckNoLinks(directory);
            foreach (var path in Directory.GetFiles(directory))
            {
                CheckNoLinks(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Path.GetFileName(path) is "cache.abf" or "localSettings.json") continue;
                size += new FileInfo(path).Length;
                var asset = relative.StartsWith("data/", StringComparison.Ordinal) && Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);
                if (!asset) definitionBytes += new FileInfo(path).Length;
                if (size > 8L * 1024 * 1024 * 1024 || definitionBytes > 128L * 1024 * 1024 || result.Count >= 10000)
                    throw new AppException("PROJECT_TOO_LARGE", "工程定义或资产超出独立大小限制", 3);
                result.Add(relative, FileHash(path, token));
            }
            foreach (var child in Directory.GetDirectories(directory)) Walk(child);
        }
        Walk(root);
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
    /// <summary>完整文件清单必须一致, 包括新增和删除.</summary>
    internal static void AssertSame(Dictionary<string, string> expected, Dictionary<string, string> current)
    {
        if (expected.Count != current.Count || expected.Any(p => current.GetValueOrDefault(p.Key) != p.Value))
            throw new AppException("STALE_BASELINE", "源工程或 staging 已改变, 请重新预览", 3);
    }
    /// <summary>只为文本定义提供原始 diff, 二进制仅展示 hash.</summary>
    internal static string? Text(string root, string file, Dictionary<string, string> files) => files.ContainsKey(file)
        && Path.GetExtension(file) is ".json" or ".tmdl" or ".bim" or ".pbip" or ".pbir" or ".pbism" ? File.ReadAllText(Path.Combine(root, file)) : null;
    /// <summary>计算文件或计划指纹.</summary>
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>所有整树清单均逐块计算, 不按文件大小分配内存.</summary>
    internal static string FileHash(string path, CancellationToken token) => ModelConfigurationOwnership.FileHash(path, token);
    /// <summary>判断规范路径是否位于根中.</summary>
    internal static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var r && r != ".." && !r.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(r);
    /// <summary>只解析 staging 内已存在的相对绑定.</summary>
    internal static string Resolve(string from, string relative, string root)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new AppException("UNSAFE_PROJECT_PATH", "不支持绝对绑定");
        var path = Path.GetFullPath(Path.Combine(from, relative));
        if (!Inside(root, path)) throw new AppException("UNSAFE_PROJECT_PATH", "路径超出工程");
        CheckNoLinks(path);
        return path;
    }
    /// <summary>不跟随目标或父级的文件链接, 网络目录不作为发布根.</summary>
    internal static void CheckNoLinks(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new AppException("UNSAFE_PROJECT_PATH", "仅支持本机目录", 3);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AppException("UNSAFE_PROJECT_PATH", "路径包含链接或重解析点", 3);
    }
}
