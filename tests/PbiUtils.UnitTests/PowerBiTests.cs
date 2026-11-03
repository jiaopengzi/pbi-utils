// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerBiTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享 Power BI 扫描的空态、部分失败、并发、取消与桥接输入限制.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>控制发现与读取结果, 不依赖本机安装 Power BI.</summary>
public sealed class PowerBiTests
{
    /// <summary>本地端口可访问但没有模型时不能对用户宣称可连接.</summary>
    [Fact]
    public async Task EmptyModelListIsNotConnectable()
    {
        var reader = new Reader { Endpoints = [new(1, "报表", 10, 1, 1234)] };
        var instance = Assert.Single((await new PowerBiService(reader, TimeProvider.System).ExecuteAsync()).Instances);
        Assert.Equal("unavailable", instance.Status);
        Assert.Equal("DESKTOP_NO_LOCAL_MODEL", instance.ErrorCode);
        Assert.Empty(instance.Models);
    }

    /// <summary>没有端口时只在 Bridge 已确认文件就绪后说明无模型, 未就绪仍等待.</summary>
    [Theory]
    [InlineData("verified", "unavailable")]
    [InlineData("unavailable", "starting")]
    [InlineData("unsaved", "starting")]
    public async Task MissingPortUsesVerifiedFileReadiness(string fileStatus, string expected)
    {
        var reader = new Reader { Endpoints = [new(1, "报表", null, null, null)], ReadFile = _ => new(null, fileStatus) };
        var instance = Assert.Single((await new PowerBiService(reader, TimeProvider.System).ExecuteAsync()).Instances);
        Assert.Equal(expected, instance.Status);
        Assert.Equal(0, reader.ReadCount);
    }

    /// <summary>无端口窗口在文件身份检查期间关闭不应中止其他实例扫描.</summary>
    [Fact]
    public async Task MissingPortIdentityFailureRemainsPerInstance()
    {
        var reader = new Reader
        {
            Endpoints = [new(1, "已关闭", null, null, null), new(2, "加载中", null, null, null)],
            ReadFile = endpoint => endpoint.ProcessId == 1 ? throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭", 3) : new(null, "unavailable")
        };
        var instances = (await new PowerBiService(reader, TimeProvider.System).ExecuteAsync()).Instances;
        Assert.Equal(["unavailable", "starting"], instances.Select(instance => instance.Status));
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", instances[0].ErrorCode);
    }

    /// <summary>同名实例仍分别返回平台核验的完整路径, 未核验路径不伪造为成功身份.</summary>
    [Fact]
    public async Task SameNamedInstancesKeepVerifiedPathsAndUnavailableStatus()
    {
        var reader = new Reader
        {
            Endpoints = [new(1, "same", 10, 1, 1000), new(2, "same", 20, 1, 2000), new(3, "same", 30, 1, 3000)],
            ReadFile = endpoint => endpoint.ProcessId == 3 ? new(null, "unavailable", "DESKTOP_BRIDGE_UNSUPPORTED")
                : new($"C:\\folder{endpoint.ProcessId}\\same.pbix", "verified")
        };
        var result = await new PowerBiService(reader, TimeProvider.System).ExecuteAsync();
        Assert.Equal(["C:\\folder1\\same.pbix", "C:\\folder2\\same.pbix", null], result.Instances.Select(instance => instance.FilePath));
        Assert.Equal(["verified", "verified", "unavailable"], result.Instances.Select(instance => instance.FileIdentityStatus));
        Assert.Equal("DESKTOP_BRIDGE_UNSUPPORTED", result.Instances[2].FileIdentityErrorCode);
        Assert.NotEqual(result.Instances[0].InstanceId, result.Instances[1].InstanceId);
    }

    /// <summary>空列表真实表示没有实例, 不伪造模型结果.</summary>
    [Fact]
    public async Task EmptyScan()
    {
        var reader = new Reader();
        var result = await new PowerBiService(reader, TimeProvider.System).ExecuteAsync();
        Assert.Empty(result.Instances);
        Assert.Equal(0, reader.ReadCount);
        Assert.True(DateTimeOffset.TryParse(result.Timestamp, out _));
    }

