// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCapabilityTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证旧连接页能力探测桥接已移除, 可信来源也不能恢复该功能.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>已移除的连接页探测不能通过旧网页消息重新启用.</summary>
public sealed class UdfCapabilityTests
{
    /// <summary>旧方法在可信来源也返回未知方法, 非可信来源仍丢弃且不建立请求.</summary>
    [Fact]
    public async Task RemovedCapabilityRequestIsRejected()
    {
        ConcurrentQueue<JsonElement> output = new();
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test");
        const string request = "{\"protocolVersion\":1,\"id\":\"probe\",\"method\":\"powerbi.udfCapabilities\",\"params\":{}}";
        await bridge.ReceiveAsync("https://evil.test", request);
        Assert.Empty(output);
        await bridge.ReceiveAsync(OriginPolicy.Production, request);
        Assert.Equal("UNKNOWN_METHOD", output.Last().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, bridge.PendingCount);
    }
}
