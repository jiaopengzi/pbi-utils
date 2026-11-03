// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\PowerBiSchemaRecoveryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实 SDK 异常类型验证固定只读架构的断链恢复、取消、权限及诊断边界.

using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;
using ConnectionExceptionCause = Microsoft.AnalysisServices.ConnectionExceptionCause;

namespace PbiUtils.Desktop.Tests;

/// <summary>假架构会话不访问真实账号或网络, 只控制真实 SDK 异常的抛出位置.</summary>
public sealed class PowerBiSchemaRecoveryTests
{
    /// <summary>10053及10054断链只重新连接一次, 丢弃已有部分表字段并以同一后台目标完整重读.</summary>
    [Theory]
    [InlineData(10053, "MDSCHEMA_DIMENSIONS")]
    [InlineData(10054, "MDSCHEMA_DIMENSIONS")]
    [InlineData(10054, "MDSCHEMA_MEASURES")]
    public void ReconnectsOnceAndDiscardsAllPartialMetadata(int socketCode, string failedSchema)
    {
        var first = new Session { Failure = TransportFailure(socketCode), FailAt = failedSchema, TableName = "PartialTable" };
        var second = new Session { TableName = "FinalTable" }; var created = 0;
        var reader = new PowerBiSchemaReader(() => ++created == 1 ? first : second);
        var target = Target(); var snapshot = reader.Read("model://test", target, default, "original-handle");
        Assert.Equal(2, created); Assert.True(first.Disposed); Assert.True(second.Disposed);
        Assert.Same(target, first.OpenedTarget); Assert.Same(target, second.OpenedTarget);
        Assert.Equal("original-handle", snapshot.Connection!.ModelId);
        Assert.Equal("FinalTable", Assert.Single(snapshot.Model.Tables).Name);
        Assert.Equal("FinalTable", Assert.Single(snapshot.Model.Measures).Table);
        Assert.Equal(["MDSCHEMA_DIMENSIONS", "MDSCHEMA_HIERARCHIES", "MDSCHEMA_MEASURES"], second.Reads);
        Assert.Equal(failedSchema == "MDSCHEMA_DIMENSIONS" ? 1 : 3, first.Reads.Count);
    }

    /// <summary>第二次断链明确失败且不创建第三个会话, 页面只显示简短可操作提示.</summary>
    [Fact]
    public void SecondTransportFailureStopsWithoutPublishingOrThirdAttempt()
    {
        var sessions = new List<Session>();
        var reader = new PowerBiSchemaReader(() => { var session = new Session { Failure = TransportFailure() }; sessions.Add(session); return session; });
        var error = Assert.Throws<AppException>(() => reader.Read("model://test", Target(), default));
        Assert.Equal("SERVICE_CONNECTION_INTERRUPTED", error.Code); Assert.True(error.Message.Length < 100);
        Assert.Contains("重新连接", error.Message); Assert.DoesNotContain("Adomd", error.Message); Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(2, sessions.Count); Assert.All(sessions, session => Assert.True(session.Disposed));
    }

    /// <summary>恢复读取时的权限失败不能被第一次断链覆盖或伪装成成功.</summary>
    [Fact]
    public void PermissionFailureOnRecoveryRemainsAuthoritative()
    {
        var first = new Session { Failure = TransportFailure() };
        var second = new Session { Failure = new AppException("POWER_BI_ACCESS_DENIED", "目录权限已改变", 3) }; var created = 0;
        var reader = new PowerBiSchemaReader(() => ++created == 1 ? first : second);
        Assert.Equal("POWER_BI_ACCESS_DENIED", Assert.Throws<AppException>(() => reader.Read("model://test", Target(), default)).Code);
        Assert.Equal(2, created); Assert.True(first.Disposed); Assert.True(second.Disposed);
    }

