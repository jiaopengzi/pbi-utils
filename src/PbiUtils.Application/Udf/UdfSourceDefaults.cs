// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfSourceDefaults.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一官方 GitHub、固定 Gitee 镜像与本地函数来源的默认配置.

namespace PbiUtils.Application.Udf;

/// <summary>宿主与来源登记共享同一内置身份, 不从远端配置推断默认来源.</summary>
public static class UdfSourceDefaults
{
    /// <summary>官方公开仓库使用默认分支, 更新时再固定具体提交.</summary>
    public static UdfSourceEntry Official { get; } = new("daxlib", "DAXLib", "https://github.com/daxlib/daxlib", null, "builtin", "git");

    /// <summary>仅用于内置来源的网络切换, 不作为独立来源登记.</summary>
    public static UdfSourceEntry DaxLibMirror { get; } = Official with { BaseUrl = "https://gitee.com/jiaopengzi/daxlib-mirror", GitRef = "main" };

    /// <summary>本地函数保持独立存储和编辑路径, 不使用远端地址.</summary>
    public static UdfSourceEntry Local { get; } = new("local", "Local", null, null, "local");

    /// <summary>固定顺序的不可变内置登记, 调用者只能读取.</summary>
    public static IReadOnlyList<UdfSourceEntry> Builtins { get; } = Array.AsReadOnly<UdfSourceEntry>([Official, Local]);
}
