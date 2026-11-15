// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Diagnostics\RuntimeEnvironment.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 适配 Windows 运行环境信息与用户级产品数据目录.

using PbiUtils.Application.Diagnostics;
using System.Runtime.InteropServices;
using PbiUtils.Application;

namespace PbiUtils.Infrastructure.Diagnostics;

/// <summary>Windows 只读环境和用户目录适配器.</summary>
/// <param name="powerBiSupported">宿主是否注册了本机 Power BI 只读适配器.</param>
public sealed class RuntimeEnvironment(bool powerBiSupported = false) : IRuntimeEnvironment
{
    /// <inheritdoc />
    public RuntimeSnapshot Read() => new(RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
        "available-platform", powerBiSupported ? "read-only-discovery" : "not-available-in-this-host");

    /// <summary>获取用户级数据目录, 不创建目录或写入安装路径.</summary>
    /// <returns>当前用户的产品数据目录.</returns>
    public static string UserDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils");

    /// <summary>从固定的用户数据根定位共享运行协调, 避免客户端的 AppData 重定向分裂锁域.</summary>
    /// <param name="userRoot">当前组合根使用的用户数据目录.</param>
    /// <returns>数据根下的 runtime 目录; 不创建目录.</returns>
    internal static string RuntimeCoordinationDirectory(string userRoot) => Path.Combine(userRoot, "runtime");
}
