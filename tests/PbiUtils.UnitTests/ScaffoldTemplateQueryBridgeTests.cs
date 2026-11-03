// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldTemplateQueryBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实桥接、持久计划和 PBIP 组合验证模板查询选择的冻结及拒绝边界.

using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Protocol;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Application.TemplateMeasures;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅在测试独占根中执行真实新建, 不连接 Desktop 或写真实用户配置.</summary>
public sealed class ScaffoldTemplateQueryBridgeTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-query-bridge-" + Guid.NewGuid().ToString("N"));
    private ScaffoldBridge? bridge;
    private ScaffoldTemplateCatalog? catalog;
    private static readonly string[] Hour24Selection = ["Hour24"];
    private string UserRoot => Path.Combine(root, "user");

    /// <summary>模式经严格分段请求进入冻结摘要, 修改模式会撤销旧计划, 未知值不改变草稿.</summary>
    [Fact]
    public async Task MeasureStorageModeIsFrozenAndRejectsUnknownValue()
    {
        var identity = await StartAsync();
        identity = await CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measures",
            value = JsonSerializer.SerializeToElement(TemplateMeasureValidation.DefaultMeasures(), JsonSerializerOptions.Web)
        });
        identity = await CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measuresMode",
            value = "config"
        });
        var plan = await CallAsync("scaffold.preview", identity);
        Assert.Equal("config", plan.GetProperty("request").GetProperty("measuresMode").GetString());
        var frozen = plan.GetProperty("request").Deserialize<ScaffoldRequest>(ScaffoldInputJson.Create())!;
        using var second = new ScaffoldWorkspace(UserRoot, catalog!, new ScaffoldModelWriter());
        var output = plan.GetProperty("outputPath").GetString()!;
        var mismatch = await Assert.ThrowsAsync<AppException>(() => second.CreateAsync(plan.GetProperty("digest").GetString()!,
            frozen with { MeasuresMode = "tmdl" }, output, null, default));
        Assert.Equal("SCAFFOLD_APPROVAL_MISMATCH", mismatch.Code);
        Assert.False(Directory.Exists(output));
        var invalid = await Assert.ThrowsAsync<AppException>(() => CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measuresMode",
            value = "unknown"
        }));
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", invalid.Code);
        var unchanged = await CallAsync("scaffold.preview", identity);
        Assert.Equal("config", unchanged.GetProperty("request").GetProperty("measuresMode").GetString());
        var changed = await CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measuresMode",
            value = "tmdl"
        });
        var next = await CallAsync("scaffold.preview", changed);
        Assert.Equal("tmdl", next.GetProperty("request").GetProperty("measuresMode").GetString());
    }

    /// <summary>将系统目录选择绑定为本测试根, 其他业务均使用真实共享实现.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        catalog = new(UserRoot);
        bridge = new(catalog, new(new ScaffoldWorkspace(UserRoot, catalog, new ScaffoldModelWriter())), UserRoot,
            _ => Task.FromResult<string?>(root));
        return Task.CompletedTask;
    }

    /// <summary>网页选择进入持久摘要, 改选择不能消费原批准, 实际生成只包含本次标准查询.</summary>
    [Fact]
    public async Task QuerySelectionIsFrozenAndChangedSelectionCannotReuseApproval()
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, Hour24Selection);
        var plan = await CallAsync("scaffold.preview", identity);
        var request = plan.GetProperty("request").Deserialize<ScaffoldRequest>(ScaffoldInputJson.Create())!;
        Assert.NotNull(request.TemplateQueries);
        Assert.Equal(["Hour24"], request.TemplateQueries);
        var digest = plan.GetProperty("digest").GetString()!;
        var output = plan.GetProperty("outputPath").GetString()!;
        using var second = new ScaffoldWorkspace(UserRoot, catalog!, new ScaffoldModelWriter());
        var changed = request with { TemplateQueries = ["MQHY"] };
        Assert.Equal("SCAFFOLD_APPROVAL_MISMATCH", (await Assert.ThrowsAsync<AppException>(() => second.CreateAsync(digest, changed, output, null, default))).Code);
        Assert.False(Directory.Exists(output));
        var created = await CallAsync("scaffold.create", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            digest
        });
        Assert.True(created.GetProperty("verified").GetBoolean());
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(output, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        Assert.NotNull(database.Model.Tables.Find("Hour24"));
        Assert.Null(database.Model.Tables.Find("MQHY"));
    }

    /// <summary>未知和重复选择在更新前拒绝, 原草稿身份及已冻结计划仍可创建.</summary>
    [Theory]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[\"Hour24\",\"Hour24\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\" Hour24\"]")]
    public async Task InvalidSelectionPreservesCurrentDraftAndPlan(string selection)
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, Hour24Selection);
        var plan = await CallAsync("scaffold.preview", identity);
        using var invalid = JsonDocument.Parse(selection);
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", (await Assert.ThrowsAsync<AppException>(() => UpdateAsync(identity, invalid.RootElement))).Code);
        var created = await CallAsync("scaffold.create", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            digest = plan.GetProperty("digest").GetString()
        });
        Assert.True(created.GetProperty("projectCreated").GetBoolean());
        Assert.True(created.GetProperty("verified").GetBoolean());
    }

    /// <summary>当前偏好读取保留原文件, 启用模板度量后可经真实桥接保存并冻结预览.</summary>
    [Fact]
    public async Task CurrentMeasurePreferencesCanBeEnabledAndPreviewed()
    {
        Directory.CreateDirectory(UserRoot);
        var path = Path.Combine(UserRoot, "template-measure-preferences.json");
        TemplateMeasureDefinition[] definitions = [new("custom_time", "fixed", "Uncategorized", true, "原说明", "原目录"), new("custom_link", "https://example.org", "WebUrl", false, "自定义说明", "自定义目录")];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, measures = definitions }, JsonSerializerOptions.Web);
        await File.WriteAllBytesAsync(path, bytes);
        var templates = await CallAsync("scaffold.templates", new { });
        Assert.Equal(JsonValueKind.Null, templates.GetProperty("measurePreferencesWarning").ValueKind);
        var measures = templates.GetProperty("defaultMeasures");
        Assert.Equal("custom_time", measures[0].GetProperty("name").GetString());
        Assert.Equal("Uncategorized", measures[0].GetProperty("dataCategory").GetString());
        Assert.Equal("fixed", measures[0].GetProperty("value").GetString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var identity = await StartAsync();
        identity = await CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "measures",
            value = measures
        });
        var plan = await CallAsync("scaffold.preview", identity);
        var plannedMeasures = plan.GetProperty("request").GetProperty("measures").Deserialize<TemplateMeasureDefinition[]>(BridgeBusinessInput.Json)!;
        Assert.Equal(definitions, plannedMeasures);
        var saved = new LocalTemplateMeasurePreferencesStore(UserRoot).Read();
        Assert.Null(saved.Warning);
        Assert.Equal(2, saved.Measures.Length);
        Assert.Equal("Uncategorized", saved.Measures[0].DataCategory);
        Assert.Equal("custom_link", saved.Measures[1].Name);
        Assert.False(saved.Measures[1].Enabled);
        Assert.DoesNotContain("RefreshTime", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    /// <summary>分组映射进入冻结计划, 只改组名也不能复用原批准; 原批准实际创建保持原组名.</summary>
    [Theory]
    [InlineData("Hour24")]
    [InlineData("RefreshTime")]
    public async Task GroupChangeCannotReuseFrozenApproval(string query)
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, new[] { query });
        identity = await UpdateGroupsAsync(identity, new Dictionary<string, string> { [query] = "小时维度" });
        var plan = await CallAsync("scaffold.preview", identity);
        Assert.Equal("小时维度", plan.GetProperty("request").GetProperty("templateQueryGroups").GetProperty(query).GetString());
        var request = plan.GetProperty("request").Deserialize<ScaffoldRequest>(ScaffoldInputJson.Create())!;
        var changed = request with { TemplateQueryGroups = new() { [query] = "其他维度" } };
        var digest = plan.GetProperty("digest").GetString()!;
        var output = plan.GetProperty("outputPath").GetString()!;
        using var second = new ScaffoldWorkspace(UserRoot, catalog!, new ScaffoldModelWriter());
        Assert.Equal("SCAFFOLD_APPROVAL_MISMATCH", (await Assert.ThrowsAsync<AppException>(() => second.CreateAsync(digest, changed, output, null, default))).Code);
        Assert.False(Directory.Exists(output));
        var created = await CreateAsync(identity, plan);
        Assert.True(created.GetProperty("verified").GetBoolean());
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(output, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        Assert.Equal("小时维度", database.Model.Tables[query].Partitions[0].QueryGroup?.Folder);
    }

    /// <summary>非法映射或值类型更新不消耗草稿 revision, 不废弃既有冻结计划.</summary>
    [Theory]
    [InlineData("{\"Unknown\":\"group\"}", "INVALID_SCAFFOLD_REQUEST")]
    [InlineData("{\"Hour24\":null}", "INVALID_SCAFFOLD_REQUEST")]
    [InlineData("{\"Hour24\":\" \"}", "INVALID_SCAFFOLD_REQUEST")]
    [InlineData("{\"Hour24\":\"group\\nname\"}", "INVALID_SCAFFOLD_REQUEST")]
    [InlineData("[]", "VALIDATION_ERROR")]
    [InlineData("1", "VALIDATION_ERROR")]
    [InlineData("{\"Hour24\":1}", "VALIDATION_ERROR")]
    [InlineData("{\"Hour24\":{\"folder\":\"nested\"}}", "VALIDATION_ERROR")]
    public async Task InvalidGroupUpdatePreservesFrozenPlan(string groupsJson, string expectedCode)
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, Hour24Selection);
        identity = await UpdateGroupsAsync(identity, new Dictionary<string, string> { ["Hour24"] = "最初分组" });
        var plan = await CallAsync("scaffold.preview", identity);
        using var invalid = JsonDocument.Parse(groupsJson);
        Assert.Equal(expectedCode, (await Assert.ThrowsAsync<AppException>(() => UpdateGroupsAsync(identity, invalid.RootElement))).Code);
        var created = await CreateAsync(identity, plan);
        Assert.True(created.GetProperty("projectCreated").GetBoolean());
        var output = plan.GetProperty("outputPath").GetString()!;
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(output, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        Assert.Equal("最初分组", database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
    }

    /// <summary>分组段仍使用严格更新信封, 额外授权或任意字段不能改变原计划.</summary>
    [Fact]
    public async Task GroupUpdateRejectsUnknownEnvelopeFields()
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, Hour24Selection);
        var plan = await CallAsync("scaffold.preview", identity);
        var error = await Assert.ThrowsAsync<AppException>(() => CallAsync("scaffold.updateDraft", new
        {
            draftId = identity.GetProperty("draftId").GetString(),
            revision = identity.GetProperty("revision").GetInt32(),
            section = "templateQueryGroups",
            value = new Dictionary<string, string> { ["Hour24"] = "group" },
            approved = true
        }));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.True((await CreateAsync(identity, plan)).GetProperty("verified").GetBoolean());
    }

    /// <summary>明确 null 解除整份覆盖后恢复目录默认分组, 与单项空字符串的不分组语义不同.</summary>
    [Fact]
    public async Task NullGroupMapRestoresDefaultGrouping()
    {
        var identity = await StartAsync();
        identity = await UpdateAsync(identity, Hour24Selection);
        identity = await UpdateGroupsAsync(identity, new Dictionary<string, string> { ["Hour24"] = "小时维度" });
        identity = await UpdateGroupsAsync(identity, null);
        var plan = await CallAsync("scaffold.preview", identity);
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("request").GetProperty("templateQueryGroups").ValueKind);
        Assert.True((await CreateAsync(identity, plan)).GetProperty("verified").GetBoolean());
        var output = plan.GetProperty("outputPath").GetString()!;
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(output, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        Assert.Equal("model", database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
    }

    /// <summary>提交可选分组段, JSON 对象值类型和业务键由正式桥接及共享校验处理.</summary>
    /// <param name="identity">宿主返回的当前草稿身份.</param>
    /// <param name="value">分组字典或待拒绝的 JSON, null 解除覆盖.</param>
    /// <returns>新身份; 输入无效时抛稳定错误且不修改原草稿.</returns>
    private Task<JsonElement> UpdateGroupsAsync(JsonElement identity, object? value) => CallAsync("scaffold.updateDraft", new
    {
        draftId = identity.GetProperty("draftId").GetString(),
        revision = identity.GetProperty("revision").GetInt32(),
        section = "templateQueryGroups",
        value
    });

    /// <summary>使用未变更的草稿身份和原摘要创建, 不自行重建或放宽批准.</summary>
    private Task<JsonElement> CreateAsync(JsonElement identity, JsonElement plan) => CallAsync("scaffold.create", new
    {
        draftId = identity.GetProperty("draftId").GetString(),
        revision = identity.GetProperty("revision").GetInt32(),
        digest = plan.GetProperty("digest").GetString()
    });

    /// <summary>使用真实内置模板的当前基线和宿主选择建立最小草稿.</summary>
    private async Task<JsonElement> StartAsync()
    {
        var template = await catalog!.GetAsync("builtin:blank", default);
        await CallAsync("scaffold.chooseOutput", new { });
        return await CallAsync("scaffold.updateDraft", new
        {
            section = "base",
            value = new { templateId = template.Info.Id, templateBaseline = template.Info.Baseline, projectName = "queries", locale = "zh-cn" }
        });
    }

    /// <summary>提交模板查询段, 始终使用上一轮宿主返回的草稿身份.</summary>
    /// <param name="identity">有效草稿 ID 和 revision.</param>
    /// <param name="value">待验证的查询选择 JSON 值.</param>
    /// <returns>更新后的宿主身份; 无效选择抛稳定应用错误.</returns>
    private Task<JsonElement> UpdateAsync(JsonElement identity, object value) => CallAsync("scaffold.updateDraft", new
    {
        draftId = identity.GetProperty("draftId").GetString(),
        revision = identity.GetProperty("revision").GetInt32(),
        section = "templateQueries",
        value
    });

    /// <summary>执行生产桥接入口并按正式 JSON 配置读取响应, 不伪造计划或成功回执.</summary>
    private async Task<JsonElement> CallAsync(string method, object parameters) => JsonSerializer.SerializeToElement(
        await bridge!.HandleAsync(method, JsonSerializer.SerializeToElement(parameters), null, default), BridgeBusinessInput.Json);

    /// <summary>先关闭并排空测试宿主及未提交计划, 再删除本测试分配的根目录.</summary>
    public async Task DisposeAsync()
    {
        if (bridge is not null) await bridge.ShutdownAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
