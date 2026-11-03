// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证表导出、独立统计、精确计数和目录能力的身份及取消发布边界, 不访问真实模型.

using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>导出桥接拒绝网页路径与查询, 未连接时不得打开文件选择器.</summary>
public sealed partial class ModelTableExportBridgeTests
{
    /// <summary>表统计只接受已绑定身份, 不打开保存窗口或改变模型定义.</summary>
    [Fact]
    public async Task StatisticsReturnsBoundMetadataWithoutFileSelection()
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.tableStatistics", new { projectId = "project", baselineId = "baseline" }, "statistics");
        var result = fixture.Response("statistics").GetProperty("result");
        Assert.Equal("project", result.GetProperty("projectId").GetString());
        Assert.Equal(2, result.GetProperty("tables")[0].GetProperty("rowCount").GetInt64());
        Assert.Equal(0, fixture.SelectionCount);
        Assert.Equal("baseline", fixture.Snapshot.BaselineId);
    }

    /// <summary>统计与导出共用模型门闩; 在途取消排空后导出才重新可用.</summary>
    [Fact]
    public async Task StatisticsCancellationDrainsBeforeExportGateIsReleased()
    {
        using var fixture = new Fixture { BlockStatistics = true };
        await fixture.ConnectAsync();
        var statistics = fixture.Call("model.tableStatistics", new { projectId = "project", baselineId = "baseline" }, "statistics");
        await fixture.StatisticsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Call("model.exportTables", Fixture.Parameters, "blocked-export");
        Assert.Equal("BUSY", fixture.Response("blocked-export").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.SelectionCount);
        await fixture.Call("request.cancel", new { requestId = "statistics" }, "cancel-statistics");
        Assert.True(fixture.Response("cancel-statistics").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.True(fixture.StatisticsToken.IsCancellationRequested);
        Assert.False(statistics.IsCompleted);
        Assert.False(fixture.StatisticsDrained);
        Assert.DoesNotContain(fixture.Messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "statistics");
        await fixture.Call("model.exportTables", Fixture.Parameters, "still-blocked-export");
        Assert.Equal("BUSY", fixture.Response("still-blocked-export").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.SelectionCount);
        fixture.StatisticsRelease.TrySetResult();
        await statistics.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("CANCELLED", fixture.Response("statistics").GetProperty("error").GetProperty("code").GetString());
        Assert.True(fixture.StatisticsDrained);
        Assert.Equal(0, fixture.Session.PendingCount);
        await fixture.Call("model.exportTables", Fixture.Parameters, "export-after-statistics");
        Assert.True(fixture.Response("export-after-statistics").TryGetProperty("result", out _));
    }

    /// <summary>导航撤销和关闭取消旧文档统计, 仍等待同步读取退出才释放模型锁及会话资源.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationAndShutdownDrainStatisticsBeforeReleasingOwnership(bool shutdown)
    {
        using var fixture = new Fixture { BlockStatistics = true };
        await fixture.ConnectAsync();
        var statistics = fixture.Call("model.tableStatistics", new { projectId = "project", baselineId = "baseline" }, "statistics");
        await fixture.StatisticsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task? closing = null;
        if (shutdown)
        {
            closing = fixture.Session.ShutdownAsync();
            Assert.False(closing.IsCompleted);
            Assert.Equal("Draining", fixture.Session.Lifecycle);
        }
        else fixture.Session.InvalidateDocument();
        Assert.True(fixture.StatisticsToken.IsCancellationRequested);
        Assert.False(statistics.IsCompleted);
        Assert.False(fixture.StatisticsDrained);
        Assert.Equal(1, fixture.Session.PendingCount);
        Assert.DoesNotContain(fixture.Messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "statistics");
        if (!shutdown)
        {
            await fixture.Call("model.exportTables", Fixture.Parameters, "blocked-export");
            Assert.Equal("BUSY", fixture.Response("blocked-export").GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, fixture.SelectionCount);
        }
        fixture.StatisticsRelease.TrySetResult();
        await statistics.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.StatisticsDrained);
        Assert.Equal(0, fixture.Session.PendingCount);
        if (closing is not null)
        {
            await closing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Disposed", fixture.Session.Lifecycle);
            Assert.DoesNotContain(fixture.Messages, message => message.TryGetProperty("id", out var id) && id.GetString() == "statistics");
        }
        else
        {
            Assert.Equal("CANCELLED", fixture.Response("statistics").GetProperty("error").GetProperty("code").GetString());
            await fixture.Call("model.exportTables", Fixture.Parameters, "export-after-statistics");
            Assert.True(fixture.Response("export-after-statistics").TryGetProperty("result", out _));
        }
    }
    /// <summary>完整身份导出只打开一次原生目标选择, 并返回实际文件摘要与两个阶段.</summary>
    [Fact]
    public async Task ExportUsesBoundTargetAndReportsPublication()
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "export");
        var response = fixture.Response("export");
        Assert.Equal("Sales.xlsx", response.GetProperty("result").GetProperty("fileName").GetString());
        Assert.Equal(0, response.GetProperty("result").GetProperty("rowCount").GetInt64());
        Assert.Equal(1, fixture.SelectionCount);
        Assert.Equal(["Sales"], fixture.SelectedRequest!.Tables);
        Assert.True(fixture.Committed);
        string[] phases = [.. fixture.Messages.Where(m => m.TryGetProperty("requestPhase", out _))
            .Select(m => m.GetProperty("requestPhase").GetProperty("phase").GetString()!)];
        Assert.Equal(["exporting", "committing"], phases);
    }

    /// <summary>过期基线或不支持的来源不能触发保存选择器, 同一会话可继续正常操作.</summary>
    [Theory]
    [InlineData("stale", false, "STALE_BASELINE")]
    [InlineData("baseline", true, "EXPORT_SOURCE_UNSUPPORTED")]
    public async Task InvalidTargetDoesNotOpenSelector(string baseline, bool limited, string expected)
    {
        using var fixture = new Fixture();
        fixture.Snapshot = fixture.Snapshot with { BaselineId = baseline, Connection = new("desktop", "Desktop", limited) };
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "export");
        Assert.Equal(expected, fixture.Response("export").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.SelectionCount);
        Assert.False(fixture.Committed);
    }

    /// <summary>用户取消原生选择时不创建临时文件或数据查询.</summary>
    [Fact]
    public async Task CancelledSelectionDoesNotReadData()
    {
        using var fixture = new Fixture { CancelSelection = true };
        await fixture.ConnectAsync();
        await fixture.Call("model.exportTables", Fixture.Parameters, "export");
        Assert.Equal("CANCELLED", fixture.Response("export").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.CreatedFiles);
        Assert.False(fixture.Committed);
    }

    /// <summary>同步读取尚未返回时取消只记入请求, 锁和唯一终态等待底层排空.</summary>
    [Fact]
    public async Task CancellationDrainsReaderAndKeepsModelLocked()
    {
        using var fixture = new Fixture { BlockRead = true };
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "export");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("request.cancel", new { requestId = "export" }, "cancel");
        Assert.True(fixture.Response("cancel").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.False(export.IsCompleted);
        await fixture.Call("project.disconnect", new { }, "disconnect");
        Assert.Equal("BUSY", fixture.Response("disconnect").GetProperty("error").GetProperty("code").GetString());
        fixture.ReadRelease.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("CANCELLED", fixture.Response("export").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.Committed);
        Assert.Equal(0, fixture.Session.PendingCount);
        Assert.True(fixture.FileDisposed);
    }

    /// <summary>文件发布边界之后取消不被受理, 原请求等待文件发布完成.</summary>
    [Fact]
    public async Task CancellationAfterPublicationBoundaryIsRejected()
    {
        using var fixture = new Fixture { BlockCommit = true };
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "export");
        await fixture.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Call("request.cancel", new { requestId = "export" }, "cancel");
        Assert.False(fixture.Response("cancel").GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.False(export.IsCompleted);
        fixture.CommitRelease.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Response("export").TryGetProperty("result", out _));
        Assert.True(fixture.Committed);
    }

    /// <summary>导出阶段有独立十分钟预算, 过期选择阶段不能误取消, 超时仍等待同步读取退出.</summary>
    [Fact]
    public async Task ExportTimeoutKeepsOriginalRequestUntilReaderDrains()
    {
        var clock = new ExportClock();
        using var fixture = new Fixture(clock) { BlockRead = true };
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "export");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Timers.Last(timer => timer.Due == TimeSpan.FromSeconds(180)).Fire();
        Assert.False(fixture.ReadToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(599));
        Assert.False(fixture.ReadToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fixture.ReadToken.IsCancellationRequested);
        Assert.False(export.IsCompleted);
        fixture.ReadRelease.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("TIMEOUT", fixture.Response("export").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.Committed);
    }

    /// <summary>导航撤销和关闭都取消未发布导出, 关闭任务在数据读取排空前不能结束.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationAndShutdownDrainUnpublishedExport(bool shutdown)
    {
        using var fixture = new Fixture { BlockRead = true };
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "export");
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task? closing = null;
        if (shutdown)
        {
            closing = fixture.Session.ShutdownAsync();
            Assert.False(closing.IsCompleted);
            Assert.Equal("Draining", fixture.Session.Lifecycle);
        }
        else fixture.Session.InvalidateDocument();
        Assert.True(fixture.ReadToken.IsCancellationRequested);
        Assert.False(export.IsCompleted);
        fixture.ReadRelease.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        if (closing is not null)
        {
            await closing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Disposed", fixture.Session.Lifecycle);
        }
        else Assert.Equal("CANCELLED", fixture.Response("export").GetProperty("error").GetProperty("code").GetString());
        Assert.False(fixture.Committed);
        Assert.True(fixture.FileDisposed);
        Assert.Equal(0, fixture.Session.PendingCount);
    }

    /// <summary>查询失败或取消叠加临时文件清理失败时, 协议保留首因并追加安全清理事实.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureDoesNotReplaceFailureOrCancellation(bool cancel)
    {
        using var fixture = new Fixture { BlockRead = cancel, FailWrite = !cancel, FailFileDispose = true };
        await fixture.ConnectAsync();
        var export = fixture.Call("model.exportTables", Fixture.Parameters, "export");
        if (cancel)
        {
            await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Call("request.cancel", new { requestId = "export" }, "cancel");
            fixture.ReadRelease.TrySetResult();
        }
        await export.WaitAsync(TimeSpan.FromSeconds(3));
        var error = fixture.Response("export").GetProperty("error");
        Assert.Equal(cancel ? "CANCELLED" : "EXPORT_VALUE_UNSUPPORTED", error.GetProperty("code").GetString());
        Assert.Contains("导出临时文件清理失败", error.GetProperty("message").GetString());
        Assert.False(fixture.Committed);
    }

    private static readonly string[] SalesTables = ["Sales"];

    /// <summary>合法导出参数仍要求宿主已经绑定当前模型.</summary>
    [Fact]
    public async Task ExportRequiresCurrentModel()
    {
        var response = await CallAsync(new { projectId = "project", baselineId = "baseline", tables = SalesTables, format = "xlsx", includeLog = false, locale = "zh-cn", rowLimit = (int?)null });
        Assert.Equal("PROJECT_NOT_SELECTED", response.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>额外目标、查询、未知格式与错误字段类型在任何文件操作前拒绝.</summary>
    [Theory]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Sales\"],\"format\":\"csv\",\"includeLog\":false,\"locale\":\"zh-cn\",\"path\":\"C:/export.csv\"}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Sales\"],\"format\":\"csv\",\"includeLog\":false,\"locale\":\"zh-cn\",\"query\":\"EVALUATE Sales\"}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Sales\"],\"format\":\"exe\",\"includeLog\":false,\"locale\":\"zh-cn\"}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Sales\"],\"format\":\"csv\",\"includeLog\":\"false\",\"locale\":\"zh-cn\"}")]
    [InlineData("{\"projectId\":\"p\",\"baselineId\":\"b\",\"tables\":[\"Sales\"],\"format\":\"csv\",\"includeLog\":true,\"locale\":\"zh-cn\"}")]
    public async Task InvalidParametersAreRejected(string parameters)
    {
        var response = await CallAsync(JsonSerializer.Deserialize<JsonElement>(parameters));
        Assert.Equal("VALIDATION_ERROR", response.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>在独立会话发送单一请求, 返回对应唯一终态并检查排空.</summary>
    /// <param name="parameters">网页业务参数, 不包含宿主资源.</param>
    /// <returns>协议终态; 此夹具没有模型或文件选择器.</returns>
    private static async Task<JsonElement> CallAsync(object parameters)
    {
        ConcurrentQueue<JsonElement> messages = new();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test");
        await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "export", method = "model.exportTables", @params = parameters }));
        Assert.Equal(0, session.PendingCount);
        return Assert.Single(messages);
    }

    /// <summary>模型/行/发布为受控替身, 可选目录记录使用本测试独立临时目录, 不启动原生程序.</summary>
    private sealed class Fixture : IProjectReader, IModelEditWorkspace, IModelTableDataReader, IModelTableFileWriter, IModelTableRows, IDisposable, IAsyncDisposable
    {
        private readonly string? ownedRoot;
        private ModelTableExportRequest? writingRequest;
        internal static object Parameters => new { projectId = "project", baselineId = "baseline", tables = new[] { "Sales" }, format = "xlsx", includeLog = false, locale = "zh-cn", rowLimit = (int?)null };
        internal ProjectSnapshot Snapshot { get; set; } = new("model://export", "project", "baseline", "TOM", "", "", null,
            new("Model", 1606, "en-US", [new("Sales", null, false, [new("Value", "Data", "Int64", false, null)], [])], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        internal ConcurrentQueue<JsonElement> Messages { get; } = new();
        internal BridgeSession Session { get; }
        internal int SelectionCount { get; private set; }
        internal ModelTableExportRequest? SelectedRequest { get; private set; }
        internal bool CancelSelection { get; init; }
        internal bool BlockRead { get; init; }
        internal bool BlockCommit { get; init; }
        internal bool BlockStatistics { get; init; }
        internal bool BlockCounts { get; init; }
        internal bool BlockOpen { get; init; }
        internal string Destination { get; set; } = "C:/bound/Sales.xlsx";
        internal LocalModelExportLocationStore? Locations { get; }
        internal List<int?> ReadLimits { get; } = [];
        internal List<string> OpenedDirectories { get; } = [];
        internal TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OpenRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken OpenToken { get; private set; }
        internal int CountCalls { get; private set; }
        internal string[]? CountTables { get; private set; }
        internal ProjectSnapshot? CountSnapshot { get; private set; }
        internal CancellationToken CountToken { get; private set; }
        internal bool CountsDrained { get; private set; }
        internal TaskCompletionSource CountsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CountsRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StatisticsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StatisticsRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken StatisticsToken { get; private set; }
        internal bool StatisticsDrained { get; private set; }
        internal bool FailWrite { get; init; }
        internal bool FailFileDispose { get; init; }
        internal int CreatedFiles { get; private set; }
        internal bool Committed { get; private set; }
        internal bool FileDisposed { get; private set; }
        internal CancellationToken ReadToken { get; private set; }
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CommitRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Columns => ["Value"];

        /// <summary>组装真实共享服务和桥接, 文件及模型 I/O 由本夹具控制.</summary>
        internal Fixture(TimeProvider? clock = null, bool locations = false, bool failLocationStore = false)
        {
            if (locations)
            {
                ownedRoot = Path.Combine(Path.GetTempPath(), "pbi-utils-export-bridge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedRoot);
                Destination = Path.Combine(Directory.CreateDirectory(Path.Combine(ownedRoot, "selected")).FullName, "Sales.xlsx");
                var storage = Path.Combine(ownedRoot, "settings");
                if (failLocationStore) File.WriteAllText(storage, "blocked storage root");
                Locations = new(storage);
            }
            var edits = new ModelEditService(this, this);
            Session = new(new(false, null), new(new RuntimeEnvironment()), message => Messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test",
                projects: new(edits), selectProject: _ => Task.FromResult<string?>("test.pbip"), connections: new TestLocalFileConnection(this).Service(), edits: edits,
                requestTimeProvider: clock, tableExports: new(this, this, this), tableStatistics: new StatisticsReader(this), tableRowCounts: new CountsReader(this),
                exportLocations: Locations, openExportDirectory: locations ? async (directory, token) =>
                {
                    OpenToken = token; OpenedDirectories.Add(directory); OpenEntered.TrySetResult();
                    if (BlockOpen) await OpenRelease.Task;
                    token.ThrowIfCancellationRequested();
                }
            : null, chooseTableExportFile: (request, _) =>
                {
                    SelectionCount++;
                    SelectedRequest = request;
                    return Task.FromResult(CancelSelection ? null : Destination);
                });
        }

        /// <summary>创建仅属于本夹具的目标目录, 用于真实目录记录与存在性检查.</summary>
        internal string DirectoryFor(string name) => Directory.CreateDirectory(Path.Combine(ownedRoot ?? throw new InvalidOperationException("Location fixture required"), name)).FullName;

        /// <summary>通过真实连接路由建立唯一当前模型并清除连接阶段消息.</summary>
        private long connectionRevision;
        internal async Task ConnectAsync()
        {
            await Call("project.open", new { }, "open");
            connectionRevision = Response("open").GetProperty("connectionRevision").GetInt64();
            Messages.Clear();
        }
        /// <summary>构造固定版本协议请求, 返回处理任务而不吞取消或错误.</summary>
        internal Task Call(string method, object parameters, string id) => Session.ReceiveAsync(OriginPolicy.Production,
            JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters, connectionRevision }));
        /// <summary>只取指定请求的唯一终态, 不把阶段事件当回执.</summary>
        internal JsonElement Response(string id) => Assert.Single(Messages, m => m.TryGetProperty("id", out var field) && field.GetString() == id);
        /// <summary>返回当前可控权威快照.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot); }
        /// <summary>本夹具不执行真实写入, 误调用立即失败.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new NotSupportedException();
        /// <summary>模拟不能立即中断的 SDK 调用, 退出边界检查取消后才消费行.</summary>
        public async Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            ReadLimits.Add(rowLimit);
            ReadToken = token;
            ReadEntered.TrySetResult();
            if (BlockRead) await ReadRelease.Task;
            token.ThrowIfCancellationRequested();
            return consume(this, token);
        }
        /// <summary>记录创建请求, 不实际访问文件系统.</summary>
        public Task<IModelTableExportFile> CreateAsync(string destination, ModelTableExportRequest request, CancellationToken token)
        { CreatedFiles++; writingRequest = request; return Task.FromResult<IModelTableExportFile>(new ExportFile(this, destination)); }
        /// <summary>消费零行表仍保留文件标题行为的委派边界.</summary>
        public long Write(string table, IModelTableRows rows, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains(Snapshot.Model.Tables, item => item.Name == table);
            if (FailWrite) throw new AppException("EXPORT_VALUE_UNSUPPORTED", "Excel 不支持非有限数值", 3);
            long count = 0;
            while (rows.Read()) { token.ThrowIfCancellationRequested(); count++; }
            return count;
        }
        /// <summary>与宿主共用发布裁决, 之后等待且不再次响应取消.</summary>
        public async Task CommitAsync(Action? beginCommit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            beginCommit?.Invoke();
            CommitEntered.TrySetResult();
            if (BlockCommit) await CommitRelease.Task;
            if (Locations is not null)
                Directory.CreateDirectory(writingRequest is { Format: "csv", Tables.Count: > 1 } ? Destination : Path.GetDirectoryName(Destination)!);
            Committed = true;
        }
        /// <summary>模拟零行结果.</summary>
        public bool Read() => false;
        /// <summary>空表没有当前行, 误读立即失败.</summary>
        public object? GetValue(int ordinal) => throw new InvalidOperationException();
        /// <summary>释放阻塞门闩并关闭测试自己的会话.</summary>
        public void Dispose()
        {
            ReadRelease.TrySetResult(); CommitRelease.TrySetResult(); StatisticsRelease.TrySetResult(); CountsRelease.TrySetResult(); OpenRelease.TrySetResult();
            Session.Dispose();
        }

        /// <summary>目录用例先排空真实Bridge请求再删除唯一测试目录, 不触及外部产物或用户进程.</summary>
        public async ValueTask DisposeAsync()
        {
            Dispose(); await Session.ShutdownAsync();
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
            {
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(ownedRoot), StringComparison.OrdinalIgnoreCase);
                Directory.Delete(ownedRoot, true);
            }
        }

        /// <summary>单次临时文件的生命周期独立于整个测试会话.</summary>
        private sealed class ExportFile(Fixture owner, string destination) : IModelTableExportFile
        {
            public string FileName => Path.GetFileName(destination);
            /// <summary>转交零行表写入边界.</summary>
            public long Write(string table, IModelTableRows rows, CancellationToken token) => owner.Write(table, rows, token);
            /// <summary>全部选定表写入后完成当前批次, 此测试不创建实际文件.</summary>
            public void Complete(CancellationToken token) => token.ThrowIfCancellationRequested();
            /// <summary>转交受控文件发布门闩.</summary>
            public Task CommitAsync(Action? beginCommit, CancellationToken token) => owner.CommitAsync(beginCommit, token);
            /// <summary>记录本次文件资源已释放, 不关闭当前会话.</summary>
            public void Dispose()
            {
                owner.FileDisposed = true;
                if (owner.FailFileDispose) throw new AppException("EXPORT_CLEANUP_FAILED", "导出临时文件清理失败", 4);
            }
        }

        /// <summary>仅替代外部精确计数用例, 记录Bridge传入的宿主快照与精确表范围并模拟同步排空.</summary>
        private sealed class CountsReader(Fixture owner) : IModelTableRowCountReader
        {
            /// <summary>不查询模型, 只返回所选表实际范围形状; 取消后仍等显式释放再退出.</summary>
            public async Task<ModelTableStatisticsResult> ReadRowCountsAsync(ProjectSnapshot snapshot, IReadOnlyList<string> tables, CancellationToken token)
            {
                owner.CountCalls++; owner.CountTables = [.. tables]; owner.CountSnapshot = snapshot; owner.CountToken = token;
                owner.CountsEntered.TrySetResult();
                try
                {
                    if (owner.BlockCounts) await owner.CountsRelease.Task;
                    token.ThrowIfCancellationRequested();
                    return new(snapshot.ProjectId, snapshot.BaselineId, tables.Select(table => new ModelTableStatistics(table, table == "Sales" ? 2 : 5, null, IsQueryCount: true)).ToArray());
                }
                finally { owner.CountsDrained = true; }
            }
        }

        /// <summary>受控只读统计游标只用于验证宿主门闩和取消, 不查询真实模型或文件.</summary>
        private sealed class StatisticsReader(Fixture owner) : IModelTableStatisticsReader
        {
            /// <summary>返回当前快照身份的固定统计, 模拟SDK内部收到取消仍等待返回, 边界检查取消后才发布.</summary>
            public async Task<ModelTableStatisticsResult> ReadStatisticsAsync(ProjectSnapshot snapshot, CancellationToken token)
            {
                owner.StatisticsToken = token;
                owner.StatisticsEntered.TrySetResult();
                try
                {
                    if (owner.BlockStatistics) await owner.StatisticsRelease.Task;
                    token.ThrowIfCancellationRequested();
                    return new(snapshot.ProjectId, snapshot.BaselineId, [new("Sales", 2, 1024)]);
                }
                finally { owner.StatisticsDrained = true; }
            }
        }
    }
    /// <summary>只推进当前测试的单次计时器, 不使用墙上时间等待长预算.</summary>
    private sealed class ExportClock : TimeProvider
    {
        private TimeSpan now;
        internal List<ExportTimer> Timers { get; } = [];
        /// <summary>记录真实宿主申请的阶段预算.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ExportTimer(callback, state, now + dueTime);
            Timers.Add(timer);
            return timer;
        }
        /// <summary>推进到期时刻, 已释放计时器不自动重放.</summary>
        internal void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in Timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray()) timer.Fire();
        }
    }

    /// <summary>也允许显式重放已排队的过期回调, 验证会话的 generation 防护.</summary>
    private sealed class ExportTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due { get; } = due;
        internal bool Disposed { get; private set; }
        /// <summary>触发一次回调, 不在夹具中吞掉宿主错误.</summary>
        internal void Fire() { Disposed = true; callback(state); }
        /// <summary>宿主每阶段另建计时器, 此测试不使用 Change.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        /// <summary>停止自动触发.</summary>
        public void Dispose() => Disposed = true;
        /// <summary>保持与同步释放等价.</summary>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
