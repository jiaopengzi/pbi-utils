// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteModelTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过完整 MCP 桥接在独占临时 PBIP 上验证编辑、保存、放弃与配置写入.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Execution;
using PbiUtils.Application.Models.Configuration;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实文件事务及 MCP 授权/代际/回执验收, 不连接任何用户模型.</summary>
public sealed class McpWriteModelTests
{
    /// <summary>精确授权基于真实 before/after, 成功响应及回执必须对应已落盘的新基线.</summary>
    [Fact]
    public async Task ModelEditPersistsAuthorizedChangeAndCompletedReceipt()
    {
        await using var fixture = await ModelFixture.CreateAsync();
        var before = await fixture.InspectAsync();
        var parameters = Json(new { operation = "table.set", objectName = "销售", property = "description", value = "MCP 写入", table = "", pageId = "" });
        var result = Assert.IsType<ProjectSnapshot>(await fixture.CallAsync("model.edit", parameters));
        var proposal = Assert.Single(fixture.Proposals);
        Assert.Equal(before.BaselineId, proposal.BaselineId);
        Assert.Equal(before.ProjectId, proposal.WorkspaceId);
        Assert.Equal("pbip", proposal.ConnectionKind);
        var change = Assert.Single(proposal.Changes);
        Assert.Equal(new ToolPropertyChange("销售", "description", "原说明", "MCP 写入"), change);
        var actual = await fixture.Reader.ReadAsync(fixture.Entry, default);
        Assert.Equal("MCP 写入", Assert.Single(actual.Model.Tables).Description);
        Assert.Equal(actual.BaselineId, result.BaselineId);
        Assert.NotEqual(before.BaselineId, result.BaselineId);
        Assert.False(result.HasPendingChanges);
        Assert.Contains("当前 PBIP 文件已保存", result.Warnings);
        Assert.Equal(2, fixture.Revision);
        fixture.AssertReceipt("model.edit", true);
    }

    /// <summary>保存和放弃都处理真实未提交草稿; 保存写入文件, 放弃重新读取未修改的权威文件.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelSaveOrDiscardResolvesRealDraft(bool discard)
    {
        await using var fixture = await ModelFixture.CreateAsync();
        var before = await fixture.InspectAsync();
        await fixture.Edits.StageAsync(new("table.set", "销售", "description", "待保存说明"), default);
        Assert.True(fixture.Edits.HasPendingChanges);
        Assert.Equal(before.BaselineId, (await fixture.Reader.ReadAsync(fixture.Entry, default)).BaselineId);
        var method = discard ? "model.discard" : "model.save";
        var result = Assert.IsType<ProjectSnapshot>(await fixture.CallAsync(method, Json(new { })));
        var actual = await fixture.Reader.ReadAsync(fixture.Entry, default);
        Assert.Equal(discard ? "原说明" : "待保存说明", Assert.Single(actual.Model.Tables).Description);
        Assert.Equal(actual.BaselineId, result.BaselineId);
        Assert.False(fixture.Edits.HasPendingChanges);
        Assert.False(result.HasPendingChanges);
        if (discard) Assert.Equal(before.BaselineId, actual.BaselineId);
        else Assert.NotEqual(before.BaselineId, actual.BaselineId);
        fixture.AssertReceipt(method, !discard);
    }

