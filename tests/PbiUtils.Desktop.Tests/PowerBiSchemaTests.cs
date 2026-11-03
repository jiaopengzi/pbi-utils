// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\PowerBiSchemaTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证实际 Service 架构路由、元数据限制、取消、资源释放和错误脱敏.

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

public sealed class PowerBiSchemaTests
{
    /// <summary>真实标准字段可保留表达式、格式和文件夹, 隐藏与转义唯一名称不丢失.</summary>
    [Fact]
    public void MapsReturnedDefinitionsAndIncludesHiddenMeasures()
    {
        DataTable dimensions = new(); dimensions.Columns.Add("DIMENSION_UNIQUE_NAME"); dimensions.Columns.Add("DIMENSION_TYPE"); dimensions.Rows.Add("[Sales]", "1");
        DataTable measures = new();
        foreach (var column in new[] { "MEASUREGROUP_NAME", "MEASURE_NAME", "MEASURE_CAPTION", "MEASURE_UNIQUE_NAME", "EXPRESSION", "DEFAULT_FORMAT_STRING", "MEASURE_DISPLAY_FOLDER", "MEASURE_IS_VISIBLE" }) measures.Columns.Add(column);
        measures.Rows.Add("Sales", "Total", "translated", "[Measures].[Total]", "SUM('Sales'[Value])", "0.00", "Totals", "False");
        measures.Rows.Add("Sales", DBNull.Value, DBNull.Value, "[Measures].[Hidden]]Measure]", DBNull.Value, DBNull.Value, DBNull.Value, "false");
        var model = PowerBiSchemaReader.Map("model", dimensions, new(), measures);
        Assert.Equal("Total", model.Measures[0].Name); Assert.Equal("SUM('Sales'[Value])", model.Measures[0].Expression);
        Assert.Equal("0.00", model.Measures[0].FormatString); Assert.Equal("Totals", model.Measures[0].DisplayFolder);
        Assert.True(model.Measures[0].IsHidden); Assert.Equal("Hidden]Measure", model.Measures[1].Name); Assert.Equal("", model.Measures[1].Expression);
        var restrictions = PowerBiSchemaReader.Restrictions("MDSCHEMA_MEASURES");
        Assert.Single(restrictions.Cast<Microsoft.AnalysisServices.AdomdClient.AdomdRestriction>());
        Assert.Equal("MEASURE_VISIBILITY", restrictions[0].Name); Assert.Equal(3, restrictions[0].Value);
        Assert.Empty(PowerBiSchemaReader.Restrictions("MDSCHEMA_DIMENSIONS").Cast<Microsoft.AnalysisServices.AdomdClient.AdomdRestriction>());
    }
    /// <summary>Desktop 文件名由窗口标题而非数据库随机 ID 得到, 未命名窗口有明确后备.</summary>
    [Theory]
    [InlineData("销售分析.pbix - Power BI Desktop", "销售分析.pbix")]
    [InlineData("Sales * - Power BI Desktop", "Sales")]
    [InlineData("Power BI Desktop", "未命名报表")]
    public void UsesDesktopReportTitle(string title, string expected) => Assert.Equal(expected, ConnectedModelReader.DesktopModelName(title));

    private static readonly string[] ExpectedSchemas = ["MDSCHEMA_DIMENSIONS", "MDSCHEMA_HIERARCHIES", "MDSCHEMA_MEASURES"];

