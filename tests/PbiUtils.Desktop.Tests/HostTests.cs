// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\HostTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Windows 宿主程序集的生产与开发来源边界.

using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>实际 Windows 宿主程序集的边界检查, 不等同交互验收.</summary>
public sealed class HostTests
{
    /// <summary>Windows 宿主不允许 Release 来源被环境配置覆盖.</summary>
    [Fact]
    public void ReleaseRejectsDevelopmentOrigin()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Throws<InvalidOperationException>(() => new OriginPolicy(false, OriginPolicy.Development));
        Assert.Equal(OriginPolicy.Production, new OriginPolicy(false, null).Origin);
    }
}
