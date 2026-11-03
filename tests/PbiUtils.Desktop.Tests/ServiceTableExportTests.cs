// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ServiceTableExportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证共享容量 Service 的真实导出路由、身份复核与取消释放, 使用内存会话而非网络.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Data;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ServiceTableExportTests
{
    /// <summary>受限元数据来源仍可读取数据; 新查询前重新读取相同目标架构, 取消后不得消费数据.</summary>
    [Fact]
    public async Task SharedServiceExportRechecksSchemaAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var schema = new Schema();
        var reader = new ConnectedModelReader(new NoFiles(), new Cloud(), new(() => schema));
        var path = reader.RegisterService(Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = await reader.ReadAsync(path, default);
            Assert.True(snapshot.Connection!.IsLimited);
            schema.AfterOpen = cancellation.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException("Must not consume after cancellation"), cancellation.Token));
            Assert.Equal(2, schema.OpenCount);
            Assert.Equal(2, schema.DisposeCount);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>缺失架构列或层次标题不代表实际数据列; 真实路由保留全部原始类型、内部同名用户列和重复行.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedServiceConsumesActualColumnsAndPreservesRows(bool missingSchemaColumns)
    {
        var schema = new Schema { MissingColumns = missingSchemaColumns };
        var session = new DataSession();
        var cloud = new Cloud();
        var (reader, snapshot) = await Connect(schema, cloud, () => session);
        try
        {
            List<object?[]> actual = [];
            var count = await reader.ReadAsync(snapshot, "Sales", null, (rows, token) =>
            {
                Assert.Equal(session.Rows.Columns, rows.Columns);
                while (rows.Read()) actual.Add([.. Enumerable.Range(0, rows.Columns.Count).Select(rows.GetValue)]);
                return actual.Count;
            }, default);
            Assert.Equal(2, count); Assert.Equal(2, actual.Count);
            Assert.Equal(actual[0], actual[1]); Assert.IsType<long>(actual[0][3]); Assert.Equal(9223372036854775806L, actual[0][3]);
            Assert.IsType<DateTime>(actual[0][4]); Assert.Equal("=SUM(A1)", actual[0][0]);
            Assert.Equal("Sales", session.Table); Assert.Same(cloud.Target, session.Target);
            Assert.True(session.Disposed); Assert.Equal(2, schema.OpenCount); Assert.True(snapshot.Connection!.IsLimited);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>共享Service空表通过文件用例导出真实标题, 无伪造数据行, 不取层次caption作为标题.</summary>
    [Fact]
    public async Task EmptySharedTableExportsActualHeadersThroughFileService()
    {
        var schema = new Schema { MissingColumns = true };
        var session = new DataSession { Rows = new(["隐藏字段", "RowNumber"], []) };
        var (reader, snapshot) = await Connect(schema, new(), () => session);
        var directory = Path.Combine(Path.GetTempPath(), "pbi-utils-service-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var destination = Path.Combine(directory, "empty.csv");
        try
        {
            var result = await new ModelTableExportService(reader, reader, new ModelTableFileWriter()).ExportAsync(snapshot.ProjectPath,
                new(snapshot.ProjectId, snapshot.BaselineId, ["Sales"], "csv"), destination, default);
            Assert.Equal(0, result.RowCount); Assert.False(result.IsSimulation);
            Assert.Equal("隐藏字段,RowNumber\r\n", await File.ReadAllTextAsync(destination, Encoding.UTF8));
            Assert.True(session.Disposed); Assert.Single(Directory.GetFiles(directory));
        }
        finally { reader.Disconnect(); File.Delete(destination); Directory.Delete(directory); }
    }

    /// <summary>当前账号、云或模型标识改变时, 查询前可见架构身份复核拒绝旧快照, 不建立数据会话.</summary>
    [Theory]
    [InlineData("account")]
    [InlineData("cloud")]
    [InlineData("database")]
    [InlineData("schema")]
    public async Task ChangedServiceIdentityCannotStartQuery(string change)
    {
        var schema = new Schema(); var cloud = new Cloud(); var opened = false;
        var (reader, snapshot) = await Connect(schema, cloud, () => { opened = true; return new DataSession(); });
        var original = cloud.Target;
        if (change == "schema") schema.Caption = "Changed visible schema";
        else cloud.Target = new(change == "cloud" ? "pbiazure://api.powerbi.cn" : original.Server, original.Database, original.IdentityProvider,
            original.AccessToken, original.ExpiresOn, change == "account" ? "other account" : original.AccountId,
            change == "database" ? "other database" : original.DatabaseId, original.ModelName);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException(), default));
            Assert.Equal("STALE_BASELINE", error.Code); Assert.False(opened); Assert.Equal(2, schema.DisposeCount);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>数据会话同步打开后取消, 返回边界立即停止且释放会话, 不调用消费器.</summary>
    [Fact]
    public async Task CancellationAfterDataOpenDisposesAndDoesNotConsume()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new DataSession { AfterOpen = cancellation.Cancel };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException("Must not consume"), cancellation.Token));
            Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>会话工厂或SDK原文包含令牌和路径时, 只返回固定安全错误, 失败不重试数据读取.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DataFailuresAreSafeAndNeverRetried(bool factoryFailure)
    {
        var attempts = 0; var session = new DataSession { FailOpen = true };
        var (reader, snapshot) = await Connect(new(), new(), () =>
        {
            attempts++;
            return factoryFailure ? throw new InvalidOperationException("sensitive-test-token C:\\private") : session;
        });
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException(), default));
            Assert.Equal("EXPORT_QUERY_FAILED", error.Code); Assert.DoesNotContain("sensitive-test-token", error.Message); Assert.DoesNotContain("C:\\private", error.Message);
            Assert.Equal(1, attempts); Assert.Equal(!factoryFailure, session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>实际游标无字段时明确失败, 不以有表架构或有层次标题宣称可导出空文件.</summary>
    [Fact]
    public async Task ActualZeroFieldsFailsBeforeConsumer()
    {
        var session = new DataSession { Rows = new([], []) };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException(), default));
            Assert.Equal("EXPORT_TABLE_EMPTY", error.Code); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>消费器首错与SDK清理同时失败时仍保留原业务错误, 不被次要释放错误覆盖.</summary>
    [Fact]
    public async Task DataCleanupFailureDoesNotReplaceOriginalConsumerFailure()
    {
        var session = new DataSession { AfterDispose = () => throw new InvalidOperationException("sensitive-test-token C:\\private") };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new AppException("EXPORT_EXCEL_LIMIT", "Excel limit"), default));
            Assert.Equal("EXPORT_EXCEL_LIMIT", error.Code); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>消费期间取消且SDK清理再失败时, 仍返回原取消而不是查询失败.</summary>
    [Fact]
    public async Task DataCleanupFailureDoesNotReplaceCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new DataSession { AfterDispose = () => throw new InvalidOperationException("sensitive-test-token C:\\private") };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, token) =>
            {
                cancellation.Cancel(); token.ThrowIfCancellationRequested(); return 0;
            }, cancellation.Token));
            Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>没有先前失败时SDK清理失败不得伪装成功, 返回固定安全错误且不泄漏原文.</summary>
    [Fact]
    public async Task CleanupFailureAloneFailsSafely()
    {
        var session = new DataSession { AfterDispose = () => throw new InvalidOperationException("sensitive-test-token C:\\private") };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => 0, default));
            Assert.Equal("EXPORT_QUERY_FAILED", error.Code); Assert.DoesNotContain("sensitive-test-token", error.Message); Assert.DoesNotContain("C:\\private", error.Message);
            Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>数据已完整读取后可见架构变化, 共享服务拒绝发布且完整保留原文件, 临时文件清理.</summary>
    [Fact]
    public async Task ChangedSchemaAfterQueryPreservesExistingDestination()
    {
        var schema = new Schema(); var session = new DataSession { AfterDispose = () => schema.Caption = "Changed after query" };
        var (reader, snapshot) = await Connect(schema, new(), () => session);
        var directory = Path.Combine(Path.GetTempPath(), "pbi-utils-service-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var destination = Path.Combine(directory, "original.csv");
        await File.WriteAllTextAsync(destination, "keep original");
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => new ModelTableExportService(reader, reader, new ModelTableFileWriter()).ExportAsync(snapshot.ProjectPath,
                new(snapshot.ProjectId, snapshot.BaselineId, ["Sales"], "csv"), destination, default));
            Assert.Equal("STALE_BASELINE", error.Code); Assert.Equal("keep original", await File.ReadAllTextAsync(destination));
            Assert.True(session.Disposed); Assert.Single(Directory.GetFiles(directory));
        }
        finally { reader.Disconnect(); File.Delete(destination); Directory.Delete(directory); }
    }

    /// <summary>固定查询只转义已选表名, 标题仅去精确表限定前缀, 特殊括号和未知格式不被猜测截断.</summary>
    [Fact]
    public void QueryAndHeadersPreserveSpecialIdentifiers()
    {
        Assert.Equal("EVALUATE\n'导出''O\"Brien]表['", ModelTableExportQuery.Create("导出'O\"Brien]表["));
        Assert.Equal("列[名]尾", PowerBiServiceTableDataReader.ColumnName("表[名]", "表[名][列[名]尾]"));
        Assert.Equal("列]名", PowerBiServiceTableDataReader.ColumnName("O'Brien", "'O''Brien'[列]名]"));
        Assert.Equal("Other[Value]", PowerBiServiceTableDataReader.ColumnName("Sales", "Other[Value]"));
        Assert.Equal("[Value]", PowerBiServiceTableDataReader.ColumnName("Sales", "[Value]"));
    }

    /// <summary>Service 查询使用 API 核验的 long 布局, SAMPLE 保留 SDK 要求的三个参数和固定常量表达式.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void SamplingQuerySuppliesRequiredConstantExpression(int limit)
        => Assert.Equal("EVALUATE\nSAMPLE ( " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", 'Sales', 0 )", ModelTableExportQuery.Create("Sales", limit));

    /// <summary>模型侧每表上限传至真实生产Service适配路由, 标题仍来自实际游标而非架构caption.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    [InlineData(null)]
    public async Task SamplingLimitReachesBoundServiceDataSession(int? limit)
    {
        var session = new DataSession { Rows = new(["Actual"], [[7L]]) };
        var (reader, snapshot) = await Connect(new(), new(), () => session);
        try
        {
            var count = await reader.ReadAsync(snapshot, "Sales", limit, (rows, token) =>
            {
                Assert.Equal(["Actual"], rows.Columns);
                long result = 0;
                while (rows.Read()) { token.ThrowIfCancellationRequested(); Assert.Equal(7L, rows.GetValue(0)); result++; }
                return result;
            }, default);
            Assert.Equal(1, count); Assert.Equal(limit, session.RowLimit); Assert.True(session.Disposed);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>通过实际ConnectedModelReader创建受限快照, 工厂只替换外部数据会话, 不替换被测路由.</summary>
    private static async Task<(ConnectedModelReader Reader, ProjectSnapshot Snapshot)> Connect(Schema schema, Cloud cloud, Func<IPowerBiTableDataSession> data)
    {
        var reader = new ConnectedModelReader(new NoFiles(), cloud, new(() => schema), serviceTableData: data);
        var path = reader.RegisterService(Guid.NewGuid().ToString("N"));
        try { return (reader, await reader.ReadAsync(path, default)); }
        catch { reader.Disconnect(); throw; }
    }

    /// <summary>固定架构与错误注入不接触任何业务数据、网络或凭据存储.</summary>
    private sealed class Schema : IPowerBiSchemaSession
    {
        public Action? AfterOpen { get; set; }
        public bool MissingColumns { get; init; }
        public string Caption { get; set; } = "Display hierarchy";
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string ServerVersion => "test-engine";
        /// <summary>记录一次打开并在测试指定的同步边界触发动作.</summary>
        public void Open(PowerBiServiceTarget target) { OpenCount++; AfterOpen?.Invoke(); }
        /// <summary>仅返回真实读取器需要的固定行集, 不提供完整TOM元数据.</summary>
        public DataTable Read(string name)
        {
            DataTable table = new();
            if (name == "MDSCHEMA_DIMENSIONS")
            {
                table.Columns.Add("DIMENSION_UNIQUE_NAME"); table.Columns.Add("DIMENSION_TYPE"); table.Rows.Add("[Sales]", "1");
            }
            else if (name == "MDSCHEMA_HIERARCHIES")
            {
                table.Columns.Add("DIMENSION_UNIQUE_NAME"); table.Columns.Add("HIERARCHY_CAPTION");
                if (!MissingColumns) table.Rows.Add("[Sales]", Caption);
            }
            else { table.Columns.Add("MEASUREGROUP_NAME"); table.Columns.Add("MEASURE_NAME"); }
            return table;
        }
        /// <summary>记录每轮会话总会释放.</summary>
        public void Dispose() => DisposeCount++;
    }

    /// <summary>只模拟外部游标, 生产读取器仍负责身份、取消、错误及资源边界.</summary>
    private sealed class DataSession : IPowerBiTableDataSession
    {
        public DataRows Rows { get; init; } = new(["Text", "Hidden", "RowNumber", "Big", "Date"],
            [["=SUM(A1)", true, 7L, 9223372036854775806L, new DateTime(1900, 1, 1)], ["=SUM(A1)", true, 7L, 9223372036854775806L, new DateTime(1900, 1, 1)]]);
        public Action? AfterOpen { get; init; }
        public Action? AfterDispose { get; init; }
        public bool FailOpen { get; init; }
        public bool Disposed { get; private set; }
        public string? Table { get; private set; }
        public int? RowLimit { get; private set; }
        public PowerBiServiceTarget? Target { get; private set; }
        public IModelTableRows OpenTable(PowerBiServiceTarget target, string table, int? rowLimit, CancellationToken token)
        {
            Target = target; Table = table; RowLimit = rowLimit;
            if (FailOpen) throw new InvalidOperationException("sensitive-test-token C:\\private");
            AfterOpen?.Invoke(); return Rows;
        }
        public void Dispose() { Disposed = true; AfterDispose?.Invoke(); }
    }

    /// <summary>逐行暴露原始标量, 不进行JSON或浮点转换.</summary>
    private sealed class DataRows(IReadOnlyList<string> columns, object?[][] values) : IModelTableRows
    {
        private int row = -1;
        public IReadOnlyList<string> Columns => columns;
        public bool Read() => ++row < values.Length;
        public object? GetValue(int ordinal) => values[row][ordinal];
    }

    private sealed class NoFiles : IProjectReader
    {
        /// <summary>Service路由不得回落文件读取.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => throw new InvalidOperationException("No file reads");
    }

    private sealed class Cloud : IPowerBiServiceClient
    {
        public PowerBiServiceTarget Target { get; set; } = new("pbiazure://api.powerbi.com", "vs-db", "test identity", "sensitive-test-token", DateTimeOffset.UtcNow.AddHours(1), "account", "database", "Service sales");
        public PowerBiAccount? Account => null;
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => throw new InvalidOperationException();
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => throw new InvalidOperationException();
        /// <summary>返回当前后端假身份; 参数不含令牌并在网络边界之前尊重取消.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Target); }
        public Task SignOutAsync(CancellationToken token, Action? committing = null) => Task.CompletedTask;
    }
}
