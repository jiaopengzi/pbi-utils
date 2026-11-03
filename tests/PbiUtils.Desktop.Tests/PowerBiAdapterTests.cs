// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\PowerBiAdapterTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Windows 实例发现、端口拒绝与取消, 不写入或终止用户模型.

using PbiUtils.Windows.Desktop.Discovery;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>Windows 适配器安全边界, 无需运行 Power BI 也可执行.</summary>
public sealed class PowerBiAdapterTests
{
    /// <summary>实际系统快照可读取, 只返回有效 PID 和回环端口范围.</summary>
    [Fact]
    public async Task DiscoveryReturnsOnlyValidEndpoints()
    {
        var endpoints = await new LocalPowerBiReader().DiscoverAsync(CancellationToken.None);
        Assert.All(endpoints, endpoint =>
        {
            Assert.True(endpoint.ProcessId > 0);
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Title));
            if (endpoint.Port is { } port)
            {
                Assert.InRange(port, 1, 65535);
                Assert.True(endpoint.EngineProcessId > 0);
                Assert.True(endpoint.EngineStarted > 0);
                Assert.Matches("^[a-f0-9]{64}$", endpoint.InstanceId!);
            }
            else Assert.Null(endpoint.InstanceId);
        });
    }

    /// <summary>未发现的伪造 PID 不会触发 TOM 连接, 无效端口也直接拒绝.</summary>
    [Fact]
    public async Task RejectsUnknownOrInvalidEndpoint()
    {
        var reader = new LocalPowerBiReader();
        Assert.Equal("POWER_BI_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(new(0, "", null, null, -1), CancellationToken.None))).Code);
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", (await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(new(int.MaxValue, "", int.MaxValue, 1, 1234), CancellationToken.None))).Code);
    }

    /// <summary>预先取消不得启动发现或连接.</summary>
    [Fact]
    public async Task PreCancelledDiscoveryAndRead()
    {
        var reader = new LocalPowerBiReader();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.DiscoverAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new(1, "", 1, 1, 1234), source.Token));
    }
}
