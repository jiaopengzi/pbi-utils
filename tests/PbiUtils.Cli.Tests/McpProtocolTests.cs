// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpProtocolTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 stdio 子进程验证 MCP 发现, 工具调用, 写入开关与 EOF 排空.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Execution;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ModelContextProtocol.Client;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>使用真实协议帧而非产品内部调用, 验证第三方客户端观察到的行为.</summary>
[Collection("CLI User Data")]
public sealed class McpProtocolTests
{
    /// <summary>无效 JSON 开关在真正 stdio 入口报错, stdout 仍不混入普通 CLI 文档.</summary>
    [Fact]
    public async Task InvalidJsonOptionDoesNotPolluteProtocolOutput()
    {
        var start = new ProcessStartInfo(CliTests.Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "mcp", "serve", "--json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("INVALID_ARGUMENT", await errors);
    }

    /// <summary>实际stdio客户端共用一个后台但会话与权限独立, 任一EOF不会结束另一客户端.</summary>
    [Fact]
    public async Task ClientsShareHostWithIndependentContextsAndPermissions()
    {
        await using var first = new McpWireClient();
        await first.DiscoverAsync();
        await using var second = new McpWireClient("--allow-write");
        await second.DiscoverAsync();
        var firstSessions = await first.RequestAsync("tools/call", new { name = "desktop_sessions", arguments = new { } });
        var secondSessions = await second.RequestAsync("tools/call", new { name = "desktop_sessions", arguments = new { } });
        var a = firstSessions.GetProperty("result").GetProperty("structuredContent").GetProperty("sessions")[0];
        var b = secondSessions.GetProperty("result").GetProperty("structuredContent").GetProperty("sessions")[0];
        Assert.Equal(a.GetProperty("processId").GetInt32(), b.GetProperty("processId").GetInt32());
        Assert.NotEqual(a.GetProperty("id").GetString(), b.GetProperty("id").GetString());
        var firstInfo = await first.RequestAsync("tools/call", new { name = "server_info", arguments = new { } });
        var secondInfo = await second.RequestAsync("tools/call", new { name = "server_info", arguments = new { } });
        Assert.False(firstInfo.GetProperty("result").GetProperty("structuredContent").GetProperty("allowWrite").GetBoolean());
        Assert.True(secondInfo.GetProperty("result").GetProperty("structuredContent").GetProperty("allowWrite").GetBoolean());
        Assert.Equal(0, await first.CloseAsync());
        await second.DiscoverAsync();
        var stillConnected = await second.RequestAsync("tools/call", new { name = "desktop_sessions", arguments = new { } });
        Assert.Equal(b.GetProperty("id").GetString(), stillConnected.GetProperty("result").GetProperty("structuredContent").GetProperty("sessions")[0].GetProperty("id").GetString());
    }

    /// <summary>README 中列出的未启用列表应明确报不支持, 不冒充空资源成功, 后续工具列表仍可用.</summary>
    [Fact]
    public async Task UnsupportedListsReturnMethodNotFoundWithoutBreakingTools()
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        foreach (var method in new[] { "resources/list", "resources/templates/list", "prompts/list", "tasks/list", "roots/list" })
        {
            var result = await client.RequestAsync(method, new { });
            Assert.Equal(-32601, result.GetProperty("error").GetProperty("code").GetInt32());
        }
        var tools = await client.RequestAsync("tools/list", new { });
        Assert.NotEmpty(tools.GetProperty("result").GetProperty("tools").EnumerateArray());
    }

