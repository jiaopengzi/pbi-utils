// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\SampleOutputDirectory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 集中示例输出目录和隔离树策略, 不保存进度或决定发布时机.

using PbiUtils.Core;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>纯路径规范化与磁盘节点检查保持分离, 所有遍历拒绝链接.</summary>
internal static class SampleOutputDirectory
{
    /// <summary>拒绝根目录, 相对路径, 设备路径和重解析节点, 子树扫描不跟随链接.</summary>
    internal static string ValidateOutput(string outputPath)
    {
        var root = NormalizeOutput(outputPath);
        if (File.Exists(root)) throw InvalidOutput();
        for (var directory = root; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)) RejectLink(directory);
        foreach (var name in new[] { "data", "pbip" })
        {
            var child = Path.Combine(root, name);
            RejectLink(child);
            if (File.Exists(child)) throw InvalidOutput();
            if (Directory.Exists(child)) foreach (var entry in Walk(child)) RejectLink(entry);
        }
        return root;
    }

    /// <summary>仅按文本规范化有界绝对目录并拒绝非法段, 不访问磁盘或网络位置.</summary>
    /// <param name="outputPath">宿主或 CLI 明确提供的输出目录.</param>
    /// <returns>可用于目录门闩的规范化路径; 非法输入抛出稳定应用错误.</returns>
    internal static string NormalizeOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || outputPath.Length > 240 || !Path.IsPathFullyQualified(outputPath) ||
            outputPath.StartsWith(@"\\?\", StringComparison.Ordinal) || outputPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            outputPath.Any(char.IsControl)) throw InvalidOutput();
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw InvalidOutput(); }
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase)) throw InvalidOutput();
        var parts = root[Path.GetPathRoot(root)!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part))) throw InvalidOutput();
        return root;
    }

    /// <summary>每一步先检查链接, 再枚举下层, 不让递归跨出目标树.</summary>
    internal static IEnumerable<string> Walk(string directory)
    {
        RejectLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            yield return entry;
            if (Directory.Exists(entry)) foreach (var child in Walk(entry)) yield return child;
        }
    }

    /// <summary>不存在的节点允许创建, 已存在文件和目录均拒绝重解析属性.</summary>
    internal static void RejectLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw InvalidOutput(); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>拒绝 Win32 保留设备段, 包含带扩展名与上标数字变体.</summary>
    internal static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(name[3]);
    }

    /// <summary>只在目标同盘父目录创建唯一隔离目录, 不复用已有用户目录.</summary>
    internal static string CreateStage(string root)
    {
        var parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, ".pbi-utils-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>复制整个已核验子树, 包括用户额外条目与必要隐藏文件.</summary>
    internal static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Walk(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, entry));
            if (Directory.Exists(entry)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target);
            }
        }
    }

    /// <summary>拒绝不明确或会越过边界的输出位置.</summary>
    internal static AppException InvalidOutput() => new("SAMPLE_INVALID_OUTPUT", "请选择非根目录的本地绝对路径, 且目录不能包含链接或重解析节点.", 3);
}
