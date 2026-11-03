// FilePath    : pbi-utils\src\PbiUtils.Application\Diagnostics\DoctorService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义运行环境适配边界并实现共享只读诊断用例.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;

namespace PbiUtils.Application.Diagnostics;

/// <summary>已使用的运行环境适配边界.</summary>
public interface IRuntimeEnvironment
{
    /// <summary>只读获取环境数据.</summary>
    /// <returns>不包含敏感路径的运行环境.</returns>
    RuntimeSnapshot Read();
}

/// <summary>平台诊断信息; unsupported 不表示核心失败.</summary>
public sealed record RuntimeSnapshot(string Os, string Architecture, string Runtime, string Desktop, string PowerBi);
/// <summary>带版本与能力状态的诊断结果.</summary>
public sealed record DoctorResult(string Version, RuntimeSnapshot Environment);

/// <summary>共享只读诊断用例.</summary>
/// <param name="environment">宿主环境适配器.</param>
public sealed class DoctorService(IRuntimeEnvironment environment, ILogger<DoctorService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<DoctorService>.Instance;

    /// <summary>执行只读检查.</summary>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>环境和产品版本.</returns>
    public DoctorResult Execute(CancellationToken cancellationToken = default) => BusinessLog.Run<DoctorResult>(log, "Doctor.Execute", () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Product.Version, environment.Read());
    });
}