    /// <summary>某实例关闭和等待模型不影响其他实例, 不连接尚未发现的端口.</summary>
    [Fact]
    public async Task PartialFailureAndStartingState()
    {
        var reader = new Reader
        {
            Endpoints = [new(1, "启动中", null, null, null), new(2, "关闭", 20, 1, 2000), new(3, "报表", 30, 1, 3000), new(4, "无权限", null, null, null, "POWER_BI_ACCESS_DENIED")],
            Read = (endpoint, _) => endpoint.ProcessId == 2
                ? throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭", 3)
                : Task.FromResult<IReadOnlyList<PowerBiModel>>([new("模型", 1600, 3, 7)])
        };
        var result = await new PowerBiService(reader, TimeProvider.System).ExecuteAsync();
        Assert.Equal(["starting", "unavailable", "connected", "unavailable"], result.Instances.Select(i => i.Status));
        Assert.Equal("POWER_BI_INSTANCE_CLOSED", result.Instances[1].ErrorCode);
        Assert.Equal(7, Assert.Single(result.Instances[2].Models).MeasureCount);
        Assert.Equal(2, reader.ReadCount);
        Assert.Null(result.Instances[0].InstanceId);
        Assert.Null(result.Instances[1].InstanceId);
        Assert.Equal(reader.Endpoints[2].InstanceId, result.Instances[2].InstanceId);
        Assert.Null(result.Instances[3].InstanceId);
    }

    /// <summary>展示身份忽略报表标题, 任一进程、引擎启动时间或端口变化均隔离旧连接.</summary>
    [Fact]
    public void InstanceIdentityTracksEngineInsteadOfTitle()
    {
        var endpoint = new PowerBiEndpoint(1, "相同报表", 10, 100, 1234);
        Assert.Matches("^[a-f0-9]{64}$", endpoint.InstanceId!);
        Assert.Equal(endpoint.InstanceId, (endpoint with { Title = "修改后的标题" }).InstanceId);
        Assert.NotEqual(endpoint.InstanceId, (endpoint with { ProcessId = 2 }).InstanceId);
        Assert.NotEqual(endpoint.InstanceId, (endpoint with { EngineProcessId = 20 }).InstanceId);
        Assert.NotEqual(endpoint.InstanceId, (endpoint with { EngineStarted = 200 }).InstanceId);
        Assert.NotEqual(endpoint.InstanceId, (endpoint with { Port = 2345 }).InstanceId);
    }

    /// <summary>无有效完整引擎身份时不发布可误判为当前连接的展示标识.</summary>
    /// <param name="processId">本机窗口进程标识.</param>
    /// <param name="engineProcessId">模型引擎进程标识, 可为空.</param>
    /// <param name="engineStarted">模型引擎 UTC 启动 ticks, 可为空.</param>
    /// <param name="port">模型引擎本机端口, 可为空.</param>
    [Theory]
    [InlineData(0, 10, 1L, 1234)]
    [InlineData(1, null, 1L, 1234)]
    [InlineData(1, 0, 1L, 1234)]
    [InlineData(1, 10, null, 1234)]
    [InlineData(1, 10, 0L, 1234)]
    [InlineData(1, 10, 1L, null)]
    [InlineData(1, 10, 1L, 0)]
    [InlineData(1, 10, 1L, 65536)]
    public void MissingOrInvalidEngineIdentityHasNoDisplayId(int processId, int? engineProcessId, long? engineStarted, int? port)
        => Assert.Null(new PowerBiEndpoint(processId, "报表", engineProcessId, engineStarted, port).InstanceId);

    /// <summary>同名实例仍返回独立身份, 扫描与 Desktop 连接字段遵循同一 camelCase 展示契约.</summary>
    [Fact]
    public async Task SameTitleInstancesRetainDistinctDisplayIdentities()
    {
        var endpoint = new PowerBiEndpoint(1, "同名报表", 10, 100, 1234);
        var reader = new Reader { Endpoints = [endpoint, endpoint with { ProcessId = 2, EngineProcessId = 20 }] };
        var result = await new PowerBiService(reader, TimeProvider.System).ExecuteAsync();
        Assert.NotEqual(result.Instances[0].InstanceId, result.Instances[1].InstanceId);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var scan = JsonSerializer.SerializeToElement(result, options);
        var desktop = JsonSerializer.SerializeToElement(new ModelConnectionInfo("desktop", "Power BI Desktop", InstanceId: endpoint.InstanceId), options);
        Assert.Equal(scan.GetProperty("instances")[0].GetProperty("instanceId").GetString(), desktop.GetProperty("instanceId").GetString());
        Assert.Null(new ModelConnectionInfo("ssas-xmla", "SSAS / XMLA").InstanceId);
        Assert.Null((endpoint with { ErrorCode = "POWER_BI_ACCESS_DENIED" }).InstanceId);
    }