    /// <summary>取消发生在首次失败后或第二次打开期间, 原令牌立即停止后续读取, 不创建额外预算.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalCancellationStopsBeforeOrDuringRecovery(bool duringRecovery)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Session { Failure = TransportFailure(), AfterDispose = duringRecovery ? null : cancellation.Cancel };
        var second = new Session { AfterOpen = cancellation.Cancel }; var created = 0;
        var reader = new PowerBiSchemaReader(() => ++created == 1 ? first : second);
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read("model://test", Target(), cancellation.Token));
        Assert.Equal(duringRecovery ? 2 : 1, created); Assert.True(first.Disposed); Assert.Empty(second.Reads);
        Assert.Equal(duringRecovery, second.Disposed);
    }

    /// <summary>预取消不创建连接, 权限、认证、超时及未知异常不能进入断链重试.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("authentication")]
    [InlineData("forbidden")]
    [InlineData("timeout")]
    [InlineData("unknown")]
    [InlineData("argument")]
    [InlineData("argumentOpen")]
    [InlineData("withoutHttp")]
    public void UnprovenFailuresAreNotRetried(string reason)
    {
        Exception failure = reason switch
        {
            "authentication" => TransportFailure(cause: ConnectionExceptionCause.AuthenticationFailed),
            "forbidden" => TransportFailure(status: HttpStatusCode.Forbidden),
            "timeout" => TransportFailure(10060),
            "argument" or "argumentOpen" => new ArgumentException("secret"),
            "withoutHttp" => SdkFailure(new IOException("secret", new SocketException(10054))),
            _ => new InvalidOperationException("secret")
        };
        var session = new Session { Failure = failure, FailAt = reason == "argumentOpen" ? "Open" : "MDSCHEMA_DIMENSIONS" }; var created = 0;
        var reader = new PowerBiSchemaReader(() => { created++; return session; });
        Assert.ThrowsAny<Exception>(() => reader.Read("model://test", Target(), new(reason == "cancel")));
        Assert.Equal(reason == "cancel" ? 0 : 1, created);
        Assert.Equal(reason != "cancel", session.Disposed);
    }

    /// <summary>恢复日志保留安全断链数字和类型, 不记录异常正文、令牌、账号或模型字段.</summary>
    [Fact]
    public void RecoveryDiagnosticsContainSafeFactsWithoutSensitiveExceptionText()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-schema-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = new LoggingRuntime(root, "test"); new LoggingService(runtime).Save("Debug", default);
            var first = new Session { Failure = TransportFailure() }; var second = new Session(); var created = 0;
            var reader = new PowerBiSchemaReader(() => ++created == 1 ? first : second, runtime.Factory.CreateLogger<PowerBiSchemaReader>());
            _ = reader.Read("model://test", Target(), default); runtime.Dispose();
            var output = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines));
            Assert.Contains("ModelReadRetry", output); Assert.Contains("10054", output); Assert.Contains("SocketException", output);
            Assert.DoesNotContain("secret", output); Assert.DoesNotContain("private-account", output); Assert.DoesNotContain("private-model", output);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>以真实 SDK 异常包装可控 HTTP 及 Socket 原因, 不基于消息文本判断故障.</summary>
    private static AdomdConnectionException TransportFailure(int socketCode = 10054, ConnectionExceptionCause cause = ConnectionExceptionCause.Unspecified, HttpStatusCode? status = null) =>
        SdkFailure(new HttpRequestException("secret HTTP reason", new IOException("secret IO reason", new SocketException(socketCode)), status), cause);
    /// <summary>锁定 SDK 的连接异常构造器为内部成员, 测试只通过反射创建, 生产代码不使用反射.</summary>
    private static AdomdConnectionException SdkFailure(Exception inner, ConnectionExceptionCause cause = ConnectionExceptionCause.Unspecified) =>
        (AdomdConnectionException)Activator.CreateInstance(typeof(AdomdConnectionException), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, ["secret SDK reason", inner, cause], null)!;
    /// <summary>只提供合成账号及令牌, 不使用已登录用户的任何配置.</summary>
    private static PowerBiServiceTarget Target() => new("pbiazure://api.powerbi.com", "private-model", "synthetic identity", "secret-token", DateTimeOffset.UtcNow.AddHours(1), "private-account", "stable-model", "Display");

    private sealed class Session : IPowerBiSchemaSession
    {
        public Exception? Failure { get; init; }
        public string FailAt { get; init; } = "MDSCHEMA_DIMENSIONS";
        public string TableName { get; init; } = "FinalTable";
        public Action? AfterOpen { get; init; }
        public Action? AfterDispose { get; init; }
        public bool Disposed { get; private set; }
        public List<string> Reads { get; } = [];
        public PowerBiServiceTarget? OpenedTarget { get; private set; }
        public string ServerVersion => "test-engine";
        /// <summary>捕获同一后台目标, 不建立网络连接.</summary>
        public void Open(PowerBiServiceTarget target)
        {
            OpenedTarget = target;
            if (FailAt == "Open" && Failure is not null) throw Failure;
            AfterOpen?.Invoke();
        }
        /// <summary>只允许三个固定架构行集, 指定位置抛出可控断链或权限异常.</summary>
        public DataTable Read(string schema)
        {
            Reads.Add(schema); if (schema == FailAt && Failure is not null) throw Failure;
            DataTable table = new();
            if (schema == "MDSCHEMA_DIMENSIONS")
            { table.Columns.Add("DIMENSION_UNIQUE_NAME"); table.Columns.Add("DIMENSION_TYPE"); table.Rows.Add("[" + TableName + "]", "1"); }
            else if (schema == "MDSCHEMA_HIERARCHIES")
            { table.Columns.Add("DIMENSION_UNIQUE_NAME"); table.Columns.Add("HIERARCHY_CAPTION"); table.Rows.Add("[" + TableName + "]", "Column"); }
            else if (schema == "MDSCHEMA_MEASURES")
            { table.Columns.Add("MEASUREGROUP_NAME"); table.Columns.Add("MEASURE_NAME"); table.Columns.Add("EXPRESSION"); table.Rows.Add(TableName, "Measure", "1"); }
            else throw new InvalidOperationException("Unexpected schema");
            return table;
        }
        /// <summary>标记当前会话释放, 用于精确触发两次尝试之间的取消.</summary>
        public void Dispose() { Disposed = true; AfterDispose?.Invoke(); }
    }
}
