// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\UdfRepositoryContent.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 两种 Git 获取适配共用的仓库路径与 LFS 占位内容边界, 不获取外部对象.

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>只核查仓库内存身份与普通文件内容, 不连接网络或展开工作区.</summary>
internal static class UdfRepositoryContent
{
    /// <summary>仓库路径仅为内存身份, 仍拒绝越界、分隔符歧义、控制字符和无界名称.</summary>
    /// <param name="path">固定提交树或历史中已经解析的仓库相对路径.</param>
    /// <exception cref="PbiUtils.Core.AppException">路径不满足原仓库安全边界时拒绝.</exception>
    internal static void ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.Length > 255)) throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>拒绝以精确 LFS 版本标记行开头的占位内容, 覆盖所有普通文件且不继续下载外部对象.</summary>
    /// <param name="content">已完成原获取与对象哈希校验的普通文件内容.</param>
    /// <returns>精确标记行或其单独结尾为 true, 标记后普通文本不被误认.</returns>
    internal static bool IsLfsPointer(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> marker = "version https://git-lfs.github.com/spec/v1"u8;
        if (!content.StartsWith(marker)) return false;
        var remainder = content[marker.Length..];
        return remainder.IsEmpty || remainder.StartsWith("\n"u8) || remainder.StartsWith("\r\n"u8);
    }
}
