// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldWorkspaceFiles.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 核验脚手架文件清单、预览文本和隔离目录归属, 不消费发布回执.

using PbiUtils.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>磁盘操作复用同一字节预算, 发布状态与恢复仍由 ScaffoldWorkspace 拥有.</summary>
internal sealed class ScaffoldWorkspaceFiles(long maximumBytes)
{
    /// <summary>对每个节点先检查链接再进入, 流式计算有界文件摘要.</summary>
    internal ScaffoldFileRecord[] Inventory(string directory, CancellationToken token, bool allowGeneratedGit = false)
    {
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) throw new AppException("SCAFFOLD_STAGE_MISSING", "预览产物已不存在, 请重新预览", 3);
        var pending = new Stack<string>();
        pending.Push(directory);
        List<ScaffoldFileRecord> files = [];
        long bytes = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            UserDataStore.Check(current);
            foreach (var path in Directory.GetFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                UserDataStore.Check(path);
                if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    // 只豁免显式初始化范围的元数据; 根目录、其他子树、文件形式及链接继续拒绝.
                    if (allowGeneratedGit && Path.GetRelativePath(directory, path).Replace('\\', '/').Equals("pbip/.git", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(path)) continue;
                    throw new AppException("SCAFFOLD_FILES_CHANGED", "预览目录不允许包含 Git 元数据, 请重新预览", 3);
                }
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                    continue;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = stream.Length;
                bytes = checked(bytes + length);
                if (bytes > maximumBytes || files.Count >= 20000) throw new AppException("SCAFFOLD_SIZE_LIMIT", "脚手架产物超过允许大小, 请缩小数据范围", 3);
                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (stream.Length != length) throw new AppException("SCAFFOLD_FILES_CHANGED", "预览文件在读取期间变化, 请重新预览", 3);
                files.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), hash, length));
            }
        }
        return [.. files.OrderBy(file => file.Path, StringComparer.Ordinal)];
    }

    /// <summary>只把变更的有界模型文本加入预览, 数据和二进制只保留清单.</summary>
    internal static ScaffoldFileRecord PreviewText(ScaffoldFileRecord file, ScaffoldTemplateFile? before, string staging)
    {
        var extension = Path.GetExtension(file.Path).ToLowerInvariant();
        if (extension is not (".tmdl" or ".json" or ".pbip" or ".pbir" or ".pbism" or ".bim" or ".md") || file.Bytes > 128 * 1024) return file;
        if (before is not null && Convert.ToHexStringLower(SHA256.HashData(before.Content)) == file.Sha256) return file;
        var oldText = before is { Content.Length: <= 131072 } ? Encoding.UTF8.GetString(before.Content) : null;
        return file with { Before = oldText, After = File.ReadAllText(Path.Combine(staging, file.Path), Encoding.UTF8) };
    }

    /// <summary>清单内容或文件数不同均拒绝使用旧预览.</summary>
    internal static void AssertInventory(ScaffoldPlan plan, ScaffoldFileRecord[] current)
    {
        if (plan.Files.Count != current.Length || !plan.Files.Zip(current).All(pair => pair.First.Path == pair.Second.Path
            && pair.First.Sha256 == pair.Second.Sha256 && pair.First.Bytes == pair.Second.Bytes))
            throw new AppException("SCAFFOLD_FILES_CHANGED", "预览产物已被修改, 请重新预览", 3);
    }

    /// <summary>词法规范化不替代实际访问前的链接检查.</summary>
    internal static string Output(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Contains('\0')) throw new AppException("INVALID_OUTPUT", "请选择有效的本地输出目录");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || !Path.GetFileName(full).Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new AppException("INVALID_OUTPUT", "输出必须是与项目名称一致的本机新目录");
        return full;
    }

    /// <summary>固定随机目录名从计划派生, 不读取客户端指定的 staging.</summary>
    internal static string Stage(string output, string id) => Path.Combine(Path.GetDirectoryName(output)!, ".pbi-utils-scaffold-" + id);

    /// <summary>验证每个删除节点及祖先, 不递归跟随链接或删除最终工程.</summary>
    internal static void DeleteStage(string staging)
    {
        if (!Path.GetFileName(staging).StartsWith(".pbi-utils-scaffold-", StringComparison.Ordinal)) throw new AppException("SCAFFOLD_NEEDS_VERIFICATION", "项目提交状态需核验, 请检查已生成目录, 不会重复创建", 3);
        UserDataStore.Check(staging);
        if (!Directory.Exists(staging)) return;
        foreach (var child in Directory.GetFileSystemEntries(staging))
        {
            UserDataStore.Check(child);
            if (Directory.Exists(child)) DeleteChildren(child);
            else File.Delete(child);
        }
        UserDataStore.Check(staging);
        Directory.Delete(staging);
    }

    /// <summary>只清理已确认归属 staging 内的普通子树.</summary>
    internal static void DeleteChildren(string directory)
    {
        UserDataStore.Check(directory);
        foreach (var child in Directory.GetFileSystemEntries(directory))
        {
            UserDataStore.Check(child);
            if (Directory.Exists(child)) DeleteChildren(child);
            else File.Delete(child);
        }
        UserDataStore.Check(directory);
        Directory.Delete(directory);
    }

    /// <summary>新建仓库不能未经说明嵌套在既有仓库内.</summary>
    internal static bool HasGitAncestor(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git"))) return true;
        return false;
    }

    /// <summary>验证入口是工程内相对文件, 不接受绝对路径或父级片段.</summary>
    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or "")) throw new AppException("SCAFFOLD_NEEDS_VERIFICATION", "项目提交状态需核验, 请检查已生成目录, 不会重复创建", 3);
        return value;
    }
}
