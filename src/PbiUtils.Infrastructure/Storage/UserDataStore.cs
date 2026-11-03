// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Storage\UserDataStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一用户数据目录初始化与原子文件存储.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Storage;

/// <summary>仅在当前用户主目录初始化并管理产品数据.</summary>
public static class UserDataStore
{
    /// <summary>宿主初始化统一目录, 保留已存在的用户数据.</summary>
    public static string Initialize()
    {
        var root = RuntimeEnvironment.UserDataDirectory();
        Check(root);
        Directory.CreateDirectory(root);
        Check(root);
        return root;
    }

    /// <summary>拒绝路径本身与祖先中的链接, 不跟随重定向访问用户数据.</summary>
    public static void Check(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AppException("UNSAFE_USER_DATA_PATH", "用户数据目录包含链接", 3); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>用跨进程文件锁拒绝同时保存, 不覆盖另一个实例的数据.</summary>
    public static FileStream Lock(string path)
    {
        Check(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AppException("USER_DATA_BUSY", "另一实例正在更新用户数据, 请稍后重试", 3); }
    }

    /// <summary>同目录临时文件原子替换, 原文件在失败时保留.</summary>
    public static void Write(string path, byte[] bytes)
    {
        Check(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            Check(path); Check(temporary);
            File.Move(temporary, path, true);
        }
        finally { Check(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
