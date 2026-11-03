// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证脚手架宿主目录授权, 草稿身份, 版本和旧文档失效保护.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Protocol;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>直接执行实际宿主分发器, 文件事务由工作区真实测试分别证明.</summary>
public sealed class ScaffoldBridgeTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly Adapter adapter = new();
    private readonly List<string> openedDirectories = [];
    private readonly List<string> openedProjects = [];
    private readonly List<string> directoryCreationIdentities = [];
    private readonly List<string> desktopCreationIdentities = [];
    private ScaffoldBridge? bridge;
    private static readonly string[] stringArray = ["Test.Function"];
    private static readonly string[] PreferenceInputFields = ["Year", "Dates"];
    private static readonly string[] PreferenceOutputFields = ["Dates", "Year"];
    private static readonly string[] DateOnlyFields = ["Dates"];

    /// <summary>只初始化测试独占父目录及可控系统选择回调.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root),
            openDirectory: (path, identity, _) => { openedDirectories.Add(path); directoryCreationIdentities.Add(identity); return Task.CompletedTask; },
            openDesktop: (path, identity, _) => { openedProjects.Add(path); desktopCreationIdentities.Add(identity); return Task.FromResult(ConnectedSnapshot()); });
        return Task.CompletedTask;
    }

    /// <summary>完整度量配置跨宿主恢复, 关闭可选段保留偏好且空集记忆删除.</summary>
    [Fact]
    public async Task CompleteMeasureConfigurationRestoresInNextHostAndNullDoesNotClearIt()
    {
        _ = await Call("scaffold.templates", new { });
        _ = await Call("scaffold.chooseOutput", new { });
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = new { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "Remember", locale = "zh-cn", gitInitialize = false } });
        identity = await Update(identity, "measures", new[] { new TemplateMeasureDefinition("custom", "remember", "Uncategorized", true, "description", "folder") });
        _ = await Call("scaffold.preview", identity);
        var preferences = new LocalTemplateMeasurePreferencesStore(root).Read();
        Assert.Equal("custom", Assert.Single(preferences.Measures).Name);
        identity = await Update(identity, "measures", (object)null!);
        _ = await Call("scaffold.preview", identity);
        Assert.Equal("custom", Assert.Single(new LocalTemplateMeasurePreferencesStore(root).Read().Measures).Name);
        identity = await Update(identity, "measures", Array.Empty<TemplateMeasureDefinition>());
        _ = await Call("scaffold.preview", identity);
        var next = new ScaffoldBridge(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root));
        try
        {
            var catalog = JsonSerializer.SerializeToElement(await next.HandleAsync("scaffold.templates", JsonSerializer.SerializeToElement(new { }), null, default), BridgeBusinessInput.Json);
            Assert.Empty(catalog.GetProperty("defaultMeasures").EnumerateArray());
        }
        finally { await next.ShutdownAsync(); }
    }

    /// <summary>网页不能通过选择目录参数注入任意路径.</summary>
    [Fact]
    public async Task RejectsWebPathsBeforeCallingSelector()
    {
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => Call("scaffold.chooseOutput", new { path = "unexpected" }))).Code);
        Assert.Equal(0, adapter.PreviewCalls);
    }

    /// <summary>冻结计划后产生新修订不能借用原摘要创建另一个工程, 不依赖展示确认.</summary>
    [Fact]
    public async Task FrozenPlanCannotCreateADifferentDraftAfterRevisionChange()
    {
        await Call("scaffold.chooseOutput", new { });
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = new { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "Reviewed", locale = "zh-cn", gitInitialize = false } });
        var plan = await Call("scaffold.preview", identity);
        var request = JsonSerializer.SerializeToElement(new { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32(), digest = plan.GetProperty("digest").GetString() });
        await Update(identity, "base", new { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "Changed", locale = "zh-cn", gitInitialize = false });
        await Assert.ThrowsAsync<AppException>(() => Call("scaffold.create", request));
        Assert.Equal(0, adapter.CreateCalls);
    }

    /// <summary>打开动作必须来自当前文档实际核验的创建回执, 不以目录选择授予权限.</summary>
    [Theory]
    [InlineData("scaffold.openDirectory")]
    [InlineData("scaffold.openDesktop")]
    public async Task OpenRequiresVerifiedCreatedReceipt(string method)
    {
        await Call("scaffold.chooseOutput", new { });
        Assert.Equal("CREATED_PROJECT_NOT_AVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Call(method, new { }))).Code);
    }

    /// <summary>打开动作只接受空参数, 网页路径、授权和目标字段不能扩大范围.</summary>
    [Theory]
    [InlineData("scaffold.openDirectory")]
    [InlineData("scaffold.openDesktop")]
    public async Task OpenRejectsWebTargetFields(string method)
    {
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => Call(method, new { path = root, approved = true }))).Code);
    }

    /// <summary>已核验回执只开放真实创建入口和所在目录, 打开不重放创建或改变草稿.</summary>
    [Fact]
    public async Task OpenUsesOnlyActualVerifiedReceipt()
    {
        var entry = await CreateProjectAsync();
        var creationIdentity = ProjectFileIdentity.Read(entry);
        Assert.True((await Call("scaffold.openDirectory", new { })).GetProperty("opened").GetBoolean());
        Assert.True((await Call("scaffold.openDesktop", new { })).GetProperty("opened").GetBoolean());
        Assert.Equal([entry], openedDirectories);
        Assert.Equal([entry], openedProjects);
        Assert.Equal([creationIdentity], directoryCreationIdentities);
        Assert.Equal([creationIdentity], desktopCreationIdentities);
        Assert.Equal(1, adapter.CreateCalls);
    }

    /// <summary>实际新建打开结果必须同时发布当前模型, 后续刷新沿用该连接而非重新选择文件.</summary>
    [Fact]
    public async Task DesktopOpenPublishesConnectedProjectToSession()
    {
        var entry = await CreateProjectAsync();
        var snapshot = ConnectedSnapshot();
        var output = new List<JsonElement>();
        var recent = new LocalRecentProjects(Path.Combine(root, "recent"));
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Add(JsonSerializer.Deserialize<JsonElement>(message)), "test", scaffold: bridge,
            projects: new(new OpeningReader(snapshot)), recentProjects: recent);
        try
        {
            await session.ReceiveAsync(OriginPolicy.Production, Request("desktop", "scaffold.openDesktop"));
            var result = output.Last().GetProperty("result");
            Assert.True(result.GetProperty("opened").GetBoolean());
            Assert.Equal(snapshot.ProjectPath, result.GetProperty("project").GetProperty("projectPath").GetString());
            Assert.False(result.TryGetProperty("entryPath", out _));
            Assert.Equal(entry, Assert.Single(recent.Load()).Path);
            await session.ReceiveAsync(OriginPolicy.Production, Request("inspect", "project.inspect", output.Last().GetProperty("connectionRevision").GetInt64()));
            Assert.Equal(snapshot.ProjectPath, output.Last().GetProperty("result").GetProperty("projectPath").GetString());
        }
        finally { await session.ShutdownAsync(); }
    }

    /// <summary>构造不等同磁盘 PBIP 入口的已连接模型快照, 避免把文件存在误判为连接成功.</summary>
    private static ProjectSnapshot ConnectedSnapshot() => new("model://created-desktop", "created", "connected-baseline", "TMDL", "PBIR", "4.2", null,
        new("Created", 1702, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));

    /// <summary>跨后台排队边界必须传递创建时的原身份, 排队期间替换同路径文件不能重建授权.</summary>
    [Theory]
    [InlineData("scaffold.openDirectory")]
    [InlineData("scaffold.openDesktop")]
    public async Task QueuedOpenPreservesCreationIdentityAfterReplacement(string method)
    {
        await bridge!.ShutdownAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? forwardedIdentity = null;
        var launchAccepted = false;
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root),
            openDirectory: OpenQueuedAsync, openDesktop: OpenQueuedAsync);
        var entry = await CreateProjectAsync();
        var creationIdentity = ProjectFileIdentity.Read(entry);
        var opening = Call(method, new { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            File.Move(entry, entry + ".original");
            File.Copy(entry + ".original", entry);
            Assert.NotEqual(creationIdentity, ProjectFileIdentity.Read(entry));
            resume.SetResult();
            Assert.Equal("CREATED_PROJECT_NOT_AVAILABLE", (await Assert.ThrowsAsync<AppException>(() => opening)).Code);
            Assert.Equal(creationIdentity, forwardedIdentity);
            Assert.False(launchAccepted);
        }
        finally { resume.TrySetResult(); }

        /// <summary>模拟宿主后台启动前的排队, 用传入原身份复核而非重新采样授权.</summary>
        /// <param name="path">分发器绑定的真实 PBIP 入口.</param>
        /// <param name="identity">创建回执绑定并贯穿传递的原文件身份.</param>
        /// <param name="token">后台恢复后仍需检查的取消令牌.</param>
        async Task<ProjectSnapshot> OpenQueuedAsync(string path, string identity, CancellationToken token)
        {
            forwardedIdentity = identity;
            entered.SetResult();
            await resume.Task;
            token.ThrowIfCancellationRequested();
            if (ProjectFileIdentity.Read(path) != identity) throw new AppException("CREATED_PROJECT_NOT_AVAILABLE", "请先创建并核验工程", 3);
            launchAccepted = true;
            return ConnectedSnapshot();
        }
    }

    /// <summary>未发布或核验失败的新回执撤销旧打开权限, 不退回上一个成功目标.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task UnverifiedReceiptRevokesPreviousOpenTarget(bool projectCreated, bool verified)
    {
        await CreateProjectAsync();
        await CreateProjectAsync("未完成", projectCreated, verified, false);
        foreach (var method in new[] { "scaffold.openDirectory", "scaffold.openDesktop" })
            Assert.Equal("CREATED_PROJECT_NOT_AVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Call(method, new { }))).Code);
        Assert.Empty(openedProjects);
        Assert.Empty(openedDirectories);
    }

    /// <summary>成功改选输出或撤销文档后, 原真实工程仍保留但不再可用作打开授权.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputSelectionAndInvalidationRevokeOpenTarget(bool invalidate)
    {
        var entry = await CreateProjectAsync();
        if (invalidate) bridge!.Invalidate(); else await Call("scaffold.chooseOutput", new { });
        foreach (var method in new[] { "scaffold.openDirectory", "scaffold.openDesktop" })
            Assert.Equal("CREATED_PROJECT_NOT_AVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Call(method, new { }))).Code);
        Assert.True(File.Exists(entry));
    }

    /// <summary>同路径被替换、删除或改成链接时拒绝打开, 不按可见文件名恢复权限.</summary>
    [Theory]
    [InlineData("replace")]
    [InlineData("missing")]
    [InlineData("link")]
    public async Task ChangedCreatedEntryCannotOpen(string change)
    {
        var entry = await CreateProjectAsync();
        var directory = Path.GetDirectoryName(entry)!;
        if (change == "link")
        {
            Directory.Move(directory, directory + ".original");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", directory, directory + ".original" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            File.Move(entry, entry + ".original");
            if (change == "replace") await File.WriteAllTextAsync(entry, "{}");
        }
        try
        {
            foreach (var method in new[] { "scaffold.openDirectory", "scaffold.openDesktop" })
                Assert.Equal("CREATED_PROJECT_NOT_AVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Call(method, new { }))).Code);
            Assert.Empty(openedProjects);
            Assert.Empty(openedDirectories);
        }
        finally { if (change == "link") Directory.Delete(directory); }
    }

    /// <summary>两个打开动作共享模型事务门闩, 已接受的系统打开排空前不允许并发启动另一动作.</summary>
    [Fact]
    public async Task OpenActionsShareSessionProjectGate()
    {
        await bridge!.ShutdownAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root),
            openDirectory: async (_, _, _) => { entered.SetResult(); await resume.Task; },
            openDesktop: (path, _, _) => { openedProjects.Add(path); return Task.FromResult(ConnectedSnapshot()); });
        await CreateProjectAsync();
        var output = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", scaffold: bridge);
        var opening = session.ReceiveAsync(OriginPolicy.Production, Request("directory", "scaffold.openDirectory"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await session.ReceiveAsync(OriginPolicy.Production, Request("desktop", "scaffold.openDesktop"));
            Assert.Equal("BUSY", output.Last(item => item.TryGetProperty("id", out var id) && id.GetString() == "desktop").GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(openedProjects);
        }
        finally { resume.SetResult(); await opening; await session.ShutdownAsync(); }
    }

    /// <summary>待保存、部分保存和未知提交都不能启动 Desktop, 查看真实项目目录仍可用.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "SAVE_FAILED")]
    [InlineData(false, "MODEL_COMMIT_NEEDS_VERIFICATION")]
    public async Task DesktopLaunchRejectsUnfinishedSave(bool pending, string? errorCode)
    {
        var entry = await CreateProjectAsync();
        var snapshot = new ProjectSnapshot(entry, "created", "baseline", "TMDL", "PBIR", "4.2", null,
            new("Test", 1702, "en-US", [], [], [], 0, 0), [], [], [], HasPendingChanges: pending,
            SaveResult: errorCode is null ? null : new(true, false, false, "unfinished", errorCode));
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var compiler = new ModelConfigurationCompiler();
        using var configuration = new ModelConfigurationBridge(new OpeningReader(snapshot), workspace, compiler);
        configuration.BindCreated(adapter.CreateResult!);
        var output = new List<JsonElement>();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => output.Add(JsonSerializer.Deserialize<JsonElement>(message)), "test", scaffold: bridge, configuration: configuration);
        try
        {
            await session.ReceiveAsync(OriginPolicy.Production, Request("desktop", "scaffold.openDesktop"));
            Assert.Equal("UNSAVED_CHANGES", output.Last().GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(openedProjects);
            await session.ReceiveAsync(OriginPolicy.Production, Request("directory", "scaffold.openDirectory"));
            Assert.True(output.Last().GetProperty("result").GetProperty("opened").GetBoolean());
            Assert.Single(openedDirectories);
        }
        finally { await session.ShutdownAsync(); }
    }

    /// <summary>构造空参数宿主消息, 不给测试开任意目标参数通道.</summary>
    private static string Request(string id, string method, long connectionRevision = 0) => JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = new { }, connectionRevision });

    /// <summary>只替代配置上下文的只读快照, 不运行模型编辑或真实引擎.</summary>
    private sealed class OpeningReader(ProjectSnapshot snapshot) : IProjectReader
    {
        /// <summary>返回指定保存事实供实际宿主门禁核验.</summary>
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken token) => Task.FromResult(snapshot);
    }

    /// <summary>仅测试拥有目录中准备入口并消费当前实际计划, 回执可精确模拟发布与核验事实.</summary>
    /// <param name="name">隔离工程目录和入口名称.</param>
    /// <param name="projectCreated">回执是否实际发布.</param>
    /// <param name="verified">回执是否完成权威核验.</param>
    /// <param name="choose">是否先经过系统目录选择回调.</param>
    /// <returns>本测试创建的完整 PBIP 入口.</returns>
    private async Task<string> CreateProjectAsync(string name = "项目", bool projectCreated = true, bool verified = true, bool choose = true)
    {
        if (choose) await Call("scaffold.chooseOutput", new { });
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, name + ".pbip");
        await File.WriteAllTextAsync(entry, "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"Project.Report\"}}]}");
        Directory.CreateDirectory(Path.Combine(directory, "Project.Report"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Project.Report", "definition.pbir"), "{}");
        adapter.CreateResult = new("id", "published", projectCreated, verified, false, entry, "baseline", [], []);
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = Base(name) });
        var draftId = identity.GetProperty("draftId").GetString();
        var revision = identity.GetProperty("revision").GetInt32();
        var plan = await Call("scaffold.preview", new { draftId, revision });
        await Call("scaffold.create", new { draftId, revision, digest = plan.GetProperty("digest").GetString() });
        return entry;
    }

    /// <summary>草稿与目录在宿主绑定后才可预览, 旧revision不能覆盖新草稿.</summary>
    [Fact]
    public async Task BindsDraftAndRequiresCurrentRevision()
    {
        var created = await Call("scaffold.updateDraft", new { section = "base", value = Base() });
        var id = created.GetProperty("draftId").GetString();
        var revision = created.GetProperty("revision").GetInt32();
        Assert.Equal("OUTPUT_NOT_SELECTED", (await Assert.ThrowsAsync<AppException>(() => Call("scaffold.preview", new { draftId = id, revision }))).Code);
        await Call("scaffold.chooseOutput", new { });
        var plan = await Call("scaffold.preview", new { draftId = id, revision });
        Assert.Equal(Path.Combine(root, "项目"), plan.GetProperty("outputPath").GetString());
        await Call("scaffold.updateDraft", new { draftId = id, revision, section = "base", value = Base("另一个项目") });
        Assert.Equal("STALE_SCAFFOLD_DRAFT", (await Assert.ThrowsAsync<AppException>(() => Call("scaffold.preview", new { draftId = id, revision }))).Code);
        Assert.Equal(1, adapter.DiscardCalls);
    }

    /// <summary>文档失效使既有草稿和输出授权失效, 迟到ID不能恢复.</summary>
    [Fact]
    public async Task InvalidatedDocumentCannotReuseDraft()
    {
        var created = await Call("scaffold.updateDraft", new { section = "base", value = Base() });
        bridge!.Invalidate();
        Assert.Equal("STALE_SCAFFOLD_DRAFT", (await Assert.ThrowsAsync<AppException>(() => Call("scaffold.preview", new
        { draftId = created.GetProperty("draftId").GetString(), revision = created.GetProperty("revision").GetInt32() }))).Code);
    }

    /// <summary>嵌套未知字段也不能绕过严格业务输入.</summary>
    [Fact]
    public async Task RejectsUnknownDraftFields()
    {
        await Assert.ThrowsAsync<AppException>(() => Call("scaffold.updateDraft", new
        {
            section = "base",
            value = new
            { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "项目", locale = "zh-cn", gitInitialize = false, outputPath = "injected" }
        }));
    }

    /// <summary>缺失必需字段不能被默认 false、零索引或 null 删除操作代替.</summary>
    [Theory]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"description\":\"\",\"displayFolder\":\"\"}]")]
    [InlineData("workday", "{\"date\":\"2026-10-19\",\"name\":\"Missing boolean\"}")]
    [InlineData("measure", "{\"item\":null}")]
    [InlineData("measure", "{\"index\":0}")]
    public async Task RejectsMissingRequiredDraftFields(string section, string value)
    {
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = Base() });
        identity = await Update(identity, "calendar", new { kind = "simple", startYear = 2026, endYear = 2026 });
        identity = await Update(identity, "measures", new[] { new TemplateMeasureDefinition("Test", "1", "Uncategorized", true, "", "") });
        using var parsed = JsonDocument.Parse(value);
        var error = await Assert.ThrowsAsync<AppException>(() => Update(identity, section, parsed.RootElement));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        await Call("scaffold.chooseOutput", new { });
        var plan = await Call("scaffold.preview", new { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32() });
        Assert.True(plan.GetProperty("request").GetProperty("measures")[0].GetProperty("enabled").GetBoolean());
    }

    /// <summary>显式 false 与协议可省略的身份、Git、函数版本仍保持各自含义.</summary>
    [Fact]
    public async Task PreservesExplicitFalseAndOptionalDraftDefaults()
    {
        var identity = await Call("scaffold.updateDraft", new
        {
            section = "base",
            value = new
            { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = "项目", locale = "zh-cn" }
        });
        identity = await Update(identity, "calendar", new { kind = "simple", startYear = 2026, endYear = 2026 });
        identity = await Update(identity, "workday", new { date = "2026-10-19", isWorkday = false, name = "Explicit false" });
        identity = await Update(identity, "measures", new[] { new TemplateMeasureDefinition("Test", "1", "Uncategorized", false, "", "") });
        identity = await Update(identity, "udfs", new[] { new { source = "daxlib", packageId = "test.package", functions = stringArray } });
        identity = await Update(identity, "udfFunction", new { source = "daxlib", packageId = "test.package", functionName = "Test.Other", selected = true });
        await Call("scaffold.chooseOutput", new { });
        var plan = await Call("scaffold.preview", new { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32() });
        var request = plan.GetProperty("request");
        Assert.False(request.GetProperty("gitInitialize").GetBoolean());
        Assert.False(request.GetProperty("measures")[0].GetProperty("enabled").GetBoolean());
        Assert.False(request.GetProperty("calendar").GetProperty("overrides")[0].GetProperty("isWorkday").GetBoolean());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("udfs")[0].GetProperty("version").ValueKind);
        identity = await Update(identity, "measure", new { index = 0, item = (TemplateMeasureDefinition?)null });
        plan = await Call("scaffold.preview", new { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32() });
        Assert.Empty(plan.GetProperty("request").GetProperty("measures").EnumerateArray());
    }

    /// <summary>创建本地函数不要求不存在的 ID 和基线, 更新缺基线仍拒绝且保留实际内容.</summary>
    [Fact]
    public async Task LocalCreateAllowsOmittedIdentityButUpdateRequiresBaseline()
    {
        await bridge!.ShutdownAsync();
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root), local: new LocalUdfStore(root));
        var definition = new { schemaVersion = 1, name = "Local.Strict", version = "1.0.0", expression = "() => 1", description = "", dependencies = Array.Empty<object>() };
        var created = await Call("udf.localSave", new { definition });
        var id = created.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => Call("udf.localSave", new { id, definition }))).Code);
        var actual = await Call("udf.localGet", new { id });
        Assert.Equal(created.GetProperty("baseline").GetString(), actual.GetProperty("baseline").GetString());
        await Call("udf.localDelete", new { id, baseline = actual.GetProperty("baseline").GetString(), confirm = true });
    }

    /// <summary>来源管理通过真实持久库, 所有变更严格字段并绑定当前配置基线.</summary>
    [Fact]
    public async Task CustomSourcesUseStrictHostRegistryAndSelectionBaselines()
    {
        var sources = new UdfSourceProvider(root);
        bridge = new(new Catalog(), new ScaffoldService(new Adapter()), root, _ => Task.FromResult<string?>(root), sources: sources, release: sources.Dispose);
        var listed = await Call("udf.sources", new { });
        Assert.Equal(2, listed.GetProperty("items").GetArrayLength());
        var created = await Call("udf.sourceSave", new { name = "Team", baseUrl = "https://github.com/example/functions" });
        var id = created.GetProperty("id").GetString(); var baseline = created.GetProperty("baseline").GetString();
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => Call("udf.sourceSave", new { id, name = "Team", baseUrl = "https://github.com/example/other" }))).Code);
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => Call("udf.sourceDelete", new { id, baseline, confirm = false }))).Code);
        await Assert.ThrowsAsync<AppException>(() => Call("udf.sources", new { url = "https://github.com/example/other" }));
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = Base() });
        await Update(identity, "udfFunction", new { source = id, sourceBaseline = baseline, packageId = "Team.Library", functionName = "Team.One", selected = true, version = "1.0.0" });
        await Call("udf.sourceDelete", new { id, baseline, confirm = true });
        Assert.Equal(2, (await Call("udf.sources", new { })).GetProperty("items").GetArrayLength());
    }

    /// <summary>每次修改使用宿主刚返回的真实草稿身份.</summary>
    private Task<JsonElement> Update(JsonElement identity, string section, object value) => Call("scaffold.updateDraft", new
    { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32(), section, value });

    /// <summary>大覆盖可以一次冻结, 不放宽其他功能段, 解析也不接受网页文件路径.</summary>
    [Fact]
    public async Task CalendarBulkUsesNarrowBudgetAndContentOnlyParsing()
    {
        await Call("scaffold.chooseOutput", new { });
        var identity = await Call("scaffold.updateDraft", new { section = "base", value = Base() });
        var values = Enumerable.Range(0, 1500).Select(index => new WorkdayOverride(new DateOnly(2020, 1, 1).AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), true, "业务覆盖")).ToArray();
        identity = await Update(identity, "calendar", new ScaffoldCalendarOptions("full", 2020, 2025, Overrides: values));
        var plan = await Call("scaffold.preview", new { draftId = identity.GetProperty("draftId").GetString(), revision = identity.GetProperty("revision").GetInt32() });
        Assert.Equal(1500, plan.GetProperty("request").GetProperty("calendar").GetProperty("overrides").GetArrayLength());
        await Assert.ThrowsAsync<AppException>(() => Call("scaffold.updateDraft", new { section = "base", value = new { text = new string('x', 70000) } }));
        var parsed = await Call("calendar.parseWorkdays", new { text = "date,isWorkday,name\n2024-01-01,false,假期", delimiter = "csv", startYear = 2024, endYear = 2024 });
        Assert.False(parsed.GetProperty("overrides")[0].GetProperty("isWorkday").GetBoolean());
        await Assert.ThrowsAsync<AppException>(() => Call("calendar.parseWorkdays", new { text = "", delimiter = "csv", startYear = 2024, endYear = 2024, path = "forbidden" }));
    }

    /// <summary>跨宿主实例读取同一偏好, 字段顺序由目录规范化且不含工程上下文.</summary>
    [Fact]
    public async Task CalendarPreferencesPersistOnlyUserScheme()
    {
        var saved = await Call("calendar.savePreferences", new { fields = PreferenceInputFields, numberedColumns = false });
        Assert.Equal(PreferenceOutputFields, saved.GetProperty("fields").EnumerateArray().Select(item => item.GetString()));
        await bridge!.ShutdownAsync();
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => Task.FromResult<string?>(root));
        var read = await Call("calendar.preferences", new { });
        Assert.False(read.GetProperty("numberedColumns").GetBoolean());
        await Assert.ThrowsAsync<AppException>(() => Call("calendar.savePreferences", new { fields = DateOnlyFields, numberedColumns = true, tableName = "forbidden" }));
    }

    /// <summary>目录选择占用模型事务门闩时, 偏好与纯内容解析仍可完成, 其他草稿操作继续拒绝重入.</summary>
    [Fact]
    public async Task CalendarRequestsDoNotCompeteWithWorkspaceGate()
    {
        await bridge!.ShutdownAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge = new(new Catalog(), new ScaffoldService(adapter), root, _ => { entered.SetResult(); return resume.Task; });
        var selecting = Call("scaffold.chooseOutput", new { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var saved = await Call("calendar.savePreferences", new { fields = DateOnlyFields, numberedColumns = false });
            Assert.False(saved.GetProperty("numberedColumns").GetBoolean());
            Assert.False((await Call("calendar.preferences", new { })).GetProperty("numberedColumns").GetBoolean());
            Assert.Single((await Call("calendar.parseWorkdays", new { text = "date,isWorkday,name\n2024-01-01,true,", delimiter = "csv", startYear = 2024, endYear = 2024 })).GetProperty("overrides").EnumerateArray());
            Assert.Equal("SCAFFOLD_BUSY", (await Assert.ThrowsAsync<AppException>(() => Call("scaffold.updateDraft", new { section = "base", value = Base() }))).Code);
        }
        finally { resume.TrySetResult(root); await selecting; }
    }

    /// <summary>后台偏好提交不阻塞草稿, 排队纯请求可取消或因文档失效拒绝, 关闭仍排空已经接受的提交.</summary>
    [Fact]
    public async Task BackgroundPreferencesPreserveWorkspaceAndLifecycleBoundaries()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var saving = bridge!.HandleAsync("calendar.savePreferences", JsonSerializer.SerializeToElement(new { fields = DateOnlyFields, numberedColumns = false }, JsonSerializerOptions.Web), null, CancellationToken.None,
            () => { entered.SetResult(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(JsonValueKind.String, (await Call("scaffold.updateDraft", new { section = "base", value = Base() })).GetProperty("draftId").ValueKind);
            var reading = Call("calendar.preferences", new { });
            using var cancellation = new CancellationTokenSource();
            var parsing = bridge.HandleAsync("calendar.parseWorkdays", JsonSerializer.SerializeToElement(new { text = "date,isWorkday,name\n2024-01-01,true,", delimiter = "csv", startYear = 2024, endYear = 2024 }, JsonSerializerOptions.Web), null, cancellation.Token);
            var cancelledSave = bridge.HandleAsync("calendar.savePreferences", JsonSerializer.SerializeToElement(new { fields = DateOnlyFields, numberedColumns = true }, JsonSerializerOptions.Web), null, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledSave);
            bridge.Invalidate();
            var closing = bridge.ShutdownAsync();
            Assert.False(closing.IsCompleted);
            resume.Set();
            var saved = JsonSerializer.SerializeToElement(await saving, JsonSerializerOptions.Web);
            Assert.False(saved.GetProperty("numberedColumns").GetBoolean());
            Assert.Equal("STALE_SCAFFOLD_DRAFT", (await Assert.ThrowsAsync<AppException>(() => reading)).Code);
            await closing;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Call("calendar.preferences", new { }));
        }
        finally { resume.Set(); await saving; }
    }

    /// <summary>关闭排空并清理测试拥有目录.</summary>
    public async Task DisposeAsync() { if (bridge is not null) await bridge.ShutdownAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    /// <summary>通过实际 JSON 类型边界调用.</summary>
    private async Task<JsonElement> Call(string method, object parameters) => JsonSerializer.SerializeToElement(await bridge!.HandleAsync(method,
        JsonSerializer.SerializeToElement(parameters, JsonSerializerOptions.Web), null, CancellationToken.None), JsonSerializerOptions.Web);
    /// <summary>返回最小完整基础字段.</summary>
    private static object Base(string name = "项目") => new { templateId = "builtin:blank", templateBaseline = new string('a', 64), projectName = name, locale = "zh-cn", gitInitialize = false };

    /// <summary>只替代磁盘适配器, 用于观察实际绑定参数.</summary>
    private sealed class Adapter : IScaffoldWorkspace
    {
        internal int PreviewCalls { get; private set; }
        internal int DiscardCalls { get; private set; }
        internal int CreateCalls { get; private set; }
        internal ScaffoldResult? CreateResult { get; set; }
        /// <summary>观察宿主派生输出, 不伪称真实文件生成.</summary>
        public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken)
        { PreviewCalls++; return Task.FromResult(new ScaffoldPlan("id", new string('c', 64), DateTimeOffset.UtcNow.AddMinutes(15), output, request, "test.pbip", [], [], [], 1606)); }
        /// <summary>只返回测试准备的回执, 用于验证分发器是否从实际创建结果授予受限打开权限.</summary>
        public async Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null)
        { if (beforeCommit is not null) await beforeCommit(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); CreateCalls++; committing?.Invoke(); return CreateResult ?? throw new NotSupportedException(); }
        /// <summary>记录丢弃次数证明修改使原计划失效.</summary>
        public Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken) { DiscardCalls++; return Task.FromResult(true); }
        /// <summary>没有环境资源.</summary>
        public void Dispose() { }
    }
    /// <summary>最小模板目录, 不执行文件读取.</summary>
    private sealed class Catalog : IPbipTemplateCatalog
    {
        /// <summary>返回空清单即可验证授权边界.</summary>
        public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PbipTemplateInfo>>([]);
        /// <summary>此测试不进行实际模板读取.</summary>
        public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