    /// <summary>实际 ConnectedModelReader 将 pbiazure 分发给架构会话, 不进入 TOM 或读取本机文件.</summary>
    [Theory]
    [InlineData("api.powerbi.com")]
    [InlineData("api.powerbi.cn")]
    public async Task SharedCapacityUsesSchemaSessionAndReportsLimitedMetadata(string host)
    {
        var session = new Session(); var cloud = new Cloud(host);
        var reader = new ConnectedModelReader(new NoFiles(), cloud, new(() => session));
        var service = new ModelConnectionService(reader);
        var modelId = Guid.NewGuid().ToString("N");
        var snapshot = await service.ServiceAsync(modelId, default);
        Assert.Equal(ExpectedSchemas, session.Reads);
        Assert.True(session.Opened); Assert.True(session.Disposed);
        Assert.True(snapshot.Connection!.IsLimited);
        Assert.Equal(modelId, snapshot.Connection.ModelId);
        Assert.Null(snapshot.Connection.InstanceId);
        Assert.Null(snapshot.Summary.CompatibilityLevel);
        Assert.Null(snapshot.Summary.ReportPageCount);
        Assert.Equal(1, snapshot.Summary.TableCount);
        Assert.Equal(1, snapshot.Summary.MeasureCount);
        Assert.Equal("Service sales", snapshot.Model.Name);
        Assert.Single(snapshot.Model.Tables); Assert.Single(snapshot.Model.Measures);
        Assert.Equal("销售]表", snapshot.Model.Tables[0].Name);
        Assert.Equal("日期", snapshot.Model.Tables[0].Columns[0].Name);
        Assert.Equal("", snapshot.Model.Measures[0].Expression);
        Assert.Contains(snapshot.Warnings, w => w.Contains("DAX 定义", StringComparison.Ordinal));
        Assert.DoesNotContain("sensitive-test-token", PowerBiSchemaReader.ConnectionString(cloud.Target));
        Assert.Equal(modelId, (await reader.ReadAsync(snapshot.ProjectPath, default)).Connection!.ModelId);
    }
    /// <summary>同步连接中取消, 返回后立即停止架构请求并清理连接与未生效注册.</summary>
    [Fact]
    public async Task CancellationAfterOpenStopsReadsAndDisposesSession()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { AfterOpen = cancellation.Cancel };
        var reader = new ConnectedModelReader(new NoFiles(), new Cloud(), new(() => session));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelConnectionService(reader).ServiceAsync(Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.Empty(session.Reads); Assert.True(session.Disposed);
    }
    /// <summary>失败显示发生阶段和安全错误代码, SDK 原文含令牌/路径也不能被回传.</summary>
    [Theory]
    [InlineData(true, "架构连接")]
    [InlineData(false, "表架构")]
    public async Task ErrorsIncludeStageWithoutSecretsAndDispose(bool failOpen, string stage)
    {
        var session = new Session { FailOpen = failOpen, FailRead = !failOpen };
        var reader = new ConnectedModelReader(new NoFiles(), new Cloud(), new(() => session));
        var error = await Assert.ThrowsAsync<AppException>(() => new ModelConnectionService(reader).ServiceAsync(Guid.NewGuid().ToString("N"), default));
        Assert.Equal("MODEL_CONNECTION_FAILED", error.Code);
        Assert.Contains(stage, error.Message); Assert.Contains("InvalidOperationException", error.Message);
        Assert.DoesNotContain("sensitive-test-token", error.Message); Assert.DoesNotContain("C:\\private", error.Message);
        Assert.DoesNotContain("Build 权限", error.Message); Assert.True(session.Disposed);
    }
    /// <summary>错误的行集结构不能被当作空模型成功读取.</summary>
    [Fact]
    public void InvalidTableSchemaFailsExplicitly() => Assert.Equal("MODEL_SCHEMA_INVALID", Assert.Throws<AppException>(() => PowerBiSchemaReader.Map("name", new(), new(), new())).Code);

    private sealed class Session : IPowerBiSchemaSession
    {
        public bool Opened { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailOpen { get; init; }
        public bool FailRead { get; init; }
        public Action? AfterOpen { get; init; }
        public string ServerVersion => "test-engine";
        public List<string> Reads { get; } = [];
        /// <summary>假连接不访问网络, 可控制取消或失败.</summary>
        public void Open(PowerBiServiceTarget target)
        {
            Opened = true;
            if (FailOpen) throw new InvalidOperationException("sensitive-test-token C:\\private");
            AfterOpen?.Invoke();
        }
        /// <summary>固定行集包含转义名称和 Measures 维度, 验证实际映射边界.</summary>
        public DataTable Read(string schema)
        {
            Reads.Add(schema);
            if (FailRead) throw new InvalidOperationException("sensitive-test-token C:\\private");
            DataTable result = new();
            var columns = schema switch
            {
                "MDSCHEMA_DIMENSIONS" => new[] { "DIMENSION_UNIQUE_NAME", "DIMENSION_TYPE" },
                "MDSCHEMA_HIERARCHIES" => ["DIMENSION_UNIQUE_NAME", "HIERARCHY_CAPTION"],
                "MDSCHEMA_MEASURES" => ["MEASUREGROUP_NAME", "MEASURE_CAPTION"],
                _ => throw new InvalidOperationException("Unexpected schema")
            };
            foreach (var column in columns) result.Columns.Add(column);
            if (schema == "MDSCHEMA_DIMENSIONS") { result.Rows.Add("[销售]]表]", "1"); result.Rows.Add("[Measures]", "2"); }
            else if (schema == "MDSCHEMA_HIERARCHIES") result.Rows.Add("[销售]]表]", "日期");
            else result.Rows.Add("销售]表", "销售额");
            return result;
        }
        /// <summary>记录会话释放, 无真实资源.</summary>
        public void Dispose() => Disposed = true;
    }
    private sealed class NoFiles : IProjectReader
    {
        /// <summary>服务模型路由不能退回文件系统读取.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => throw new InvalidOperationException("Unexpected file read");
    }
    private sealed class Cloud(string host = "api.powerbi.com") : IPowerBiServiceClient
    {
        public PowerBiServiceTarget Target { get; } = new("pbiazure://" + host, "vs-db", "test identity", "sensitive-test-token", DateTimeOffset.UtcNow.AddHours(1), "account", "db", "Service sales");
        public PowerBiAccount? Account => new("test@example.com");
        /// <summary>固定内存会话不执行认证.</summary>
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) => Task.FromResult(Account!);
        /// <summary>返回固定环境, 不调用真实服务.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiEnvironment>>([new("GlobalCloud", "全球版")]);
        /// <summary>路由测试不查询云目录.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiServiceModel>>([]);
        /// <summary>返回后端假目标以测试真实 Windows 路由.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Target); }
        /// <summary>内存会话退出不访问网络.</summary>
        public Task SignOutAsync(CancellationToken token, Action? committing = null) => Task.CompletedTask;
    }
}
