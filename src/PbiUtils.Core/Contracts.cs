// FilePath    : pbi-utils\src\PbiUtils.Core\Contracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义稳定应用错误与统一产品版本访问方式.

namespace PbiUtils.Core;

/// <summary>可安全展示的稳定错误, 不包含内部异常或用户路径.</summary>
public sealed class AppException(string code, string message, int exitCode = 2) : Exception(message)
{
    /// <summary>机器可读错误码.</summary>
    public string Code { get; } = code;
    /// <summary>产品退出码.</summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>由统一构建版本派生产品版本.</summary>
public static class Product
{
    /// <summary>产品 SemVer, 不包含可选构建元数据.</summary>
    public static string Version => typeof(Product).Assembly.GetCustomAttributes(false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
}
