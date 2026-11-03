// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UiSettingsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证界面与 DAX 格式偏好持久化、旧格式重建、损坏保护及严格桥接取消边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Settings;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>界面偏好独立存储, 不改变最近工程记录或授予模型写入权限.</summary>
public sealed class UiSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-ui-settings-" + Guid.NewGuid().ToString("N"));

    /// <summary>两个实例读取同一旧快照后依次修改不同字段, 后保存的补丁必须保留其他实例的最新值.</summary>
    [Fact]
    public void PatchesPreserveOtherInstanceChanges()
    {
        var first = new UiSettingsService(new LocalUiSettingsStore(root));
        var second = new UiSettingsService(new LocalUiSettingsStore(root));
        Assert.Equal(new UiSettings(), first.Read());
        Assert.Equal(new UiSettings(), second.Read());
        Assert.Equal(new UiSettings(DaxFormatStyle: "short"), second.Save(new(DaxFormatStyle: "short"), default));
        Assert.Equal(new UiSettings(ShowRecentFiles: false, DaxFormatStyle: "short"), first.Save(new(ShowRecentFiles: false), default));
        Assert.Equal(new UiSettings("en-us", false, "short"), second.Save(new(Locale: "en-us"), default));
        Assert.Equal(new UiSettings("en-us", false, "short"), first.Read());
        Assert.Equal(new UiSettings("en-us", false, "short"), new LocalUiSettingsStore(root).Read());
    }

    /// <summary>新存储默认中文和显示最近文件, 重开恢复偏好, 非法输入、取消和锁冲突保留已有设置与历史.</summary>
    [Fact]
    public void PreferencesPersistWithoutChangingOtherSettings()
    {
        var service = new UiSettingsService(new LocalUiSettingsStore(root));
        Assert.Equal(new UiSettings(), service.Read());
        var history = new LocalRecentProjects(root);
        history.Record(Path.Combine(root, "existing.pbix"));
        var historyBytes = File.ReadAllBytes(Path.Combine(root, "recent-projects.json"));
        Assert.Equal(new UiSettings("en-us", false, "short"), service.Save(new("en-us", false, "short"), default));
        Assert.Equal("en-us", new LocalUiSettingsStore(root).Read().Locale);
        Assert.False(new LocalUiSettingsStore(root).Read().ShowRecentFiles);
        Assert.Equal("short", new LocalUiSettingsStore(root).Read().DaxFormatStyle);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => service.Save(new(), default)).Code);
        Assert.Equal("INVALID_UI_LOCALE", Assert.Throws<AppException>(() => service.Save(new("EN-US", true, "long"), default)).Code);
        Assert.Equal("INVALID_DAX_FORMAT_STYLE", Assert.Throws<AppException>(() => service.Save(new("zh-cn", true, "SHORT"), default)).Code);
        Assert.ThrowsAny<OperationCanceledException>(() => service.Save(new("zh-cn", true, "long"), new CancellationToken(true)));
        using (UserDataStore.Lock(Path.Combine(root, "ui-settings.lock")))
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => service.Save(new("zh-cn", true, "long"), default)).Code);
        Assert.Equal(new UiSettings("en-us", false, "short"), service.Read());
        Assert.Equal(historyBytes, File.ReadAllBytes(Path.Combine(root, "recent-projects.json")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>损坏 JSON、重复字段和当前版本无效字段都保留原文件, 不覆盖为默认配置.</summary>
    /// <param name="contents">无法安全识别的设置内容.</param>
    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":"3"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":true}""")]
    [InlineData("""{"schemaVersion":3,"locale":"fr-fr","showRecentFiles":true,"daxFormatStyle":"long"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":"false","daxFormatStyle":"long"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":null,"daxFormatStyle":"long"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","ShowRecentFiles":false,"daxFormatStyle":"long"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":false,"daxFormatStyle":"LONG"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":false,"daxFormatStyle":null}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":false,"daxFormatStyle":"long","path":"untrusted"}""")]
    [InlineData("""{"schemaVersion":3,"schemaVersion":3,"locale":"en-us","showRecentFiles":false,"daxFormatStyle":"long"}""")]
    [InlineData("""{"schemaVersion":3,"locale":"en-us","showRecentFiles":false,"daxFormatStyle":"long","daxFormatStyle":"short"}""")]
    [InlineData("""{"schemaVersion":2,"locale":"en-us","locale":"zh-cn"}""")]
    [InlineData("""{"schemaVersion":2,"locale":"en-us"}""")]
    [InlineData("""{"schemaVersion":2,"locale":true,"showRecentFiles":false}""")]
    [InlineData("""{"schemaVersion":2,"locale":"fr-fr","showRecentFiles":false}""")]
    [InlineData("""{"schemaVersion":2,"locale":"en-us","showRecentFiles":"false"}""")]
    [InlineData("""{"schemaVersion":2,"locale":"en-us","showRecentFiles":false,"path":"untrusted"}""")]
    [InlineData("""{"schemaVersion":99,"futurePreference":true}""")]
    public void CorruptConfigurationIsPreserved(string contents)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ui-settings.json");
        File.WriteAllText(path, contents);
        var store = new LocalUiSettingsStore(root);
        Assert.Equal("zh-cn", store.Read().Locale);
        Assert.True(store.Read().ShowRecentFiles);
        Assert.Equal("long", store.Read().DaxFormatStyle);
        Assert.NotNull(store.Read().Warning);
        Assert.Equal("INVALID_UI_SETTINGS", Assert.Throws<AppException>(() => store.Save(new("en-us", false, "short"), default)).Code);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    /// <summary>不支持版本不读取历史偏好, 显式保存才原子重建, 取消及并发锁仍保留原文件.</summary>
    /// <param name="contents">语法有效但版本不受支持的配置.</param>
    [Theory]
    [InlineData("""{"schemaVersion":2,"locale":"en-us","showRecentFiles":false}""")]
    [InlineData("""{"schemaVersion":2,"locale":"zh-cn","showRecentFiles":true}""")]
    public void UnsupportedVersionResetsOnlyWhenPreferencesAreSaved(string contents)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ui-settings.json");
        File.WriteAllText(path, contents);
        var store = new LocalUiSettingsStore(root);
        var settings = store.Read();
        Assert.Equal("zh-cn", settings.Locale);
        Assert.True(settings.ShowRecentFiles);
        Assert.Equal("long", settings.DaxFormatStyle);
        Assert.Contains("版本不受支持", settings.Warning);
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Save(new("zh-cn", true, "short"), new CancellationToken(true)));
        using (UserDataStore.Lock(Path.Combine(root, "ui-settings.lock")))
            Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => store.Save(new("zh-cn", true, "short"), default)).Code);
        Assert.Equal(contents, File.ReadAllText(path));
        // 读取默认值后其他写入者造成损坏, 保存必须在锁内重新读取并拒绝覆盖.
        File.WriteAllText(path, "{changed-and-broken");
        Assert.Equal("INVALID_UI_SETTINGS", Assert.Throws<AppException>(() => store.Save(new("zh-cn", true, "short"), default)).Code);
        Assert.Equal("{changed-and-broken", File.ReadAllText(path));
        File.WriteAllText(path, contents);
        Assert.Equal(new UiSettings("zh-cn", true, "short"), store.Save(new("zh-cn", true, "short"), default));
        Assert.Equal(new UiSettings("zh-cn", true, "short"), new LocalUiSettingsStore(root).Read());
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>实际文件保存进入提交后忽略取消, 提交回调失败则不写入原文件.</summary>
    [Fact]
    public void FileSaveRetainsActualResultAfterCommitCancellation()
    {
        var store = new LocalUiSettingsStore(root);
        store.Save(new("zh-cn", true, "long"), default);
        var path = Path.Combine(root, "ui-settings.json");
        var baseline = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => store.Save(new("en-us", false, "long"), default, () => throw new InvalidOperationException("before-write")));
        Assert.Equal(baseline, File.ReadAllBytes(path));
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(new UiSettings("en-us", false), store.Save(new("en-us", false, "long"), cancellation.Token, cancellation.Cancel));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(new UiSettings("en-us", false), new LocalUiSettingsStore(root).Read());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>替换被其他读取者阻止时保留原文件与偏好, 不泄漏未完成的临时文件.</summary>
    [Fact]
    public void FailedAtomicReplacementPreservesPreviousPreferences()
    {
        var store = new LocalUiSettingsStore(root);
        store.Save(new("en-us", false, "long"), default);
        var path = Path.Combine(root, "ui-settings.json");
        var bytes = File.ReadAllBytes(path);
        using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.Save(new("zh-cn", true, "long"), default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new UiSettings("en-us", false), store.Read());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>网页只传白名单语言, 所有未知、重复、类型和来源注入都不会保存.</summary>
    [Fact]
    public async Task BridgeUsesExactShapeAndTrustedSource()
    {
        ConcurrentQueue<JsonElement> output = new();
        var store = new LocalUiSettingsStore(root);
        using var session = Create(store, output);
        await Call(session, "ui.settings", "{}");
        var result = output.Last().GetProperty("result");
        Assert.Equal("zh-cn", result.GetProperty("locale").GetString());
        Assert.True(result.GetProperty("showRecentFiles").GetBoolean());
        Assert.Equal("long", result.GetProperty("daxFormatStyle").GetString());
        Assert.False(result.GetProperty("isSimulation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("warning").ValueKind);
        await Call(session, "ui.saveSettings", """{"daxFormatStyle":"short"}""");
        Assert.Equal("short", output.Last().GetProperty("result").GetProperty("daxFormatStyle").GetString());
        await Call(session, "ui.saveSettings", """{"locale":"en-us","showRecentFiles":false}""");
        Assert.Equal(new UiSettings("en-us", false, "short"), store.Read());
        foreach (var input in new[]
        {
            "{}", """{"path":"untrusted"}""", """{"locale":true}""", """{"locale":null}""",
            """{"Locale":"zh-cn"}""", """{"showRecentFiles":"true"}""", """{"showRecentFiles":null}""",
            """{"daxFormatStyle":true}""", """{"daxFormatStyle":null}""",
            """{"locale":"zh-cn","locale":"en-us"}""", """{"showRecentFiles":true,"showRecentFiles":false}""",
            """{"daxFormatStyle":"long","daxFormatStyle":"short"}""",
        })
        {
            await Call(session, "ui.saveSettings", input);
            Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
        }
        await Call(session, "ui.saveSettings", "{\"locale\":\"fr-fr\",\"showRecentFiles\":true,\"daxFormatStyle\":\"long\"}");
        Assert.Equal("INVALID_UI_LOCALE", output.Last().GetProperty("error").GetProperty("code").GetString());
        await Call(session, "ui.saveSettings", """{"locale":"zh-cn","showRecentFiles":true,"daxFormatStyle":"LONG"}""");
        Assert.Equal("INVALID_DAX_FORMAT_STYLE", output.Last().GetProperty("error").GetProperty("code").GetString());
        await Call(session, "ui.settings", "{\"locale\":\"zh-cn\"}");
        Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
        var count = output.Count;
        await Call(session, "ui.saveSettings", "{\"locale\":\"zh-cn\",\"showRecentFiles\":true,\"daxFormatStyle\":\"long\"}", "https://evil.test");
        Assert.Equal(count, output.Count);
        Assert.Equal(new UiSettings("en-us", false, "short"), store.Read());
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>语言保存沿用提交门闩, 提交前取消无写入, 提交后取消不覆盖真实结果.</summary>
    /// <param name="committed">门闩等待前是否已经进入提交.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BridgeCancellationHonorsCommitBoundary(bool committed)
    {
        ConcurrentQueue<JsonElement> output = new();
        using var store = new GatedStore(committed);
        using var session = Create(store, output);
        var saving = Task.Run(() => Call(session, "ui.saveSettings", "{\"locale\":\"en-us\",\"showRecentFiles\":false,\"daxFormatStyle\":\"long\"}"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Call(session, "request.cancel", "{\"requestId\":\"ui.saveSettings\"}");
        Assert.Equal(!committed, output.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
        store.Release.Set();
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(committed ? "en-us" : "zh-cn", store.Read().Locale);
        Assert.Equal(!committed, store.Read().ShowRecentFiles);
        var reply = output.Last();
        if (committed)
        {
            Assert.Equal("en-us", reply.GetProperty("result").GetProperty("locale").GetString());
            Assert.False(reply.GetProperty("result").GetProperty("showRecentFiles").GetBoolean());
        }
        else Assert.Equal("CANCELLED", reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, session.PendingCount);
    }

    /// <summary>构造真实桥接分发器, 语言存储是唯一新增能力.</summary>
    /// <param name="store">真实文件存储或门闩测试适配器.</param>
    /// <param name="output">实际响应接收队列.</param>
    /// <returns>由调用方关闭的会话.</returns>
    private static BridgeSession Create(IUiSettingsStore store, ConcurrentQueue<JsonElement> output)
    {
        return new(new(false, null), new(new RuntimeEnvironment()),
            text => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", uiSettings: new(store));
    }

    /// <summary>通过真实 JSON 边界提交指定来源的请求, 使用固定方法名作为测试请求 ID.</summary>
    /// <param name="session">实际桥接会话.</param>
    /// <param name="method">白名单方法名.</param>
    /// <param name="parameters">保留重复字段的参数 JSON.</param>
    /// <param name="source">实际 WebView 来源.</param>
    /// <returns>请求终态完成任务.</returns>
    private static Task Call(BridgeSession session, string method, string parameters, string source = OriginPolicy.Production) =>
        session.ReceiveAsync(source, "{\"protocolVersion\":1,\"id\":\"" + method + "\",\"method\":\"" + method + "\",\"params\":" + parameters + "}");

    /// <summary>不依赖延时推测竞争顺序的同步存储门闩.</summary>
    private sealed class GatedStore(bool committed) : IUiSettingsStore, IDisposable
    {
        private UiSettings settings = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        /// <summary>返回门闩适配器当前实际保存值.</summary>
        public UiSettings Read() => settings;

        /// <summary>在提交前后等待测试放行, 用取消检查保证未提交不写入.</summary>
        /// <param name="patch">测试本次保存的偏好修改.</param>
        /// <param name="token">只在尚未提交时检查取消.</param>
        /// <param name="committing">进入提交阶段的测试回调.</param>
        /// <returns>门闩适配器实际保存后的界面偏好.</returns>
        public UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null)
        {
            if (committed) committing?.Invoke();
            Entered.SetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (!committed) { token.ThrowIfCancellationRequested(); committing?.Invoke(); }
            settings = new(patch.Locale ?? settings.Locale, patch.ShowRecentFiles ?? settings.ShowRecentFiles, patch.DaxFormatStyle ?? settings.DaxFormatStyle);
            return settings;
        }

        /// <summary>释放测试门闩句柄.</summary>
        public void Dispose() => Release.Dispose();
    }

    /// <summary>只清理已核验的本次临时目录, 不访问用户配置.</summary>
    public void Dispose()
    {
        UserDataStore.Check(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