    /// <summary>发现失败向入口传播, 不返回空成功.</summary>
    [Fact]
    public async Task DiscoveryFailure()
    {
        var reader = new Reader { DiscoveryError = new AppException("POWER_BI_DISCOVERY_FAILED", "发现失败", 3) };
        var error = await Assert.ThrowsAsync<AppException>(() => new PowerBiService(reader, TimeProvider.System).ExecuteAsync());
        Assert.Equal("POWER_BI_DISCOVERY_FAILED", error.Code);
    }

    /// <summary>门闩证明并发刷新被拒绝, 取消释放门闩后可再次成功刷新.</summary>
    [Fact]
    public async Task CancellationReleasesBusyGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader
        {
            Endpoints = [new(1, "报表", 10, 1, 1234)],
            Read = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; }
        };
        var service = new PowerBiService(reader, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ExecuteAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("BUSY", (await Assert.ThrowsAsync<AppException>(() => service.ExecuteAsync())).Code);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        reader.Read = (_, _) => Task.FromResult<IReadOnlyList<PowerBiModel>>([new("模型", 1600, 3, 7)]);
        Assert.Equal("connected", Assert.Single((await service.ExecuteAsync()).Instances).Status);
    }

    /// <summary>新增方法必须经来源检查且不接收网页地址、连接字符串或多余参数.</summary>
    [Fact]
    public async Task BridgeOnlyAcceptsEmptyTrustedRequests()
    {
        ConcurrentQueue<JsonElement> output = new();
        var reader = new Reader();
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(value)), "test", powerBi: new(reader, TimeProvider.System));
        var request = JsonSerializer.Serialize(new { protocolVersion = 1, id = "scan", method = "powerbi.instances", @params = new { } });
        await bridge.ReceiveAsync("https://evil.test", request);
        Assert.Empty(output);
        await bridge.ReceiveAsync(OriginPolicy.Production, request.Replace("\"params\":{}", "\"params\":{\"server\":\"evil.test\"}"));
        Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
        await bridge.ReceiveAsync(OriginPolicy.Production, request.Replace("\"params\":{}", "\"params\":{\"instanceId\":\"forged\"}"));
        Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, reader.DiscoveryCount);
        await bridge.ReceiveAsync(OriginPolicy.Production, request);
        Assert.Empty(output.Last().GetProperty("result").GetProperty("instances").EnumerateArray());
        Assert.Equal(1, reader.DiscoveryCount);
        Assert.Equal(0, bridge.PendingCount);
    }

    /// <summary>Power BI 请求超时向适配器传播取消, 最终错误唯一且清理在途记录.</summary>
    [Fact]
    public async Task BridgeTimeoutCleansPowerBiRequest()
    {
        var reader = new Reader
        {
            Endpoints = [new(1, "报表", 10, 1, 1234)],
            Read = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return []; }
        };
        ConcurrentQueue<JsonElement> output = new();
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            value => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(value)), "test", TimeSpan.FromMilliseconds(50), new(reader, TimeProvider.System));
        await bridge.ReceiveAsync(OriginPolicy.Production, "{\"protocolVersion\":1,\"id\":\"scan\",\"method\":\"powerbi.instances\",\"params\":{}}").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("TIMEOUT", Assert.Single(output).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, bridge.PendingCount);
    }

    /// <summary>可注入门闩与故障的只读适配器.</summary>
    private sealed class Reader : IPowerBiReader
    {
        public IReadOnlyList<PowerBiEndpoint> Endpoints { get; set; } = [];
        public Exception? DiscoveryError { get; init; }
        public Func<PowerBiEndpoint, PowerBiFileIdentity> ReadFile { get; init; } = _ => new(null, "unavailable");
        /// <summary>返回与每个发现身份对应的可控权威文件状态, 不读取真实用户文件.</summary>
        public Task<PowerBiFileIdentity> ReadFileIdentityAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadFile(endpoint));
        }
        public int ReadCount { get; private set; }
        public int DiscoveryCount { get; private set; }
        public Func<PowerBiEndpoint, CancellationToken, Task<IReadOnlyList<PowerBiModel>>> Read { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<PowerBiModel>>([]);
        /// <summary>返回已配置候选或传播发现错误.</summary>
        public Task<IReadOnlyList<PowerBiEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoveryCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (DiscoveryError is not null) throw DiscoveryError;
            return Task.FromResult(Endpoints);
        }
        /// <summary>记录实际读取并传播令牌.</summary>
        public Task<IReadOnlyList<PowerBiModel>> ReadAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Read(endpoint, cancellationToken);
        }
    }
}
