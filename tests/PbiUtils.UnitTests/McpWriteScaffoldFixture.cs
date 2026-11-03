// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteScaffoldFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为 MCP 文件写入正向测试组合真实宿主、事务服务与隔离存储, 仅替代系统交互和 HTTP 传输.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Application.SampleData;
using PbiUtils.Infrastructure.Udf.Sources;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Execution;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>每实例拥有随机临时目录; 自动回答仅限当前测试明确声明的方法和精确参数.</summary>
internal sealed class McpWriteScaffoldFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pbi-mcp-scaffold-write-" + Guid.NewGuid().ToString("N"));
    internal string UserRoot => Path.Combine(Root, "user");
    internal string Output => Path.Combine(Root, "output");
    internal string SampleOutput => Path.Combine(Root, "sample");
    internal ScaffoldTemplateCatalog Catalog { get; }
    internal LocalUdfStore Local { get; }
    internal UdfSourceProvider Sources { get; }
    internal SampleDataService Samples { get; } = new(new SampleDataAdapter());
    internal BridgeSession Session { get; }
    internal ExecutionJournal Journal { get; }
    internal List<string> OpenedTemplates { get; } = [];
    internal List<(string Path, string Identity)> OpenedDirectories { get; } = [];
    internal List<(string Path, string Identity)> OpenedDesktop { get; } = [];
    internal List<string> OpenedSamples { get; } = [];
    internal List<string> SampleRiskConfirmations { get; } = [];
    internal List<JsonElement> Messages { get; } = [];
    private readonly UiDialogBroker dialogs;
    private readonly ScaffoldService service;
    /// <summary>组合真实服务; 可选客户端只替换网络字节来源, 不替换目录解析或发布结果.</summary>
    internal McpWriteScaffoldFixture(Func<UdfSourceEntry, HttpClient>? clientFactory = null)
    {
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(SampleOutput);
        Catalog = new(UserRoot);
        Local = new(UserRoot);
        Sources = new(UserRoot, clientFactory);
        Journal = new(UserRoot);
        service = new(new ScaffoldWorkspace(UserRoot, Catalog, new ScaffoldModelWriter()));
        dialogs = new(HandleDialog);
        var bridge = new ScaffoldBridge(Catalog, service, UserRoot, _ => Task.FromResult<string?>(Output),
            openTemplates: (path, _) => { OpenedTemplates.Add(path); return Task.CompletedTask; }, packages: Sources.DefaultProvider, local: Local, sources: Sources,
            openDirectory: (path, identity, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(identity, ProjectFileIdentity.Read(path));
                OpenedDirectories.Add((path, identity));
                return Task.CompletedTask;
            },
            openDesktop: async (path, identity, token) =>
            {
                Assert.Equal(identity, ProjectFileIdentity.Read(path));
                OpenedDesktop.Add((path, identity));
                // 系统 Desktop 启动边界由离线权威文件读取替代, 不宣称引擎连接或计算已执行.
                return await new ProjectReader().ReadAsync(path, token);
            });
        Session = new(new(false, null), new(new RuntimeEnvironment()), message => Messages.Add(JsonSerializer.Deserialize<JsonElement>(message)), "test",
            scaffold: bridge, dialogs: dialogs, mcpJournal: Journal, projects: new(new ProjectReader()),
            recentProjects: new(UserRoot), samples: Samples, sampleDirectory: Path.Combine(Root, "not-selected"),
            chooseSampleDirectory: _ => Task.FromResult<string?>(SampleOutput), openSampleDirectory: (path, _) => { OpenedSamples.Add(path); return Task.CompletedTask; },
            confirmSample: ConfirmSampleAsync);
    }

    /// <summary>调用真实静默 MCP 入口, 核对调用前状态和精确参数的可靠回执; 业务失败直接传播.</summary>
    internal async Task<JsonElement> WriteAsync(string method, object parameters, Action<JsonElement>? beforeWrite = null, bool? commitStarted = null)
    {
        var input = Json(parameters);
        var previousIds = Journal.List().Select(item => item.Record.ExecutionId).ToHashSet(StringComparer.Ordinal);
        beforeWrite?.Invoke(input);
        var result = await Session.InvokeMcpAsync(method, input, default);
        Assert.Equal(0, Session.PendingCount);
        var receipts = Journal.List();
        Assert.Equal(previousIds.Count + 1, receipts.Count);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + input.GetRawText())));
        var receipt = Assert.Single(receipts, item => !previousIds.Contains(item.Record.ExecutionId)).Record;
        Assert.Equal(digest, receipt.RequestDigest); Assert.Equal(method, receipt.Method);
        Assert.Equal("completed", receipt.State); Assert.Null(receipt.ErrorCode);
        if (commitStarted is { } committed) Assert.Equal(committed, receipt.CommitStarted);
        return Json(result!);
    }

    /// <summary>MCP 操作不产生应用弹窗; 一旦产生直接使测试失败, 不自动回复.</summary>
    private static void HandleDialog(string message)
    {
        var envelope = JsonSerializer.Deserialize<JsonElement>(message);
        Assert.False(envelope.TryGetProperty("uiDialog", out _), "Silent MCP must not display application dialogs");
    }

    /// <summary>记录业务误入 GUI 风险确认, MCP 测试断言该列表始终为空.</summary>
    private Task<bool> ConfirmSampleAsync(string action, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Assert.Equal(SampleOutput, directory);
        SampleRiskConfirmations.Add(action);
        return Task.FromResult(false);
    }

    /// <summary>固定使用正式 Web 字段命名, 不放宽产品字段白名单.</summary>
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>排空宿主后只删除测试独占随机目录, 不操作用户工程或模型.</summary>
    public async ValueTask DisposeAsync()
    {
        await Session.ShutdownAsync();
        Session.Dispose();
        dialogs.Dispose();
        service.Dispose();
        Sources.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
