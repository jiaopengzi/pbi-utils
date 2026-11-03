// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessHostOwnershipTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在独占临时目录验证维护探测共享性、真实维护排他和文件错误边界.

using PbiUtils.Core;
using PbiUtils.Infrastructure.Hosting;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用本测试创建的协调文件, 不启动产品或访问真实用户数据.</summary>
public sealed class BusinessHostOwnershipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-host-ownership-tests-" + Guid.NewGuid().ToString("N"));
    private readonly BusinessHostSettings settings;

    /// <summary>固定本测试的安装与协调身份, 不使用用户共享后台路径.</summary>
    public BusinessHostOwnershipTests()
    {
        Directory.CreateDirectory(root);
        settings = new(root, Path.Combine(root, "installation"), Path.Combine(root, "runtime"));
    }

    /// <summary>正式协调根避开会按客户端重定向的 AppData; 测试覆盖目录不改变管道身份或安装隔离.</summary>
    [Fact]
    public void DefaultCoordinationUsesProfileAndOverridePreservesIdentity()
    {
        var current = new BusinessHostSettings(root, settings.Installation);
        var runtimeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils-runtime");
        Assert.Equal(runtimeRoot, Path.GetDirectoryName(current.CoordinationDirectory));
        Assert.False(current.CoordinationDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(root, "runtime"), settings.CoordinationDirectory);
        Assert.Equal(current.PipeName, settings.PipeName);
        Assert.NotEqual(current.CoordinationDirectory, settings.CoordinationDirectory);

        var otherInstallation = new BusinessHostSettings(root, Path.Combine(root, "other-installation"));
        var otherUserRoot = new BusinessHostSettings(Path.Combine(root, "other-user-root"), settings.Installation);
        Assert.NotEqual(current.PipeName, otherInstallation.PipeName);
        Assert.NotEqual(current.PipeName, otherUserRoot.PipeName);
        Assert.NotEqual(current.CoordinationDirectory, otherInstallation.CoordinationDirectory);
        Assert.NotEqual(current.CoordinationDirectory, otherUserRoot.CoordinationDirectory);
    }

    /// <summary>另一个只读探测保持打开时仍为空闲, 避免同时启动的客户端彼此误报维护.</summary>
    [Fact]
    public void SharedProbeDoesNotBlockOtherMaintenanceChecks()
    {
        Directory.CreateDirectory(settings.CoordinationDirectory);
        using var probe = new FileStream(Path.Combine(settings.CoordinationDirectory, "maintenance.lock"),
            FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
    }

    /// <summary>首次无文件探测返回空闲; 真实维护保持排他且可被观察, 释放后重新准入.</summary>
    [Fact]
    public void RealMaintenanceRemainsExclusiveUntilReleased()
    {
        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
        using (var maintenance = BusinessHostOwnership.TryAcquireMaintenance(settings))
        {
            Assert.NotNull(maintenance);
            Assert.True(BusinessHostOwnership.IsMaintenanceHeld(settings));
            using var contender = BusinessHostOwnership.TryAcquireMaintenance(settings);
            Assert.Null(contender);
        }

        Assert.False(BusinessHostOwnership.IsMaintenanceHeld(settings));
        using var next = BusinessHostOwnership.TryAcquireMaintenance(settings);
        Assert.NotNull(next);
    }

    /// <summary>协调目录或维护文件的类型冲突必须明确报错, 不能被误报为空闲或普通维护占用.</summary>
    /// <param name="directoryConflict">true 表示协调目录被文件占用, false 表示维护文件被目录占用.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProbePathTypeConflictRemainsAnError(bool directoryConflict)
    {
        if (directoryConflict) File.WriteAllText(settings.CoordinationDirectory, "fixture");
        else Directory.CreateDirectory(Path.Combine(settings.CoordinationDirectory, "maintenance.lock"));

        var error = Assert.Throws<AppException>(() => BusinessHostOwnership.IsMaintenanceHeld(settings));
        Assert.Equal("BUSINESS_HOST_LOCK_FAILED", error.Code);
    }

    /// <summary>所有测试句柄释放后只清理本实例创建的独占临时根.</summary>
    public void Dispose() => Directory.Delete(root, recursive: true);
}
