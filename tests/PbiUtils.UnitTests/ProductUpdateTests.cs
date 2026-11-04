// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ProductUpdateTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证双源更新选择、有限 HTTP、自动节流和原子存储行为.

using System.Net;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Updates;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有网络和用户数据均隔离, 不接触真实远端发布或用户配置.</summary>
public sealed class ProductUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-updates-" + Guid.NewGuid().ToString("N"));

    /// <summary>实际发布流水线的预发布标签必须参与更新检查, 不能误报没有正式版本.</summary>
    [Fact]
    public void PreviewReleaseFromCurrentWorkflowIsVisible()
    {
        var check = ProductUpdatePolicy.Select("github", "0.1.0",
            [new("desktop-v0.2.0-v2.1", false, true, "preview")], DateTimeOffset.UtcNow);
        Assert.Equal("available", check.Status);
        Assert.Equal("0.2.0-v2.1", check.LatestVersion);
    }

    /// <summary>共享夹具验证两站字段差异、旧版隔离、最高版本与完整发布列表.</summary>
    [Fact]
    public async Task SharedReleaseFixturesSelectCorrectVersionAndList()
    {
        var repository = FindRepository();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests/contracts/updates-releases.json")));
        foreach (var scenario in fixture.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var releases = scenario.GetProperty("releases").GetRawText();
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(releases))));
            var source = scenario.GetProperty("source").GetString()!;
            var service = new ProductUpdateService(new LocalProductUpdateStore(root), new ProductReleaseHttpSource(http), scenario.GetProperty("currentVersion").GetString()!);
            var check = (await service.CheckAsync(source, false, default)).Check!;
            var expected = scenario.GetProperty("expected");
            Assert.Equal(expected.GetProperty("status").GetString(), check.Status);
            Assert.Equal(expected.GetProperty("latestVersion").GetString(), check.LatestVersion);
            Assert.Equal(expected.GetProperty("versions").EnumerateArray().Select(item => item.GetString()), check.Releases.Select(release => release.Version));
            if (scenario.GetProperty("name").GetString() == "github-both-modes")
                Assert.Equal(scenario.GetProperty("releases")[0].GetProperty("body").GetString(), Assert.Single(check.Releases).Notes);
        }
    }

    /// <summary>发布轮次按数字排序, 同版本正式版优先; 同基版本预发布不伪称已安装某个轮次.</summary>
    [Fact]
    public void StableAndPreviewReleasesAreOrderedAndRoundTrip()
    {
        var check = ProductUpdatePolicy.Select("gitee", "0.2.0",
            [new("desktop-v0.2.0-v2.2", false, true, "two"), new("desktop-v0.2.0-v2.10", false, true, "ten"),
             new("desktop-v0.2.0", false, false, "stable"), new("desktop-v0.3.0-v2.1", true, true, "draft")], DateTimeOffset.UtcNow);
        Assert.Equal("current", check.Status);
        Assert.Equal(["0.2.0", "0.2.0-v2.10", "0.2.0-v2.2"], check.Releases.Select(release => release.Version));
        var store = new LocalProductUpdateStore(root);
        store.SaveCheck(check, false, default);
        Assert.Equal(check.Releases, store.Read("gitee").LastCheck!.Releases);
    }

    /// <summary>完整列表的纯文本预算有界, 不截断代理对, 不把大响应写成下次无法读取的缓存.</summary>
    [Fact]
    public void LargeReleaseListHasBoundedNotesAndReadableStorage()
    {
        var releases = Enumerable.Range(1, 1000).Select(index => new ProductRelease($"desktop-v0.2.0-v2.{index}", false, true,
            new string('中', 4095) + "😀"));
        var check = ProductUpdatePolicy.Select("github", "0.1.0", releases, DateTimeOffset.UtcNow);
        Assert.Equal(1000, check.Releases.Length);
        Assert.True(check.Releases.Sum(release => release.Notes.Length) <= ProductUpdatePolicy.MaximumNotesLength);
        Assert.All(check.Releases, release => { Assert.True(release.NotesTruncated); Assert.False(release.Notes.Length > 0 && char.IsHighSurrogate(release.Notes[^1])); });
        var store = new LocalProductUpdateStore(root);
        store.SaveCheck(check, false, default);
        store.SaveCheck(check with { Source = "gitee" }, false, default);
        Assert.Equal(check.Releases, store.Read().LastCheck!.Releases);
    }

    /// <summary>非法缓存的标签/展示版本/预发布状态不得成为可用列表.</summary>
    [Theory]
    [InlineData("desktop-v0.2.0", "0.3.0", false)]
    [InlineData("desktop-v0.2.0-v2.1", "0.2.0-v2.1", false)]
    [InlineData("v1.0.0", "1.0.0", false)]
    public void InvalidCachedReleaseCannotBeSaved(string tag, string version, bool preview)
    {
        var store = new LocalProductUpdateStore(root);
        var check = new ProductUpdateCheck("github", "0.1.0", "available", DateTimeOffset.UtcNow, version,
            [new(tag, version, preview, "", false)]);
        Assert.Throws<JsonException>(() => store.SaveCheck(check, false, default));
        Assert.False(File.Exists(Path.Combine(root, "product-updates.json")));
    }

    /// <summary>发布页映射无需网络或缓存, 且拒绝自由来源.</summary>
    [Theory]
    [InlineData("github")]
    [InlineData("gitee")]
    public void ReleasePageIsFixedAndIndependentOfCheck(string source)
    {
        Assert.Equal($"https://{source}.com/jiaopengzi/pbi-utils/releases", ProductUpdatePolicy.ReleasesLink(source));
        Assert.Throws<AppException>(() => ProductUpdatePolicy.ReleasesLink("https://evil.test"));
    }

    /// <summary>非当前格式按快速迭代约定重建默认设置, 不迁移旧结果也不阻断重新检查.</summary>
    [Fact]
    public void ObsoleteDocumentIsRebuiltOnNextSave()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "product-updates.json");
        const string previous = """{"schemaVersion":1,"settings":{"source":"gitee","automaticCheck":false},"checks":{}}""";
        File.WriteAllText(path, previous);
        var store = new LocalProductUpdateStore(root);
        Assert.Equal(new ProductUpdateSettings(), store.Read().Settings);
        Assert.Equal(previous, File.ReadAllText(path));
        store.Save(new(Source: "gitee"), default);
        Assert.Equal("gitee", store.Read().Settings.Source);
        Assert.Contains("\"schemaVersion\":2", File.ReadAllText(path));
    }

    /// <summary>失败分类不伪装无发布, 保留限流与 HTTP 故障的区别.</summary>
    [Theory]
    [InlineData(403, "UPDATE_HTTP_FAILED")]
    [InlineData(429, "UPDATE_RATE_LIMITED")]
    [InlineData(404, "UPDATE_HTTP_FAILED")]
    [InlineData(500, "UPDATE_HTTP_FAILED")]
    public async Task HttpFailuresHaveStableErrors(int status, string error)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var check = (await Create(http).CheckAsync("github", false, default)).Check!;
        Assert.Equal("failed", check.Status);
        Assert.Equal(error, check.ErrorCode);
    }

    /// <summary>403仅在响应明确携带限流证据时按限流分类, 不把普通访问拒绝误报限流.</summary>
    [Fact]
    public async Task ForbiddenWithRateLimitEvidenceIsClassifiedPrecisely()
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        Assert.Equal("UPDATE_RATE_LIMITED", (await Create(http).CheckAsync("github", false, default)).Check!.ErrorCode);
    }

    /// <summary>自动按来源节流24小时, 手动绕过, 关闭后自动不请求, 设置和其他来源记录保留.</summary>
    [Fact]
    public async Task AutomaticChecksThrottleAndSettingsMergeAcrossInstances()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Json("[]")); }));
        var clock = new Clock();
        var first = Create(http, clock);
        var second = Create(http, clock);
        Assert.True((await first.CheckAsync("github", true, default)).CheckedNow);
        Assert.False((await second.CheckAsync("github", true, default)).CheckedNow);
        second.Save(new(true, "gitee"), default);
        Assert.True((await second.CheckAsync("gitee", true, default)).CheckedNow);
        Assert.True((await first.CheckAsync("github", false, default)).CheckedNow);
        second.Save(new(false, "gitee"), default);
        Assert.False((await first.CheckAsync("github", true, default)).CheckedNow);
        Assert.Equal("gitee", first.Read().Settings.Source);
        Assert.Equal("gitee", first.Read().LastCheck!.Source);
        second.Save(new(true, "github"), default);
        clock.Now += TimeSpan.FromHours(24);
        Assert.True((await first.CheckAsync("github", true, default)).CheckedNow);
        Assert.Equal(4, calls);
    }

    /// <summary>满页必须继续读取, 达10页无法确认完整性时返回明确失败.</summary>
    [Fact]
    public async Task PaginationDoesNotClaimLatestFromTruncatedList()
    {
        var calls = 0;
        var full = "[" + string.Join(',', Enumerable.Repeat("{\"tag_name\":\"v1.0.0\"}", 100)) + "]";
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Contains("per_page=100&page=" + calls, request.RequestUri!.Query);
            return Task.FromResult(Json(full));
        }));
        var check = (await Create(http).CheckAsync("gitee", false, default)).Check!;
        Assert.Equal(10, calls);
        Assert.Equal("UPDATE_PAGE_LIMIT", check.ErrorCode);
        Assert.Equal("failed", check.Status);
    }

    /// <summary>最后一页的新版本必须参与最高版本选择, 不能只读取第一页.</summary>
    [Fact]
    public async Task PaginationIncludesLastPageAndPreservesPlainText()
    {
        var calls = 0;
        var first = "[" + string.Join(',', Enumerable.Repeat("{\"tag_name\":\"v1.0.0\"}", 100)) + "]";
        const string last = """[{"tag_name":"desktop-v0.9.0","body":"<b>更新</b>\n[external](https://evil.test)","assets":[]}]""";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Interlocked.Increment(ref calls) == 1 ? first : last))));
        var check = (await Create(http).CheckAsync("github", false, default)).Check!;
        Assert.Equal(2, calls);
        Assert.Equal("0.9.0", check.LatestVersion);
        Assert.Equal("<b>更新</b>\n[external](https://evil.test)", Assert.Single(check.Releases).Notes);
    }

    /// <summary>API通过分页头声明尚有数据时, 不把短页误判为读取完成或跟随外部URL.</summary>
    [Fact]
    public async Task PaginationHeadersCannotHideLaterReleaseOrRedirectRequests()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var response = Json(Interlocked.Increment(ref calls) == 1 ? "[]" : """[{"tag_name":"desktop-v0.4.0"}]""");
            if (calls == 1) response.Headers.Add("Link", "<https://evil.test/anything>; rel=\"next\"");
            return Task.FromResult(response);
        }));
        var check = (await Create(http).CheckAsync("github", false, default)).Check!;
        Assert.Equal(2, calls);
        Assert.Equal("0.4.0", check.LatestVersion);
    }

    /// <summary>自动关闭/切源发生在网络期间时, 迟到结果不覆盖用户最新设置和缓存.</summary>
    [Theory]
    [InlineData(false, "github")]
    [InlineData(true, "gitee")]
    public async Task AutomaticLateResultIsDiscarded(bool enabled, string source)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { started.SetResult(); await finish.Task.WaitAsync(token); return Json("[]"); }));
        var service = Create(http);
        var task = service.CheckAsync("github", true, default);
        await started.Task;
        Create(http).Save(new(enabled, source), default);
        finish.SetResult();
        Assert.False((await task).CheckedNow);
        Assert.Equal(new(enabled, source), service.Read().Settings);
        Assert.Null(service.Read().LastCheck);
    }

    /// <summary>设置取消不创建/覆盖文件, 网络记录合并不覆盖另一实例的新设置.</summary>
    [Fact]
    public void CancelledSaveAndConcurrentSourceRecordsRemainIndependent()
    {
        var first = new LocalProductUpdateStore(root);
        var second = new LocalProductUpdateStore(root);
        Assert.ThrowsAny<OperationCanceledException>(() => first.Save(new(false, "gitee"), new CancellationToken(true)));
        Assert.False(File.Exists(Path.Combine(root, "product-updates.json")));
        first.SaveCheck(new("github", "0.1.0", "noRelease", DateTimeOffset.UtcNow, null, []), false, default);
        second.Save(new(false, "gitee"), default);
        first.SaveCheck(new("gitee", "0.1.0", "noRelease", DateTimeOffset.UtcNow, null, []), false, default);
        Assert.Equal(new(false, "gitee"), first.Read().Settings);
        Assert.Equal("github", first.Read("github").LastCheck!.Source);
        Assert.Equal("gitee", first.Read().LastCheck!.Source);
    }

    /// <summary>两个实例基于旧设置只提交各自改动字段, 锁内合并保留另一实例刚保存的偏好.</summary>
    [Fact]
    public void SettingPatchesPreserveOtherInstanceLatestFields()
    {
        var first = new LocalProductUpdateStore(root);
        var second = new LocalProductUpdateStore(root);
        Assert.Equal(new ProductUpdateSettings(), first.Read().Settings);
        Assert.Equal(new ProductUpdateSettings(), second.Read().Settings);
        first.Save(new ProductUpdateSettingsPatch(AutomaticCheck: false), default);
        second.Save(new ProductUpdateSettingsPatch(Source: "gitee"), default);
        Assert.Equal(new ProductUpdateSettings(false, "gitee"), first.Read().Settings);
        second.Save(new ProductUpdateSettingsPatch(AutomaticCheck: true), default);
        first.Save(new ProductUpdateSettingsPatch(Source: "github"), default);
        Assert.Equal(new ProductUpdateSettings(true, "github"), second.Read().Settings);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => first.Save(new ProductUpdateSettingsPatch(), default)).Code);
        Assert.Equal("INVALID_UPDATE_SOURCE", Assert.Throws<AppException>(() => second.Save(new ProductUpdateSettingsPatch(Source: "GITHUB"), default)).Code);
        Assert.Equal(new ProductUpdateSettings(true, "github"), first.Read().Settings);
    }

    /// <summary>取消不写检查记录; 同源跨实例并发明确拒绝, 设置保存仍可进行.</summary>
    [Fact]
    public async Task CancellationAndConcurrentChecksPreserveState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Json("[]"); }));
        using var cancellation = new CancellationTokenSource();
        var service = Create(http);
        var pending = service.CheckAsync("github", false, cancellation.Token);
        await started.Task;
        var other = Create(http);
        Assert.Equal("UPDATE_CHECK_BUSY", (await other.CheckAsync("github", false, default)).Check!.ErrorCode);
        other.Save(new(false, "gitee"), default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(service.Read().LastCheck);
        Assert.False(service.Read().Settings.AutomaticCheck);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>损坏文件、重复字段与未知schema不可被检查或保存覆盖.</summary>
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":9}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public void CorruptStorageRemainsProtected(string contents)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "product-updates.json");
        File.WriteAllText(path, contents);
        var store = new LocalProductUpdateStore(root);
        Assert.Throws<AppException>(() => store.Read());
        Assert.Throws<AppException>(() => store.Save(new(Source: "github"), default));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    /// <summary>HTTP内部超时与响应类型、容量错误有界失败.</summary>
    [Theory]
    [InlineData("timeout", "UPDATE_TIMEOUT")]
    [InlineData("malformed", "UPDATE_INVALID_RESPONSE")]
    [InlineData("oversize", "UPDATE_RESPONSE_TOO_LARGE")]
    public async Task InvalidResponsesDoNotBecomeNoRelease(string kind, string error)
    {
        using var http = new HttpClient(new Handler((_, _) => kind == "timeout"
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException())
            : Task.FromResult(Json(kind == "malformed" ? "{}" : new string(' ', 4 * 1024 * 1024 + 1)))));
        var check = (await Create(http).CheckAsync("github", false, default)).Check!;
        Assert.Equal("failed", check.Status);
        Assert.Equal(error, check.ErrorCode);
    }

    /// <summary>隔离服务使用可注入HTTP和时钟.</summary>
    private ProductUpdateService Create(HttpClient http, TimeProvider? clock = null) => new(new LocalProductUpdateStore(root), new ProductReleaseHttpSource(http), "0.1.0", clock);

    /// <summary>创建UTF8 API响应.</summary>
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>定位共享夹具, 测试不需要修改产品项目文件.</summary>
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tests/contracts/updates-releases.json"))) return directory.FullName;
        throw new InvalidOperationException("Repository fixture not found");
    }

    /// <summary>释放本次测试创建的隔离目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    /// <summary>将固定响应注入HTTP适配器, 令牌传播到测试回调.</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    /// <summary>测试时钟只控制节流, 不改变系统时间.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
