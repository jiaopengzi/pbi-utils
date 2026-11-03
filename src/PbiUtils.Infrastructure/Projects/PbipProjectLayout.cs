// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Projects\PbipProjectLayout.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从已核验 PBIP 入口固定解析项目根, 不读取模型参数或搜索任意祖先.

using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Projects;

/// <summary>模型文件清单仍相对于 PBIP 父目录, 本类型只统一整树事务和资产的项目根.</summary>
public static class PbipProjectLayout
{
    /// <summary>只识别入口所在目录和固定的 pbip 父级, 不以模型中的路径参数扩大文件访问范围.</summary>
    /// <param name="projectPath">宿主已绑定的具体本机 PBIP 文件入口.</param>
    /// <returns>规范化绝对项目根; 入口父目录名为 pbip 时提升一级, 否则使用入口父目录.</returns>
    /// <exception cref="AppException">入口不是已存在的 PBIP, 或入口及祖先包含网络路径或链接.</exception>
    public static string ProjectRoot(string projectPath)
    {
        var entry = Path.GetFullPath(projectPath);
        if (entry.StartsWith("\\\\", StringComparison.Ordinal))
            throw new AppException("UNSAFE_PROJECT_PATH", "仅支持本机目录", 3);
        if (!Path.GetExtension(entry).Equals(".pbip", StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            throw new AppException("PBIP_REQUIRED", "需要已存在的具体 PBIP 入口", 3);
        ChangeWorkspaceFiles.CheckNoLinks(entry);
        var directory = Path.GetDirectoryName(entry)!;
        // Windows 目录名称不区分大小写; 只允许固定的一次提升, 不搜索同名祖先或读取 M 参数.
        return Path.GetFileName(directory).Equals("pbip", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? throw new AppException("UNSAFE_PROJECT_PATH", "项目根目录无效", 3)
            : directory;
    }
}
