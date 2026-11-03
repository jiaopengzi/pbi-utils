// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelConnectionDiagnosticsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模型连接诊断保留底层原因且不泄露令牌、连接字段、账号或路径.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Application.Connections;
using System.Data.Common;
using System.Reflection;
using Microsoft.AnalysisServices.AdomdClient;
using PbiUtils.Application;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ModelConnectionDiagnosticsTests
{
    /// <summary>两种云的共享容量发现使用30秒连接预算, 单次架构命令仍为10秒; SDK离线解析确认秒单位.</summary>
    /// <param name="host">受支持云的合成官方主机.</param>
    [Theory]
    [InlineData("api.powerbi.com")]
    [InlineData("api.powerbi.cn")]
    public void CloudDiscoveryHasSeparateThirtySecondConnectionBudget(string host)
    {
        var target = Target(host);
        var settings = new DbConnectionStringBuilder { ConnectionString = PowerBiSchemaReader.ConnectionString(target) };
        Assert.Equal("30", settings["Connect Timeout"]);
        Assert.Equal("10", settings["Timeout"]);
        // 只替换目标与认证字段以避免构造器访问真实云; 保留产品输出的预算供真实 SDK 解析.
        settings["Data Source"] = "localhost:1";
        settings.Remove("Integrated Security"); settings.Remove("Identity Provider");
        using var connection = new AdomdConnection(settings.ConnectionString);
        Assert.Equal(30, connection.ConnectionTimeout);
    }

    /// <summary>在锁定SDK中复现构造后设置AccessToken来不及的异常, 全程不调用Open或访问云模型.</summary>
    [Fact]
    public void LockedSdkRequiresCredentialsDuringPbiazureParsing()
    {
        var target = Target();
        Assert.Throws<AdomdConnectionException>(() => new AdomdConnection(PowerBiSchemaReader.ConnectionString(target)));
        // 检查真实 SDK 的认证句柄建立规则; 无地址的内存对象不会解析端点或发起 HTTP.
        var type = typeof(AdomdConnection).Assembly.GetType("Microsoft.AnalysisServices.AdomdClient.ConnectionInfo", true)!;
        var info = Activator.CreateInstance(type, true)!;
        var build = type.GetMethod("BuildExternalAuthenticationHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var missing = Assert.Throws<TargetInvocationException>(() => build.Invoke(info, [null]));
        Assert.IsType<AdomdConnectionException>(missing.InnerException);
        var properties = new DbConnectionStringBuilder { ConnectionString = PowerBiSchemaReader.ConnectionString(target, includeCredential: true) };
        type.GetField("password", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(info, properties["Password"]);
        Assert.Null(Record.Exception(() => build.Invoke(info, [null])));
    }
    /// <summary>底层异常原因可见, 敏感的连接串、已知令牌和 Windows 路径全部隐藏.</summary>
    [Fact]
    public void NestedReasonsRemainUsefulWithoutCredentials()
    {
        var target = Target();
        var inner = new InvalidOperationException("When interactive authentication is not supported, an external access-token is required.");
        var outer = new Exception("SDK failure; Password=top-secret-token; Data Source=pbiazure://api.powerbi.com; C:\\private folder\\credentials.dpapi", inner);
        var text = ModelConnectionDiagnostics.Describe(outer, target);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("an external access-token is required", text);
        Assert.Contains("0x", text);
        Assert.DoesNotContain("top-secret-token", text);
        Assert.DoesNotContain("private folder", text);
        Assert.DoesNotContain("api.powerbi.com", text);
        Assert.DoesNotContain("StackTrace", text);
    }
    /// <summary>未知凭据格式也必须脱敏, 包含带分号的引号值、JSON属性和Bearer头.</summary>
    [Theory]
    [InlineData("password=secret; reason=bad credentials", "secret")]
    [InlineData("PWD=\"first;secret\"; reason=bad credentials", "first;secret")]
    [InlineData("\"accessToken\":\"json-secret\"", "json-secret")]
    [InlineData("\"refresh_token\":\"refresh-secret\"", "refresh-secret")]
    [InlineData("token='opaque-secret';", "opaque-secret")]
    [InlineData("Bearer opaque-bearer-value", "opaque-bearer-value")]
    [InlineData("https://service.example/path?access_token=opaque", "service.example")]
    [InlineData("Read C:\\Users\\private user\\file.txt", "private user")]
    [InlineData("Read \\\\private-server\\private-share\\file.txt", "private-share")]
    [InlineData("Account person@example.com rejected", "person@example.com")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzZWNyZXQiOiJ2YWx1ZSJ9.signature", "eyJzZWNyZXQiOiJ2YWx1ZSJ9")]
    public void RedactsSensitiveFormats(string message, string sensitive) => Assert.DoesNotContain(sensitive, ModelConnectionDiagnostics.Sanitize(message));
    /// <summary>超长详情整体省略, 不截出半个未隐藏的令牌; 异常链有固定上限.</summary>
    [Fact]
    public void LimitsDiagnosticSizeAndChainDepth()
    {
        Assert.Equal("SDK 详情过长, 已省略", ModelConnectionDiagnostics.Sanitize(new string('x', 16385)));
        Exception error = new("deepest");
        for (var i = 0; i < 10; i++) error = new("level-" + i, error);
        var result = ModelConnectionDiagnostics.Describe(error);
        Assert.DoesNotContain("deepest", result);
        Assert.Equal(6, result.Split("Exception 0x", StringSplitOptions.None).Length - 1);
    }
    /// <summary>只有pbiazure初始化使用后端Password槽传递ClaimsToken, 诊断参数默认不带凭据.</summary>
    [Fact]
    public void PbiazureCredentialsAreExplicitAndNotPersisted()
    {
        var target = Target();
        var standard = new DbConnectionStringBuilder { ConnectionString = PowerBiSchemaReader.ConnectionString(target) };
        Assert.False(standard.ContainsKey("Password"));
        var credential = new DbConnectionStringBuilder { ConnectionString = PowerBiSchemaReader.ConnectionString(target, includeCredential: true) };
        Assert.Equal(target.AccessToken, credential["Password"]);
        Assert.Equal("False", credential["Persist Security Info"]);
        Assert.False(credential.ContainsKey("User ID"));
        Assert.Equal("ClaimsToken", credential["Integrated Security"]);
        Assert.DoesNotContain(target.AccessToken, ModelConnectionDiagnostics.Sanitize(credential.ConnectionString, target));
    }
    /// <summary>生成测试用目标, 不使用任何真实账号或密钥.</summary>
    private static PowerBiServiceTarget Target(string host = "api.powerbi.com") => new("pbiazure://" + host, "test-catalog", "test identity", "top-secret-token", DateTimeOffset.UtcNow.AddHours(1), "test-account-id");
}