    /// <summary>当前目标配置通过真实编译器和文件事务保存, 再从磁盘读取新增度量及更新代际.</summary>
    [Fact]
    public async Task ConfigurationApplyPersistsMeasureAndCompletedReceipt()
    {
        await using var fixture = await ModelFixture.CreateAsync(configuration: true);
        var context = Assert.IsType<ModelConfigurationContext>(await fixture.CallAsync("configuration.context", Json(new { target = "current" })));
        var result = Assert.IsType<ModelConfigurationContext>(await fixture.CallAsync("configuration.apply", ConfigurationRequest(context)));
        Assert.False(result.NoChanges);
        Assert.False(result.Project.HasPendingChanges);
        var actual = await fixture.Reader.ReadAsync(fixture.Entry, default);
        Assert.Equal(result.Project.BaselineId, actual.BaselineId);
        Assert.Contains(actual.Model.Measures, measure => measure.Name == "Mcp Fixture Value" && measure.Expression.Contains("value", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Revision);
        fixture.AssertReceipt("configuration.apply", true);
    }

    /// <summary>首次配置保存前故障留下真实草稿, complete 保存该草稿, discard 回读原文件并释放草稿.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationRecoveryResolvesUncommittedConfiguration(bool discard)
    {
        await using var fixture = await ModelFixture.CreateAsync(configuration: true, failFirstSave: true);
        var before = await fixture.InspectAsync();
        var context = Assert.IsType<ModelConfigurationContext>(await fixture.CallAsync("configuration.context", Json(new { target = "current" })));
        var initialReceipts = fixture.Journal.List();
        var connectionReceipt = fixture.AssertReceipt("project.open", true);
        Assert.Equal(connectionReceipt, Assert.Single(initialReceipts));
        var failure = await Assert.ThrowsAsync<AppException>(() => fixture.CallAsync("configuration.apply", ConfigurationRequest(context)));
        Assert.Equal("EXECUTION_FAILED", failure.Code);
        Assert.Equal(1, fixture.FailureWorkspace!.SaveAttempts);
        Assert.Equal(1, fixture.FailureWorkspace.CreatedDrafts);
        Assert.True(fixture.Edits.HasPendingChanges);
        var draft = await fixture.Edits.ReadAsync(before.ProjectPath, default);
        Assert.True(draft.HasPendingChanges);
        Assert.Contains(draft.Model.Measures, measure => measure.Name == "Mcp Fixture Value");
        Assert.DoesNotContain((await fixture.Reader.ReadAsync(fixture.Entry, default)).Model.Measures, measure => measure.Name == "Mcp Fixture Value");
        var failedReceipts = fixture.Journal.List();
        Assert.Equal(initialReceipts.Count + 1, failedReceipts.Count);
        Assert.Equal(connectionReceipt, Assert.Single(failedReceipts, item => item.Record.ExecutionId == connectionReceipt.Record.ExecutionId));
        var failed = Assert.Single(failedReceipts, item => item.Record.ExecutionId != connectionReceipt.Record.ExecutionId);
        Assert.Equal("configuration.apply", failed.Record.Method);
        Assert.Equal("not_committed", failed.Record.State);
        Assert.False(failed.Record.CommitStarted);
        Assert.Equal("EXECUTION_FAILED", failed.Record.ErrorCode);
        var method = discard ? "configuration.discard" : "configuration.complete";
        var result = Assert.IsType<ModelConfigurationContext>(await fixture.CallAsync(method, Json(new { context.TargetId, draft.BaselineId })));
        var actual = await fixture.Reader.ReadAsync(fixture.Entry, default);
        Assert.Equal(result.Project.BaselineId, actual.BaselineId);
        Assert.Equal(!discard, actual.Model.Measures.Any(measure => measure.Name == "Mcp Fixture Value"));
        Assert.False(fixture.Edits.HasPendingChanges);
        Assert.False(result.Project.HasPendingChanges);
        if (discard) Assert.Equal(before.BaselineId, actual.BaselineId);
        else Assert.NotEqual(before.BaselineId, actual.BaselineId);
        var recoveryReceipt = fixture.AssertReceipt(method, !discard);
        var finalReceipts = fixture.Journal.List();
        Assert.Equal(failedReceipts.Count + 1, finalReceipts.Count);
        foreach (var previous in failedReceipts)
            Assert.Equal(previous, Assert.Single(finalReceipts, item => item.Record.ExecutionId == previous.Record.ExecutionId));
        Assert.Equal(recoveryReceipt, Assert.Single(finalReceipts, item => failedReceipts.All(previous => previous.Record.ExecutionId != item.Record.ExecutionId)));
        Assert.Equal(1, fixture.FailureWorkspace!.CreatedDrafts);
        Assert.Equal(discard ? 1 : 2, fixture.FailureWorkspace.SaveAttempts);
    }

    /// <summary>生成严格 Web 字段, 保留实际配置修订而不伪造批准或目标路径.</summary>
    private static JsonElement ConfigurationRequest(ModelConfigurationContext context) => Json(new
    {
        context.TargetId,
        context.Project.BaselineId,
        Section = "measures",
        Request = new ModelConfigurationRequest(Measures: [new("Mcp Fixture Value", "value", "Uncategorized", true, "", "")], MeasureConfigurationRevision: context.MeasureConfigurationRevision)
    });

    /// <summary>使用产品 Web 命名规则序列化工具参数.</summary>
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>拥有本轮全部文件与会话, 外部连接只用既有文件连接替身, 编辑和回读均为真实 PBIP.</summary>
    private sealed class ModelFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-mcp-write-model-" + Guid.NewGuid().ToString("N"));
        private readonly ChangeWorkspace workspace;
        private readonly ModelConfigurationCompiler compiler = new();
        private ModelConfigurationBridge Configuration { get; }
        internal ProjectReader Reader { get; } = new();
        internal string Entry { get; private set; } = "";
        internal ModelEditService Edits { get; }
        internal BridgeSession Session { get; private set; } = null!;
        internal ExecutionJournal Journal { get; }
        internal List<ToolWriteProposal> Proposals { get; } = [];
        internal long Revision { get; private set; }
        internal FirstSaveFailureWorkspace? FailureWorkspace { get; }

        /// <summary>构造独占存储与真实服务; 故障包装只拦截第一次提交前保存, 不替代实际草稿.</summary>
        private ModelFixture(bool failFirstSave)
        {
            workspace = new(Path.Combine(root, "user"));
            Journal = new(Path.Combine(root, "receipts"));
            IModelEditWorkspace source = workspace;
            if (failFirstSave) source = FailureWorkspace = new(workspace);
            Edits = new(Reader, source);
            Configuration = new(Reader, source, compiler);
        }

        /// <summary>建立真实临时工程, 经桥接文件连接选择并由 MCP inspect 观察当前代际.</summary>
        internal static async Task<ModelFixture> CreateAsync(bool configuration = false, bool failFirstSave = false)
        {
            var fixture = new ModelFixture(failFirstSave);
            try
            {
                var project = Path.Combine(fixture.root, "project");
                Directory.CreateDirectory(project);
                if (configuration)
                {
                    var template = await new ScaffoldTemplateCatalog(fixture.root).GetAsync("builtin:blank", default);
                    var composition = await new ScaffoldModelWriter().ComposeAsync(template,
                        new(1, template.Info.Id, template.Info.Baseline, "Project", "zh-cn", null, null, null, null, null), project, project, null, default);
                    fixture.Entry = Path.GetFullPath(Path.Combine(project, composition.EntryFile));
                }
                else fixture.Entry = PbipFixture.Create(project);
                fixture.Session = new(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test", projects: new(fixture.Edits), edits: fixture.Edits,
                    selectProject: _ => Task.FromResult<string?>(fixture.Entry), connections: new TestLocalFileConnection(fixture.Reader).Service(),
                    configuration: fixture.Configuration, mcpJournal: fixture.Journal, toolWrites: new((proposal, token) =>
                    {
                        token.ThrowIfCancellationRequested(); fixture.Proposals.Add(proposal); return Task.FromResult(true);
                    }));
                await fixture.Session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
                await fixture.InspectAsync();
                Assert.Equal(1, fixture.Revision);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        /// <summary>由同一 MCP 会话观察目标及代际, 失败不改变观察值.</summary>
        internal async Task<ProjectSnapshot> InspectAsync() => Assert.IsType<ProjectSnapshot>(await CallAsync("project.inspect", Json(new { })));

        /// <summary>所有被测操作均经过真正的 MCP 白名单、代际与回执分发.</summary>
        internal Task<object?> CallAsync(string method, JsonElement parameters) => Session.InvokeMcpAsync(method, parameters, default, Revision, revision => Revision = revision);

        /// <summary>从新日志读取器核对指定方法的持久终态, 返回执行身份及原文摘要供后续核验原记录未被修改.</summary>
        internal ExecutionView AssertReceipt(string method, bool commitStarted)
        {
            var receipt = Assert.Single(new ExecutionJournal(Path.Combine(root, "receipts")).List(), entry => entry.Record.Method == method);
            Assert.Equal("completed", receipt.Record.State);
            Assert.Equal(commitStarted, receipt.Record.CommitStarted);
            Assert.Null(receipt.Record.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(receipt.Record.RequestDigest));
            return receipt;
        }

        /// <summary>排空本轮会话与文件资源后只删除本轮 GUID 临时根, 不终止任何外部程序.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Session is not null) { Session.Dispose(); await Session.ShutdownAsync(); }
            await Configuration.DisposeAsync();
            await Edits.DisposeAsync();
            compiler.Dispose(); workspace.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>仅模拟一次磁盘提交前故障, 实际配置修改和恢复继续使用同一真实草稿.</summary>
    private sealed class FirstSaveFailureWorkspace(IModelEditWorkspace inner) : IModelEditWorkspace
    {
        internal int CreatedDrafts { get; private set; }
        internal int SaveAttempts { get; private set; }
        /// <summary>委托真实草稿创建并统计次数, 用于证明 complete 没有重新生成配置.</summary>
        public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        { CreatedDrafts++; return new Draft(this, await inner.CreateDraftAsync(baseline, token)); }

        /// <summary>真实 PBIP 草稿包装, 只有第一次 Save 的进入边界被故障替代.</summary>
        private sealed class Draft(FirstSaveFailureWorkspace owner, IModelDraft draft) : IModelDraft
        {
            public ProjectSnapshot Snapshot => draft.Snapshot;
            /// <summary>实际执行模型编辑, 参数与取消原样传播.</summary>
            public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => draft.ApplyAsync(request, token);
            /// <summary>第一次提交前抛出可恢复 I/O 错误, 后续保存使用原始文件事务.</summary>
            public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token)
            {
                if (++owner.SaveAttempts == 1) throw new IOException("isolated precommit failure");
                return draft.SaveAsync(committing, token);
            }
            /// <summary>释放真实草稿资源, 不保留测试外的文件.</summary>
            public void Dispose() => draft.Dispose();
        }
    }
}