    /// <summary>官方独立客户端分别走默认发现和显式握手, 验证相同业务与进程释放.</summary>
    /// <param name="protocolVersion">null 使用 SDK 默认发现, 指定版本使用其对应官方握手.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task OfficialClientCanDiscoverAndCallTools(string? protocolVersion)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = CliTests.Executable,
            Arguments = ["mcp", "serve"],
            Name = "pbi-utils-integration-test"
        });
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == "configuration_plan");
        Assert.Contains(tools, tool => tool.Name == "desktop_sessions");
        var result = await client.CallToolAsync("server_info", new Dictionary<string, object?>(), cancellationToken: deadline.Token);
        Assert.False(result.IsError ?? false);
        Assert.False(result.StructuredContent!.Value.GetProperty("allowWrite").GetBoolean());
    }

    /// <summary>新版客户端无需握手即可列出核心工具, 调用只读格式化并正常关闭.</summary>
    [Fact]
    public async Task ClientListsToolsFormatsAndClosesOnEofWithoutDiscovery()
    {
        await using var client = new McpWireClient();
        var listed = await client.RequestAsync("tools/list", new { });
        AssertCompleteResult(listed);
        Assert.Equal(0, listed.GetProperty("result").GetProperty("ttlMs").GetInt64());
        Assert.Equal("private", listed.GetProperty("result").GetProperty("cacheScope").GetString());
        var names = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        foreach (var name in new[] { "project_inspect", "format_code", "scaffold_templates", "scaffold_plan", "scaffold_create", "calendar_preview", "configuration_plan", "configuration_apply", "sample_generate", "udf_catalog_search" }) Assert.Contains(name, names);
        Assert.DoesNotContain("executions", names);
        Assert.DoesNotContain("resolve", names);
        var formatted = await client.RequestAsync("tools/call", new { name = "format_code", arguments = new { language = "dax", text = "SUM(Sales[Amount])" } });
        AssertCompleteResult(formatted);
        Assert.False(formatted.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("SUM ( Sales[Amount] )", formatted.GetProperty("result").GetProperty("structuredContent").GetProperty("text").GetString());
        Assert.Equal(0, await client.CloseAsync());
    }

    /// <summary>新版客户端无需会话握手, 可按每请求协议元数据发现服务并调用工具.</summary>
    [Fact]
    public async Task ModernClientDiscoversWithoutInitialize()
    {
        await using var client = new McpWireClient();
        var discovered = await client.RequestAsync("server/discover", new { });
        AssertCompleteResult(discovered);
        Assert.Equal(["2026-07-28"], discovered.GetProperty("result").GetProperty("supportedVersions").EnumerateArray().Select(version => version.GetString()));
        var capabilities = discovered.GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.False(capabilities.TryGetProperty("resources", out _));
        Assert.False(capabilities.TryGetProperty("prompts", out _));
        var result = await client.RequestAsync("tools/call", new { name = "server_info", arguments = new { } });
        AssertCompleteResult(result);
        Assert.False(result.GetProperty("result").GetProperty("structuredContent").GetProperty("allowWrite").GetBoolean());
        Assert.Equal(0, await client.CloseAsync());
    }

    /// <summary>真实 Codex 使用的 initialize 入口由 SDK 协商, 后续无逐请求元数据的调用仍受相同写权限约束.</summary>
    [Fact]
    public async Task InitializeNegotiatesAndCallsTools()
    {
        await using var client = new McpWireClient();
        var response = await client.RequestAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "codex-wire-test", version = "1" } }, includeMetadata: false);
        Assert.True(response.TryGetProperty("result", out var initialized), response.ToString());
        Assert.Equal("2025-11-25", initialized.GetProperty("protocolVersion").GetString());
        Assert.Equal("pbi-utils", initialized.GetProperty("serverInfo").GetProperty("name").GetString());
        await client.NotifyAsync("notifications/initialized", new { });
        var listed = await client.RequestAsync("tools/list", new { }, includeMetadata: false);
        Assert.Contains(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "format_code");
        var formatted = await client.RequestAsync("tools/call", new { name = "format_code", arguments = new { language = "dax", text = "SUM(Sales[Amount])" } }, includeMetadata: false);
        Assert.False(formatted.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("SUM ( Sales[Amount] )", formatted.GetProperty("result").GetProperty("structuredContent").GetProperty("text").GetString());
        var denied = await client.RequestAsync("tools/call", new { name = "desktop_model_edit", arguments = new { parameters = new { } } }, includeMetadata: false);
        Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("MCP_WRITE_DISABLED", denied.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, await client.CloseAsync());
    }

    /// <summary>版本按每次请求检查, 已发现服务也不能借旧版本或未知版本执行工具.</summary>
    [Theory]
    [InlineData("2099-01-01")]
    public async Task UnsupportedVersionIsRejectedPerRequest(string protocolVersion)
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        var meta = new Dictionary<string, object> { ["io.modelcontextprotocol/protocolVersion"] = protocolVersion, ["io.modelcontextprotocol/clientCapabilities"] = new { } };
        var response = await client.RequestAsync("tools/call", new { _meta = meta, name = "server_info", arguments = new { } });
        var error = response.GetProperty("error");
        Assert.Equal(-32022, error.GetProperty("code").GetInt32());
        Assert.Equal(protocolVersion, error.GetProperty("data").GetProperty("requested").GetString());
        Assert.Equal(["2026-07-28"], error.GetProperty("data").GetProperty("supported").EnumerateArray().Select(version => version.GetString()));
        var valid = await client.RequestAsync("tools/call", new { name = "server_info", arguments = new { } });
        AssertCompleteResult(valid);
    }

    /// <summary>握手版本不能冒充逐请求协议, 拒绝后新版发现和调用仍正常.</summary>
    [Fact]
    public async Task HandshakeVersionCannotBeUsedAsPerRequestMetadata()
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        var meta = new Dictionary<string, object> { ["io.modelcontextprotocol/protocolVersion"] = "2025-11-25", ["io.modelcontextprotocol/clientCapabilities"] = new { } };
        var rejected = await client.RequestAsync("tools/list", new { _meta = meta });
        Assert.Equal(-32600, rejected.GetProperty("error").GetProperty("code").GetInt32());
        await client.DiscoverAsync();
    }

    /// <summary>没有逐请求协议元数据时明确拒绝, 不能复用先前发现请求的版本.</summary>
    [Fact]
    public async Task MissingMetadataIsRejectedAfterDiscovery()
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        var response = await client.RequestAsync("tools/list", new { }, includeMetadata: false);
        Assert.True(response.TryGetProperty("error", out _), response.ToString());
        await client.DiscoverAsync();
    }

    /// <summary>普通完成结果必须携带新版判别字段和官方 SDK 添加的服务身份; 缺字段由断言报告.</summary>
    private static void AssertCompleteResult(JsonElement response)
    {
        Assert.True(response.TryGetProperty("result", out var result), response.ToString());
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal("pbi-utils", result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString());
    }

    /// <summary>工具参数不能开启写权限, 默认模式在任何业务副作用前拒绝写工具.</summary>
    [Fact]
    public async Task DefaultModeRejectsWritesAndArgumentPrivilegeEscalation()
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        var write = await client.RequestAsync("tools/call", new { name = "configuration_apply", arguments = new { planId = "unknown", approve = "unknown" } });
        Assert.True(write.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("MCP_WRITE_DISABLED", write.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        foreach (var name in new[] { "desktop_model_edit", "desktop_project_connectLocal", "desktop_configuration_apply" })
        {
            var denied = await client.RequestAsync("tools/call", new { name, arguments = new { parameters = new { } } });
            Assert.Equal("MCP_WRITE_DISABLED", denied.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
        var forged = await client.RequestAsync("tools/call", new { name = "server_info", arguments = new { allowWrite = true } });
        Assert.True(forged.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("INVALID_ARGUMENT", forged.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>未知工具返回协议参数错误, 后续正常请求仍可继续; 无法借工具名执行任意代码.</summary>
    [Fact]
    public async Task UnknownToolAndInvalidFieldsDoNotBreakSession()
    {
        await using var client = new McpWireClient();
        await client.DiscoverAsync();
        var unknown = await client.RequestAsync("tools/call", new { name = "shell", arguments = new { command = "not-executed" } });
        Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
        var missing = await client.RequestAsync("tools/call", new { name = "format_code", arguments = new { language = "dax" } });
        Assert.True(missing.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("INVALID_ARGUMENT", missing.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        var removedPing = await client.RequestAsync("ping", new { });
        Assert.Equal(-32601, removedPing.GetProperty("error").GetProperty("code").GetInt32());
        await client.DiscoverAsync();
    }

    /// <summary>取消在途样例生成后等待业务门闩排空, 不产生发布目录且同一进程继续可用.</summary>
    [Fact]
    public async Task CancellationStopsPreparationWithoutPublishingFiles()
    {
        var output = Path.Combine(Path.GetTempPath(), "pbi-mcp-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = new McpWireClient("--allow-write");
            await client.DiscoverAsync();
            var requestId = await client.SendRequestAsync("tools/call", new { name = "sample_generate", arguments = new { output, options = new { locale = "zh-cn", productCount = 10000, storeCount = 10000, inventoryCycle = 22, startDate = "2024-01-01", endDate = "2025-12-31" } } });
            await client.NotifyAsync("notifications/cancelled", new { requestId, reason = "test cancellation" });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (true)
            {
                var status = await client.RequestAsync("tools/call", new { name = "server_info", arguments = new { } });
                if (!status.GetProperty("result").GetProperty("isError").GetBoolean()) break;
                Assert.True(DateTimeOffset.UtcNow < deadline, "取消后业务未在期限内排空");
                await Task.Delay(25);
            }
            Assert.False(Directory.Exists(output));
            Assert.Equal(0, await client.CloseAsync());
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
}

/// <summary>集合内串行并持有跨进程测试租约, 避免独立验收脚本争用真实用户级回执.</summary>
[CollectionDefinition("CLI User Data", DisableParallelization = true)]
public sealed class CliUserDataCollection : ICollectionFixture<CliUserDataLeaseFixture>;

/// <summary>覆盖整个集合及各用例清理; 不在子产品进程再次加锁, 防止父子等待死锁.</summary>
public sealed class CliUserDataLeaseFixture : IDisposable
{
    private readonly PbiUtils.Testing.AcceptanceUserDataLease lease = new();

    /// <summary>在全部协议进程退出并完成测试回执清理后释放协调句柄.</summary>
    public void Dispose() => lease.Dispose();
}

/// <summary>仅管理本测试启动的进程, 所有读取有界, 失败时终止并回收该进程.</summary>
internal sealed class McpWireClient : IAsyncDisposable
{
    private static readonly string[] ServeArguments = ["mcp", "serve"];
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly HashSet<string>? initialReceipts;
    private readonly HashSet<string> requestDigests = new(StringComparer.Ordinal);
    private readonly HashSet<int> pendingRequests = [];
    private int sequence;

    /// <summary>使用独立参数数组启动产品, 不使用 shell, 不继承额外写权限.</summary>
    internal McpWireClient(params string[] options)
    {
        if (options.Contains("--allow-write", StringComparer.Ordinal))
            initialReceipts = new ExecutionJournal(RuntimeEnvironment.UserDataDirectory()).List().Select(view => view.Record.ExecutionId).ToHashSet(StringComparer.Ordinal);
        var start = new ProcessStartInfo(CliTests.Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var arg in ServeArguments.Concat(options)) start.ArgumentList.Add(arg);
        process = Process.Start(start)!;
        errors = process.StandardError.ReadToEndAsync();
    }

    /// <summary>发现当前服务版本供独立工具场景复用, 不创建协议会话; 失败通过断言报告.</summary>
    internal async Task DiscoverAsync()
    {
        var response = await RequestAsync("server/discover", new { });
        Assert.True(response.TryGetProperty("result", out _), response.ToString());
    }

    /// <summary>发送一帧并等待相同请求 ID 的完整 JSON 响应; 通知不会被误认为结果.</summary>
    /// <param name="method">官方协议方法.</param>
    /// <param name="parameters">请求参数, 可显式提供 _meta 验证协议错误.</param>
    /// <param name="includeMetadata">默认附加新版元数据, false 仅用于缺失元数据或旧握手负向测试.</param>
    /// <returns>完整响应; EOF, 无效 JSON 或超时使测试失败.</returns>
    internal async Task<JsonElement> RequestAsync(string method, object parameters, bool includeMetadata = true)
    {
        var id = await SendRequestAsync(method, parameters, includeMetadata);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            using var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number)
            {
                pendingRequests.Remove(responseId.GetInt32());
                if (responseId.GetInt32() == id) return parsed.RootElement.Clone();
            }
        }
    }

    /// <summary>发送有身份的请求而不等待终态, 用于真实取消与并发时序验证.</summary>
    /// <param name="method">协议方法.</param>
    /// <param name="parameters">业务参数及可选的显式协议元数据.</param>
    /// <param name="includeMetadata">是否在参数未提供 _meta 时添加新版元数据.</param>
    /// <returns>请求 ID; 序列化或管道写入失败向调用方传播.</returns>
    internal async Task<int> SendRequestAsync(string method, object parameters, bool includeMetadata = true)
    {
        var id = ++sequence;
        if (initialReceipts is not null && method == "tools/call")
        {
            var input = JsonSerializer.SerializeToElement(parameters, JsonSerializerOptions.Web);
            var tool = input.GetProperty("name").GetString();
            var arguments = input.GetProperty("arguments");
            requestDigests.Add(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + arguments.GetRawText()))));
        }
        var requestParameters = JsonSerializer.SerializeToNode(parameters, JsonSerializerOptions.Web)!.AsObject();
        if (includeMetadata && !requestParameters.ContainsKey("_meta"))
            requestParameters["_meta"] = JsonSerializer.SerializeToNode(new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/clientInfo"] = new { name = "pbi-utils-test", version = "1" }
            });
        pendingRequests.Add(id);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = requestParameters }));
        await process.StandardInput.FlushAsync();
        return id;
    }

    /// <summary>通知不等待回复, 只刷新标准输入.</summary>
    internal async Task NotifyAsync(string method, object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
        await process.StandardInput.FlushAsync();
    }

    /// <summary>EOF 为唯一正常关闭信号, 验证进程及时排空退出.</summary>
    internal async Task<int> CloseAsync()
    {
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return process.ExitCode;
    }

    /// <summary>有界等待 stderr EOF, 单个 CLI 退出后不应由仍运行的共享后台继续持有其写端.</summary>
    /// <param name="timeout">本次流关闭检查的最长时间.</param>
    /// <returns>完整 stderr; 超时或流错误向测试传播.</returns>
    internal Task<string> ReadErrorsAsync(TimeSpan timeout) => errors.WaitAsync(timeout);

    /// <summary>全部请求已经收到终态后, 有界读取退出客户端的剩余 stdout, 检测共享后台继承协议流.</summary>
    /// <param name="timeout">本次流关闭检查的最长时间.</param>
    /// <returns>剩余 stdout; 超时或流错误向测试传播, 不与请求读取同时调用.</returns>
    internal Task<string> ReadRemainingOutputAsync(TimeSpan timeout) => process.StandardOutput.ReadToEndAsync().WaitAsync(timeout);

    /// <summary>有界排空本测试客户端; 超时只可结束没有在途请求的本 CLI, 不结束共享 Host 或进程树.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                try { await CloseAsync(); }
                catch
                {
                    if (!process.HasExited && pendingRequests.Count == 0)
                    {
                        process.Kill();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    throw;
                }
            }
            await ReadErrorsAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            process.Dispose();
            CleanupOwnedReceipts();
        }
    }

    /// <summary>仅删除本测试进程生命周期内新增且请求摘要完全匹配的确定终态回执; 未知提交始终保留.</summary>
    private void CleanupOwnedReceipts()
    {
        if (initialReceipts is null) return;
        var userRoot = RuntimeEnvironment.UserDataDirectory();
        var candidates = new ExecutionJournal(userRoot).List().Where(view => !initialReceipts.Contains(view.Record.ExecutionId)
            && requestDigests.Contains(view.Record.RequestDigest) && view.Record.State is "completed" or "not_committed").ToArray();
        if (candidates.Length == 0) return;
        var directory = Path.Combine(userRoot, "mcp", "executions");
        using var lease = UserDataStore.Lock(Path.Combine(directory, ".lock"));
        foreach (var view in candidates)
        {
            var path = Path.Combine(directory, view.Record.ExecutionId + ".json");
            UserDataStore.Check(path);
            if (!File.Exists(path)) continue;
            var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (digest == view.RecordDigest) File.Delete(path);
        }
    }
}
