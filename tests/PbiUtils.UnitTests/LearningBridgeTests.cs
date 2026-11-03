// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LearningBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证学习专用宿主能力拒绝任意地址与不可用宿主, 不放宽真实来源.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Text.Json;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class LearningBridgeTests
{
    /// <summary>精确来源策略不信前缀、凭据、端口、HTTP 或应用自身的虚拟来源.</summary>
    [Theory]
    [InlineData("https://jiaopengzi.com/", true)]
    [InlineData("https://jiaopengzi.com/p/article", true)]
    [InlineData("https://jiaopengzi.com:443/p/article", true)]
    [InlineData("https://jiaopengzi.com.evil.test/", false)]
    [InlineData("https://user@jiaopengzi.com/", false)]
    [InlineData("https://jiaopengzi.com:444/", false)]
    [InlineData("http://jiaopengzi.com/", false)]
    [InlineData("https://app.pbi-utils.local/", false)]
    [InlineData("file:///C:/test.html", false)]
    public void LearningNavigationHasNoAppPrivileges(string address, bool expected) =>
        Assert.Equal(expected, LearningViewRequest.AllowsNavigation(address));

    /// <summary>没有原生学习适配器的宿主明确报告不可用, 不返回虚假打开结果.</summary>
    [Fact]
    public async Task LearningOpenRequiresExplicitHostCapability()
    {
        JsonElement response = default;
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => response = JsonSerializer.Deserialize<JsonElement>(message), "test");
        await session.ReceiveAsync(OriginPolicy.Production, """{"protocolVersion":1,"id":"learning","method":"learning.open","params":{}}""");
        Assert.Equal("UNSUPPORTED", response.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>浏览器打开不接受 URL, 布局拒绝越界和额外字段, 外站真实来源不能调用任何能力.</summary>
    [Fact]
    public async Task RestrictedLearningHandlersRejectUntrustedInputs()
    {
        var messages = new List<JsonElement>();
        var opens = 0;
        var closes = 0;
        var updates = new List<LearningViewRequest>();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Add(JsonSerializer.Deserialize<JsonElement>(message)), "test",
            openLearningSite: _ => { opens++; return Task.CompletedTask; }, closeLearningView: _ => { closes++; return Task.CompletedTask; },
            updateLearningView: (bounds, token) => { token.ThrowIfCancellationRequested(); updates.Add(bounds); return Task.CompletedTask; });
        await Send("learning.open", new { }, "https://jiaopengzi.com/");
        Assert.Empty(messages);
        Assert.Equal(0, opens);
        await Send("learning.open", new { url = "https://evil.test" });
        Assert.Equal("VALIDATION_ERROR", messages[^1].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, opens);
        await Send("learning.open", new { });
        Assert.Equal(1, opens);
        Assert.True(messages[^1].GetProperty("result").GetProperty("opened").GetBoolean());
        await Send("learning.update", new { visible = true, left = 10, top = 20, width = 900, height = 400, viewportWidth = 800, viewportHeight = 600 });
        Assert.Equal("VALIDATION_ERROR", messages[^1].GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(updates);
        await Send("learning.update", new { visible = true, left = 10, top = 20, width = 700, height = 400, viewportWidth = 800, viewportHeight = 600 });
        Assert.Single(updates);
        await Send("learning.close", new { });
        Assert.Equal(1, closes);

        // 请求辅助只构造当前用例的协议, 原始来源由调用参数独立提供.
        Task Send(string method, object parameters, string source = OriginPolicy.Production) => session.ReceiveAsync(source,
            JsonSerializer.Serialize(new { protocolVersion = 1, id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
    }
}
