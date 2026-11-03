// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UserDataTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证统一目录和最近工程的持久边界.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有文件测试使用独立临时目录, 不读取真实用户凭据.</summary>
public sealed class UserDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-user-data-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>目录基于用户主目录, 与当前工作目录和安装目录无关.</summary>
    [Fact]
    public void UsesHomeDirectory() => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils"), RuntimeEnvironment.UserDataDirectory());

    /// <summary>最近 PBIP/PBIX 文件按路径去重并限制容量, 多实例更新保留其他记录, 损坏不覆盖.</summary>
    [Fact]
    public void RecentProjectsPersistDeduplicateAndPreserveCorruption()
    {
        var store = new LocalRecentProjects(root);
        for (var i = 0; i < 22; i++) store.Record(Path.Combine(root, $"中文工程-{i}." + (i % 2 == 0 ? "pbip" : "pbix")));
        var path = Path.Combine(root, "中文工程-11.pbix");
        new LocalRecentProjects(root).Record(path);
        Assert.Equal(20, store.Load().Count); Assert.Equal(path, store.Load()[0].Path);
        new LocalRecentProjects(root).Record(path.ToUpperInvariant());
        Assert.Equal(20, store.Load().Count);
        Assert.Single(store.Load(), item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        new LocalRecentProjects(root).Record(path);
        Assert.Equal(path, store.Resolve(store.Load()[0].Id));
        Assert.Contains(store.Load(), item => item.Path.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.Load(), item => item.Path.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<AppException>(() => store.Resolve("../any-path"));
        var baseline = File.ReadAllBytes(Path.Combine(root, "recent-projects.json"));
        Assert.Equal("INVALID_PROJECT_PATH", Assert.Throws<AppException>(() => store.Record(Path.Combine(root, "other.pbit"))).Code);
        Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(root, "recent-projects.json")));
        var file = Path.Combine(root, "recent-projects.json"); File.WriteAllText(file, "broken");
        Assert.Throws<AppException>(() => store.Record(path)); Assert.Equal("broken", File.ReadAllText(file));
    }

    /// <summary>桥接只接收保存的工程身份, 失败打开不新增记录.</summary>
    [Fact]
    public async Task BridgeRecentOpenUsesOnlyStoredIdentity()
    {
        var history = new LocalRecentProjects(root); var path = Path.Combine(root, "sample.pbip");
        List<JsonElement> output = [];
        using var bridge = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Add(JsonSerializer.Deserialize<JsonElement>(message)), "test", projects: new(new Reader()),
            selectProject: _ => Task.FromResult<string?>(path), connections: new TestLocalFileConnection(new Reader()).Service(), recentProjects: history);
        Task Call(string id, string method, object parameters) => bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id,
            method,
            @params = parameters,
            connectionRevision = output.Where(message => message.TryGetProperty("connectionRevision", out _)).Select(message => message.GetProperty("connectionRevision").GetInt64()).DefaultIfEmpty().Max()
        }));
        await Call("open", "project.open", new { }); Assert.Single(history.Load());
        await Call("again", "project.openRecent", new { id = history.Load()[0].Id }); Assert.True(output.Last().TryGetProperty("result", out _));
        await Call("invalid", "project.openRecent", new { id = "unknown" }); Assert.True(output.Last().TryGetProperty("error", out _));
        Assert.Equal(0, bridge.PendingCount);
    }

    /// <summary>只提供最小工程结构, 测试不访问真实模型.</summary>
    private sealed class Reader : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(new ProjectSnapshot(path, "workspace", "baseline", "TMSL", "PBIR", "1.0", "4.0", new("model", 1606, "en-US", [], [], [], 0, 0), [], [], []));
    }

    /// <summary>只清理本测试创建的唯一临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
