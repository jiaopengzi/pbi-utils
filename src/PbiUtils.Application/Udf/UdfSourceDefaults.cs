// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfSourceDefaults.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一官方 GitHub 与本地函数来源的稳定身份和默认配置.

namespace PbiUtils.Application.Udf;

/// <summary>宿主与来源登记共享同一内置身份, 不从远端配置推断默认来源.</summary>
public static class UdfSourceDefaults
{
    /// <summary>官方公开仓库使用默认分支, 更新时再固定具体提交.</summary>
    public static UdfSourceEntry Official { get; } = new("daxlib", "DAXLib", "https://github.com/daxlib/daxlib", null, "builtin", "git");

    /// <summary>本地函数保持独立存储和编辑路径, 不使用远端地址.</summary>
    public static UdfSourceEntry Local { get; } = new("local", "Local", null, null, "local");

    /// <summary>固定顺序的不可变内置登记, 调用者只能读取.</summary>
    public static IReadOnlyList<UdfSourceEntry> Builtins { get; } = Array.AsReadOnly<UdfSourceEntry>([Official, Local]);
}
