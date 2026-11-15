// FilePath    : pbi-utils\scripts\tests\ScriptTestRepository.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从测试程序集定位独立 PowerShell 场景所使用的仓库.

namespace PbiUtils.ScriptTests;

/// <summary>为不同的 PowerShell 测试类提供无共享可变状态的仓库定位.</summary>
internal static class ScriptTestRepository
{
    /// <summary>从测试输出目录查找仓库标记, 不依赖调用目录.</summary>
    /// <returns>包含 run.ps1 的仓库根目录.</returns>
    /// <exception cref="InvalidOperationException">测试输出目录不在可识别的仓库中.</exception>
    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "run.ps1"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库");
    }
}
