// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCleanupBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实桥接响应验证临时目录清理失败在错误和取消终态中仍可见且不泄露路径.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Catalog;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>注入下载终态, 验证真实会话错误映射与取消竞争, 不触碰用户目录.</summary>
public sealed class UdfCleanupBridgeTests
{
    /// <summary>次要清理故障必须在最终响应中可见, 原始错误码和已接受取消仍优先, 原始异常正文不外泄.</summary>
    [Theory]
    [InlineData("app", false, true)]
    [InlineData("cancel", false, true)]
    [InlineData("generic", false, true)]
    [InlineData("app", true, true)]
    [InlineData("cancel", true, true)]
    [InlineData("generic", true, true)]
    [InlineData("timeout", false, true)]
    [InlineData("app", false, false)]
    [InlineData("cancel", false, false)]
    [InlineData("generic", false, false)]
    [InlineData("generic", true, false)]
    public async Task RefreshFailureKeepsCleanupNoticeAtActualBoundary(string kind, bool cancelFlight, bool cleanupFailed)
    {
        Exception failure = kind switch
        {
            "app" => new AppException("UDF_TEST_FIRST_FAILURE", "来源获取失败", 3),
            "cancel" => new OperationCanceledException("private-path-secret"),
            "timeout" => new AppException("UDF_GIT_TIMEOUT", "来源获取失败", 3),
            _ => new IOException("private-path-secret")
        };
        if (cleanupFailed)
        {
            if (kind == "timeout")
            {
                var cancelled = new OperationCanceledException("private-path-secret");
                UdfCleanupNotice.Mark(cancelled);
                UdfCleanupNotice.Transfer(cancelled, failure);
            }
            else PbiUtils.Infrastructure.Udf.UdfCleanupNotice.Mark(failure);
        }
        var provider = new FailingCatalog(failure);
        var scaffold = new ScaffoldBridge(new EmptyTemplates(), new(new UnusedWorkspace()), "unused-test-root", _ => Task.FromResult<string?>(null), packages: provider);
        ConcurrentQueue<JsonElement> messages = new();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            text => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", scaffold: scaffold);
        var refresh = Call(session, "refresh", "udf.catalogRefresh", new { });
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelFlight)
            {
                await Call(session, "cancel", "request.cancel", new { requestId = "refresh" });
                Assert.True(messages.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
            }
            provider.Release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            var response = Assert.Single(messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "refresh");
            var error = response.GetProperty("error");
            Assert.Equal(cancelFlight || kind == "cancel" ? "CANCELLED" : kind == "app" ? "UDF_TEST_FIRST_FAILURE" : kind == "timeout" ? "UDF_GIT_TIMEOUT" : "EXECUTION_FAILED", error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            Assert.Equal(cleanupFailed, message.Contains("本轮临时目录清理失败, 可能仍有临时文件.", StringComparison.Ordinal));
            Assert.DoesNotContain("private-path-secret", message, StringComparison.Ordinal);
            Assert.StartsWith(cancelFlight || kind == "cancel" ? "操作已取消" : kind is "app" or "timeout" ? "来源获取失败" : "请求执行失败", message, StringComparison.Ordinal);
            Assert.Equal(0, session.PendingCount);
        }
        finally { provider.Release.TrySetResult(); await refresh; await session.ShutdownAsync(); }
    }

    /// <summary>真实候选文件被锁定导致取消后的清理失败, 经会话错误映射后仍显示固定清理提示.</summary>
    [Fact]
    public async Task LockedCandidateCancellationPreservesCleanupNoticeAtBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-cleanup-boundary-" + Guid.NewGuid().ToString("N"));
        var store = new UdfCatalogSnapshotStore(root, new("test", "test", "https://github.com/example/functions", null, "custom", "git"));
        try
        {
            var candidate = store.Stage([DaxLibPackageReader.Read(UdfPackageTests.Package(), "Example.Test", "1.0.0")],
                new("https://github.com/example/functions", null, new string('a', 40)), CancellationToken.None);
            var manifest = Assert.Single(Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories));
            OperationCanceledException failure;
            var token = new CancellationToken(true);
            using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
                failure = Assert.ThrowsAny<OperationCanceledException>(() => store.CleanupCandidate(candidate, new OperationCanceledException("private-path-secret", token)));
            Assert.Equal(token, failure.CancellationToken);
            var provider = new FailingCatalog(failure);
            provider.Release.TrySetResult();
            ConcurrentQueue<JsonElement> messages = new();
            var scaffold = new ScaffoldBridge(new EmptyTemplates(), new(new UnusedWorkspace()), root, _ => Task.FromResult<string?>(null), packages: provider);
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
                text => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", scaffold: scaffold);
            await Call(session, "refresh", "udf.catalogRefresh", new { });
            var error = Assert.Single(messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "refresh").GetProperty("error");
            Assert.Equal("CANCELLED", error.GetProperty("code").GetString());
            Assert.Contains("本轮临时目录清理失败, 可能仍有临时文件.", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path-secret", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            await session.ShutdownAsync();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>发送实际白名单协议文档, 不调用内部错误格式化函数冒充协议验证.</summary>
    private static Task Call(BridgeSession session, string id, string method, object parameters) =>
        session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));

    /// <summary>门闩确保取消请求在下载抛错前被正式受理.</summary>
    private sealed class FailingCatalog(Exception failure) : IUdfCatalogProvider
    {
        public Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken) => throw new NotSupportedException();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UdfCatalogStatus> RefreshAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken, Action? committing = null)
        { Entered.TrySetResult(); await Release.Task; throw failure; }
        public Task<UdfCatalogStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>刷新不调用模板目录或工程写入能力, 意外调用明确失败.</summary>
    private sealed class EmptyTemplates : IPbipTemplateCatalog
    {
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>真实 ScaffoldBridge 的组合依赖, 本组不允许项目操作或磁盘写入.</summary>
    private sealed class UnusedWorkspace : IScaffoldWorkspace
    {
        public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null) => throw new NotSupportedException();
        public Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
