// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelTableStatisticsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证独立表统计的来源限制、未知值、身份基线及取消释放, 不连接真实模型.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Data;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed partial class ModelTableStatisticsTests
{
    /// <summary>离线工程不得把元数据表形状伪装为零行零字节, 统计也不能触发文件重新读取.</summary>
    [Fact]
    public async Task OfflineStatisticsRemainUnknownWithoutReadingFiles()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var statistics = Assert.IsType<IModelTableStatisticsReader>(reader, exactMatch: false);
        var snapshot = new ProjectSnapshot("offline.pbip", "project", "baseline", "TMDL", "", "", null,
            new("model", 1606, "en-US", [new("Sales", null, false, [], [])], [], [], 0, 0), [], [], []);
        var result = await statistics.ReadStatisticsAsync(snapshot, default);
        Assert.Equal("project", result.ProjectId); Assert.Equal("baseline", result.BaselineId);
        var table = Assert.Single(result.Tables);
        Assert.Equal("Sales", table.Table); Assert.Null(table.RowCount); Assert.Null(table.SizeBytes);
        Assert.False(result.IsSimulation);
    }

    /// <summary>共享容量自动查询所有表的当前账号实际行数, 且不改变可见模型指纹.</summary>
    [Fact]
    public async Task LimitedServiceAutomaticallyCountsAllTablesAndKeepsVisibleBaselineUnchanged()
    {
        var schema = new Schema { Tables = ["Sales", "Empty"] };
        var session = new Session { Count = name => name == "Empty" ? 0 : 60L };
        var (reader, snapshot) = await Connect(schema, new(), session);
        try
        {
            var result = await reader.ReadStatisticsAsync(snapshot, default);
            Assert.Equal(["Sales", "Empty"], result.Tables.Select(table => table.Table));
            Assert.Equal([60L, 0L], result.Tables.Select(table => table.RowCount));
            Assert.All(result.Tables, table => { Assert.True(table.IsQueryCount); Assert.Null(table.SizeBytes); });
            Assert.Equal(snapshot.ProjectId, result.ProjectId); Assert.Equal(snapshot.BaselineId, result.BaselineId);
            Assert.Equal(["DISCOVER_STORAGE_TABLES"], session.Reads);
            Assert.Equal(["Sales", "Empty"], Assert.Single(session.CountBatches));
            Assert.True(session.Disposed);
            Assert.True(snapshot.Connection!.IsLimited); Assert.Equal(3, schema.OpenCount);
            Assert.Equal(schema.OpenCount, schema.DisposeCount);
        }
        finally { reader.Disconnect(); }
    }
    /// <summary>Service 实际行数始终来自查询, 字节数要求已核验为 Import 且存储结构完整.</summary>
    [Theory]
    [InlineData(2, 77L)]
    [InlineData(1, null)]
    public async Task SharedServiceReadsStorageWhenImportModeIsConfirmed(int partitionMode, long? bytes)
    {
        var session = new Session { Storage = true, PartitionMode = partitionMode };
        var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            var table = Assert.Single((await reader.ReadStatisticsAsync(snapshot, default)).Tables);
            Assert.Equal(60L, table.RowCount);
            Assert.True(table.IsQueryCount);
            Assert.Equal(bytes, table.SizeBytes);
            Assert.Equal(["Sales"], Assert.Single(session.CountBatches));
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>服务不提供存储元数据时仍查询实际计数, 字节数保持未知并完成身份前后回读.</summary>
    [Fact]
    public async Task UnavailableStorageMetadataDoesNotPreventExactCounts()
    {
        var schema = new Schema(); var session = new Session { Unavailable = true };
        var (reader, snapshot) = await Connect(schema, new(), session);
        try
        {
            var result = await reader.ReadStatisticsAsync(snapshot, default);
            Assert.Equal(60L, result.Tables[0].RowCount); Assert.Null(result.Tables[0].SizeBytes);
            Assert.Equal(3, schema.OpenCount); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>大模型的存储明细超限时, Service 仍可返回完整实际计数.</summary>
    [Fact]
    public async Task StorageMetadataLimitDoesNotPreventServiceCounts()
    {
        var session = new Session { Storage = true, StorageLimit = true };
        var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            var table = Assert.Single((await reader.ReadStatisticsAsync(snapshot, default)).Tables);
            Assert.Equal(60L, table.RowCount);
            Assert.True(table.IsQueryCount);
            Assert.Null(table.SizeBytes);
            Assert.Equal(["Sales"], Assert.Single(session.CountBatches));
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>账号、云、数据库或可见架构变化拒绝旧快照, 不建立统计会话.</summary>
    [Theory]
    [InlineData("account")]
    [InlineData("cloud")]
    [InlineData("database")]
    [InlineData("schema")]
    public async Task ChangedIdentityOrDefinitionPreventsStatistics(string changed)
    {
        var schema = new Schema(); var cloud = new Cloud(); var session = new Session();
        var (reader, snapshot) = await Connect(schema, cloud, session);
        try
        {
            if (changed == "schema") schema.Revision++;
            else cloud.Target = Cloud.Changed(changed);
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal("STALE_BASELINE", error.Code); Assert.False(session.Opened);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>统计读取后目标、架构或认证失效不返回已采集结果, 并释放数据会话.</summary>
    [Theory]
    [InlineData("schema", "STALE_BASELINE")]
    [InlineData("account", "STALE_BASELINE")]
    [InlineData("authentication", "POWER_BI_SIGN_IN_REQUIRED")]
    public async Task ChangesAfterStatisticsPreventResultPublication(string changed, string expectedCode)
    {
        var schema = new Schema(); var cloud = new Cloud();
        var session = new Session { AfterRead = () => { if (changed == "schema") schema.Revision++; else if (changed == "account") cloud.Target = Cloud.Changed(changed); else cloud.SignedOut = true; } };
        var (reader, snapshot) = await Connect(schema, cloud, session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal(expectedCode, error.Code); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>同步打开或读取后受理取消; 即使释放再次失败也保留取消首错, 不伪装未知统计.</summary>
    [Theory]
    [InlineData("open", false)]
    [InlineData("open", true)]
    [InlineData("read", false)]
    [InlineData("read", true)]
    public async Task CancellationAtSdkBoundariesDisposesAndKeepsOriginalTerminal(string stage, bool cleanupFails)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterOpen = stage == "open" ? cancellation.Cancel : null, AfterRead = stage == "read" ? cancellation.Cancel : null, DisposeFailure = cleanupFails };
        var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadStatisticsAsync(snapshot, cancellation.Token));
            Assert.True(session.Disposed); Assert.Equal(stage == "open" ? 0 : 1, session.Reads.Count);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>SDK或单独清理失败均明确失败且不回传异常正文, 认证错误不能降级为未知值.</summary>
    [Theory]
    [InlineData("open")]
    [InlineData("read")]
    [InlineData("dispose")]
    public async Task SessionFailuresAreSafeAndDoNotReturnUnknownSuccess(string stage)
    {
        var session = new Session { OpenFailure = stage == "open", ReadFailure = stage == "read", DisposeFailure = stage == "dispose" };
        var (reader, snapshot) = await Connect(new(), new(), session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal("MODEL_STATISTICS_FAILED", error.Code);
            Assert.DoesNotContain("secret-test-token", error.Message); Assert.DoesNotContain("private", error.Message);
            Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>工厂失败不得伪装为统计未提供或成功, 未创建的会话不需释放.</summary>
    [Fact]
    public async Task SessionFactoryFailureRemainsSafeAndExplicit()
    {
        var reader = new ConnectedModelReader(new NoFiles(), new Cloud(), new(() => new Schema()),
            tableStatisticsSession: () => throw new InvalidOperationException("secret-test-token C:/private"));
        var path = reader.RegisterService(Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = await reader.ReadAsync(path, default);
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default));
            Assert.Equal("MODEL_STATISTICS_FAILED", error.Code);
            Assert.DoesNotContain("secret-test-token", error.Message); Assert.DoesNotContain("private", error.Message);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>注册已释放后不得按同名模型重建统计目标.</summary>
    [Fact]
    public async Task DisconnectedTargetDoesNotReopenByName()
    {
        var session = new Session();
        var (reader, snapshot) = await Connect(new(), new(), session);
        reader.Disconnect();
        Assert.Equal("MODEL_DISCONNECTED", (await Assert.ThrowsAsync<AppException>(() => reader.ReadStatisticsAsync(snapshot, default))).Code);
        Assert.False(session.Opened);
    }

    /// <summary>创建实际生产注册路由, 仅认证与元数据SDK会话由纯内存对象替代.</summary>
    private static async Task<(ConnectedModelReader Reader, ProjectSnapshot Snapshot)> Connect(Schema schema, Cloud cloud, Session session)
    {
        var reader = new ConnectedModelReader(new NoFiles(), cloud, new(() => schema), tableStatisticsSession: () => session);
        var path = reader.RegisterService(Guid.NewGuid().ToString("N"));
        try { return (reader, await reader.ReadAsync(path, default)); }
        catch { reader.Disconnect(); throw; }
    }

    /// <summary>提供可变化的权威架构, 不把实际统计字段并入定义指纹.</summary>
    private sealed class Schema : IPowerBiSchemaSession
    {
        public string[] Tables { get; init; } = ["Sales"];
        public string[] ColumnlessTables { get; init; } = [];
        public int Revision { get; set; }
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string ServerVersion => "test-engine";
        public void Open(PowerBiServiceTarget target) => OpenCount++;
        public DataTable Read(string schema)
        {
            if (schema == "MDSCHEMA_DIMENSIONS") return Data(["DIMENSION_UNIQUE_NAME", "DIMENSION_TYPE", "DESCRIPTION"], [.. Tables.Select(name => new object[] { "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]", 3, "revision-" + Revision })]);
            if (schema == "MDSCHEMA_HIERARCHIES") return Data(["DIMENSION_UNIQUE_NAME", "HIERARCHY_CAPTION"], [.. Tables.Except(ColumnlessTables, StringComparer.Ordinal).Select(name => new object[] { "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]", "Value" })]);
            return Data(["MEASURE_NAME"]);
        }
        public void Dispose() => DisposeCount++;
    }

    /// <summary>统计会话只接受固定行集, 可控制同步边界与资源释放失败.</summary>
    private sealed class Session : IPowerBiTableStatisticsSession
    {
        public bool Unavailable { get; init; }
        public bool Storage { get; init; }
        public bool StorageLimit { get; init; }
        public int PartitionMode { get; init; } = 2;
        public bool OpenFailure { get; init; }
        public bool ReadFailure { get; init; }
        public bool DisposeFailure { get; init; }
        public Action? AfterOpen { get; init; }
        public Action? AfterRead { get; init; }
        public Func<string, long> Count { get; init; } = _ => 60L;
        public int FailedCountBatch { get; init; }
        public string? InvalidCount { get; init; }
        public Action? AfterCountRead { get; init; }
        public List<string[]> CountBatches { get; } = [];
        public bool Opened { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> Reads { get; } = [];
        public void Open() { Opened = true; if (OpenFailure) throw new UnauthorizedAccessException("secret-test-token C:/private"); AfterOpen?.Invoke(); }
        public DataTable? Read(string schema, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads.Add(schema);
            if (ReadFailure) throw new InvalidOperationException("secret-test-token C:/private");
            if (StorageLimit && schema == "DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS")
                throw new AppException("MODEL_STATISTICS_LIMIT", "表统计超过读取上限", 3);
            AfterRead?.Invoke(); token.ThrowIfCancellationRequested();
            if (Unavailable) return null;
            return schema switch
            {
                "DISCOVER_STORAGE_TABLES" when Storage => Data(["DIMENSION_NAME", "TABLE_ID", "ROWS_COUNT"], ["Sales", "Sales (1)", 91L]),
                "DISCOVER_STORAGE_TABLES" => null,
                "DISCOVER_STORAGE_TABLE_COLUMNS" => Data(["DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "COLUMN_TYPE", "DICTIONARY_SIZE"], ["Sales", "Sales (1)", "Value", "BASIC_DATA", 55L]),
                "DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS" => Data(["DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "SEGMENT_NUMBER", "TABLE_PARTITION_NUMBER", "USED_SIZE"], ["Sales", "Sales (1)", "Value", 0, 0, 22L]),
                "TMSCHEMA_MODEL" => Data(["DefaultMode"], [0]),
                "TMSCHEMA_TABLES" => Data(["ID", "Name"], ["10", "Sales"]),
                "TMSCHEMA_PARTITIONS" => Data(["TableID", "Mode"], ["10", PartitionMode]),
                _ => throw new InvalidOperationException("Unexpected statistics schema")
            };
        }
        /// <summary>返回本批 Service 表的实际计数, 可模拟查询失败与无效结果.</summary>
        public DataTable ReadRowCounts(IReadOnlyList<string> tables, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CountBatches.Add([.. tables]);
            if (CountBatches.Count == FailedCountBatch) throw new UnauthorizedAccessException("secret-test-token C:/private");
            var rows = tables.Select(table => new object[] { table, Count(table) }).ToList();
            if (InvalidCount == "missing") rows.RemoveAt(0);
            if (InvalidCount == "duplicate") rows.Add(rows[0]);
            if (InvalidCount == "unknown") rows[0][0] = "Unexpected";
            if (InvalidCount == "null") rows[0][1] = DBNull.Value;
            if (InvalidCount == "fractional") rows[0][1] = 0.5m;
            if (InvalidCount == "negative") rows[0][1] = -1L;
            if (InvalidCount == "boolean") rows[0][1] = true;
            if (InvalidCount == "overflow") rows[0][1] = ulong.MaxValue;
            AfterCountRead?.Invoke(); token.ThrowIfCancellationRequested();
            if (InvalidCount == "columns") return Data(["__Table", "__Rows"], [.. rows]);
            if (InvalidCount == "extraColumn") return Data(["[__Table]", "[__Rows]", "extra"], [tables[0], Count(tables[0]), "unexpected"]);
            return Data(["[__Table]", "[__Rows]"], [.. rows]);
        }
        public void Dispose() { Disposed = true; if (DisposeFailure) throw new InvalidOperationException("secret-test-token C:/private"); }
    }

    /// <summary>仅返回测试目录中已注册的服务身份, 不读取或持久化真实凭据.</summary>
    private sealed class Cloud : IPowerBiServiceClient
    {
        public PowerBiServiceTarget Target { get; set; } = Changed("");
        public bool SignedOut { get; set; }
        public PowerBiAccount? Account => null;
        public static PowerBiServiceTarget Changed(string field) => new(field == "cloud" ? "pbiazure://api.powerbi.com" : "pbiazure://api.powerbi.cn", "catalog", "identity", "secret-test-token", DateTimeOffset.UtcNow.AddHours(1), field == "account" ? "different" : "account", field == "database" ? "different" : "database", "model");
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => throw new InvalidOperationException();
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<PowerBiServiceTarget> ResolveAsync(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (SignedOut) throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
            return Task.FromResult(Target);
        }
        public Task SignOutAsync(CancellationToken token, Action? committing = null) => Task.CompletedTask;
    }

    /// <summary>构造固定测试架构, 不请求网络、文件或真实引擎.</summary>
    private static DataTable Data(string[] columns, params object[][] rows)
    {
        var table = new DataTable();
        foreach (var column in columns) table.Columns.Add(column, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }

    /// <summary>统计测试的离线依赖不得访问文件或以重读结果替代未知统计.</summary>
    private sealed class NoFiles : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => throw new InvalidOperationException("No file access is allowed");
    }
}
