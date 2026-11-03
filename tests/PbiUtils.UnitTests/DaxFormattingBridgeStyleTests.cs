// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormattingBridgeStyleTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证本地 DAX 格式化桥接的短长样式传递与严格参数边界.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>通过实际 dispatcher 验证短长模式, 不依赖模型或远端格式化服务.</summary>
public sealed class DaxFormattingBridgeStyleTests
{
    /// <summary>同一表达式按显式样式得到不同布局, 模型正文仍保留定义头换行和语法内部缩进.</summary>
    [Theory]
    [InlineData("long", "\nDIVIDE ( 1, 2 )")]
    [InlineData("short", "\nDIVIDE (\n    1,\n    2\n)")]
    public async Task DispatchesExplicitFormattingStyle(string style, string expected)
    {
        using var response = await RequestAsync(JsonSerializer.Serialize(new { value = "[\"DIVIDE(1,2)\"]", style }));
        Assert.Equal(expected, Assert.Single(response.RootElement.GetProperty("result").GetProperty("expressions").EnumerateArray()).GetString());
    }

    /// <summary>样式是必填白名单字符串, 缺失、额外字段、错误类型或未知值均返回参数错误且不回传结果.</summary>
    [Theory]
    [InlineData("""{"value":"[\"1\"]"}""")]
    [InlineData("""{"value":"[\"1\"]","style":""}""")]
    [InlineData("""{"value":"[\"1\"]","style":"Long"}""")]
    [InlineData("""{"value":"[\"1\"]","style":"other"}""")]
    [InlineData("""{"value":"[\"1\"]","style":null}""")]
    [InlineData("""{"value":"[\"1\"]","style":1}""")]
    [InlineData("""{"value":"[\"1\"]","style":true}""")]
    [InlineData("""{"value":"[\"1\"]","style":"long","endpoint":"https://invalid.example"}""")]
    public async Task RejectsInvalidFormattingStyle(string parameters)
    {
        using var response = await RequestAsync(parameters);
        Assert.Equal("VALIDATION_ERROR", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(response.RootElement.TryGetProperty("result", out _));
    }

    /// <summary>创建一次隔离桥接并发送原始参数对象; 返回调用方负责释放的唯一响应, 不创建模型会话.</summary>
    private static async Task<JsonDocument> RequestAsync(string parameters)
    {
        ConcurrentQueue<string> messages = new();
        using var bridge = new BridgeSession(new OriginPolicy(false, null), new DoctorService(new RuntimeEnvironment()), messages.Enqueue, "test");
        using var parsed = JsonDocument.Parse(parameters);
        await bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id = "format-style",
            method = "dax.format",
            @params = parsed.RootElement
        }));
        return JsonDocument.Parse(Assert.Single(messages));
    }
}
