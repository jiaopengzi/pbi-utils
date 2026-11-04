// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UpdatesBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 核验更新桥接严格输入与显式宿主能力, 不实际打开浏览器或联网.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Updates;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class UpdatesBridgeTests
{
    /// <summary>业务失败仍返回可展示状态, 同时日志必须以失败结束, 不记为已完成.</summary>
    [Fact]
    public async Task NetworkFailureRemainsFailureInGuiBusinessLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-update-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new CaptureLogger();
            var service = new ProductUpdateService(new LocalProductUpdateStore(root), new Releases(true), "0.1.0");
            JsonElement response = default;
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
                message => response = JsonSerializer.Deserialize<JsonElement>(message), "test", logger: log, productUpdates: service);
            await session.ReceiveAsync(OriginPolicy.Production,
                """{"protocolVersion":1,"id":"failure","method":"updates.check","params":{"source":"github","automatic":false}}""");
            Assert.Equal("failed", response.GetProperty("result").GetProperty("check").GetProperty("status").GetString());
            var end = Assert.Single(log.Events, fields => fields.TryGetValue("Outcome", out var outcome) && !Equals(outcome, "Started"));
            Assert.Equal("Failed", end["Outcome"]);
            Assert.Contains("UPDATE_NETWORK_FAILED", end.Values);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>未检查、失败及缓存损坏均可打开固定来源根页, 只由用户点击触发.</summary>
    [Fact]
    public async Task ReleasePageOpensWithoutSuccessfulCheckOrReadableSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-update-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ProductUpdateService(new LocalProductUpdateStore(root), new Releases(true), "0.1.0");
            JsonElement response = default;
            var opened = new List<Uri>();
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
                message => response = JsonSerializer.Deserialize<JsonElement>(message), "test", productUpdates: service,
                openUpdateLink: (uri, _) => { opened.Add(uri); return Task.CompletedTask; });
            await Send("updates.open", new { source = "github" });
            Assert.True(response.GetProperty("result").GetProperty("opened").GetBoolean());
            await Send("updates.check", new { source = "gitee", automatic = false });
            Assert.Equal("failed", response.GetProperty("result").GetProperty("check").GetProperty("status").GetString());
            await Send("updates.open", new { source = "gitee" });
            Assert.True(response.GetProperty("result").GetProperty("opened").GetBoolean());
            File.WriteAllText(Path.Combine(root, "product-updates.json"), "{broken");
            await Send("updates.open", new { source = "github" });
            Assert.True(response.GetProperty("result").GetProperty("opened").GetBoolean());
            Assert.Equal([ProductUpdatePolicy.ReleasesLink("github"), ProductUpdatePolicy.ReleasesLink("gitee"), ProductUpdatePolicy.ReleasesLink("github")], opened.Select(uri => uri.AbsoluteUri));

            // 所有用例经过真实消息分发, 测试只替换操作系统浏览器适配.
            Task Send(string method, object parameters) => session.ReceiveAsync(OriginPolicy.Production,
                JsonSerializer.Serialize(new { protocolVersion = 1, id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>缺少服务的宿主明确不可用, 不伪造已检查或已打开.</summary>
    [Theory]
    [InlineData("updates.status", "{}")]
    [InlineData("updates.save", "{\"source\":\"github\",\"automaticCheck\":true}")]
    [InlineData("updates.save", "{\"automaticCheck\":false}")]
    [InlineData("updates.save", "{\"source\":\"gitee\"}")]
    [InlineData("updates.check", "{\"source\":\"gitee\",\"automatic\":false}")]
    [InlineData("updates.open", "{\"source\":\"github\"}")]
    public async Task MissingCapabilityIsExplicit(string method, string parameters)
        => Assert.Equal("UNSUPPORTED", await ErrorAsync(method, parameters));

    /// <summary>仅允许固定来源、明确布尔值, 移除的下载目标仍拒绝, 任意地址不能进入系统打开.</summary>
    [Theory]
    [InlineData("updates.status", "{\"extra\":true}")]
    [InlineData("updates.save", "{\"source\":\"https://evil.test\",\"automaticCheck\":true}")]
    [InlineData("updates.save", "{\"source\":\"github\",\"automaticCheck\":\"true\"}")]
    [InlineData("updates.save", "{}")]
    [InlineData("updates.save", "{\"automaticCheck\":false,\"url\":\"https://evil.test\"}")]
    [InlineData("updates.check", "{\"source\":\"github\"}")]
    [InlineData("updates.check", "{\"source\":\"github\",\"automatic\":false,\"url\":\"https://evil.test\"}")]
    [InlineData("updates.open", "{\"source\":\"github\",\"version\":\"0.2.0\",\"target\":\"release\",\"url\":\"https://evil.test\"}")]
    [InlineData("updates.open", "{\"source\":\"github\",\"version\":\"0.2.0\",\"target\":\"https://evil.test\"}")]
    [InlineData("updates.open", "{\"source\":\"github\",\"version\":\"0.2.0\",\"target\":\"with-dotnet\"}")]
    public async Task UnsafeArgumentsAreRejected(string method, string parameters)
        => Assert.Equal("VALIDATION_ERROR", await ErrorAsync(method, parameters));

    /// <summary>通过真实来源校验与请求分发读取错误, 不绕过产品协议.</summary>
    private static async Task<string?> ErrorAsync(string method, string parameters)
    {
        JsonElement response = default;
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => response = JsonSerializer.Deserialize<JsonElement>(message), "test");
        await session.ReceiveAsync(OriginPolicy.Production,
            $$"""{"protocolVersion":1,"id":"updates","method":"{{method}}","params":{{parameters}}} """);
        return response.GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>仅替换 HTTP, 保留版本策略、缓存与桥接行为.</summary>
    private sealed class Releases(bool fail = false) : IProductReleaseSource
    {
        /// <summary>返回请求来源的公开发布, 不接触真实服务.</summary>
        public Task<ProductRelease[]> ReadAsync(string source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail) return Task.FromException<ProductRelease[]>(new AppException("UPDATE_NETWORK_FAILED", "固定传输错误", 3));
            return Task.FromResult<ProductRelease[]>([new("desktop-v0.2.0", false, false, "plain text")]);
        }
    }

    /// <summary>捕获实际结构化终态, 不依赖日志格式化文案.</summary>
    private sealed class CaptureLogger : ILogger<BridgeSession>
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        /// <summary>测试无需外部关联资源.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>收集全部实际事件.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保留结构化字段供唯一终态断言.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(item => item.Key, item => item.Value));
    }
}
