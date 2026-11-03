// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BusinessLogEventTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证统一日志事件的等级过滤, 延迟求值, 结构化兼容和安全错误边界.

using PbiUtils.Application.Logging;
using System.Collections;
using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>通过严格日志接收器验证封装边界, 不依赖日志后端的再次过滤.</summary>
public sealed class BusinessLogEventTests
{
    /// <summary>关闭 Debug 时固定事件不提交日志, 身份读取及错误码提取也不执行.</summary>
    [Fact]
    public void DisabledDebugEventsDoNotLogOrEvaluateDeferredFields()
    {
        var logger = new StrictLogger { Minimum = LogLevel.Information };
        BusinessLog.BridgeTimeout(logger, "Execution", TimeSpan.FromSeconds(180));
        BusinessLog.RemoteConnectionSubmitted(logger, null);
        BusinessLog.RemoteDialogSubmitted(logger, true, true);
        BusinessLog.WindowsNetworkContext(logger, () => throw new InvalidOperationException("Disabled identity getter was evaluated"));
        BusinessLog.ConnectionFailure(logger, "Connect", new PrivateException(), _ => throw new InvalidOperationException("Disabled XMLA getter was evaluated"));
        Assert.Empty(logger.Events);
    }

    /// <summary>固定事件保留既有模板, 字段名和毫秒单位, 默认连接执行时限为 30 秒.</summary>
    [Fact]
    public void FixedDebugEventsKeepTemplatesAndStructuredFields()
    {
        var logger = new StrictLogger();
        BusinessLog.BridgeTimeout(logger, "NativeInput", TimeSpan.FromMilliseconds(1234.5));
        BusinessLog.RemoteConnectionSubmitted(logger, null);
        BusinessLog.RemoteConnectionSubmitted(logger, TimeSpan.FromMilliseconds(750.25));

        Assert.Equal(3, logger.Events.Count);
        var timeout = logger.Events[0];
        AssertEvent(timeout, LogLevel.Debug, "BridgeTimeout", "Bridge timeout armed at {Stage}; timeout {TimeoutMs}");
        Assert.Equal("NativeInput", timeout.Fields["Stage"]);
        Assert.Equal(1234.5, timeout.Fields["TimeoutMs"]);
        var defaults = logger.Events[1];
        AssertEvent(defaults, LogLevel.Debug, "RemoteConnectionSubmitted", "Remote connection stage {Stage}; timeout {TimeoutMs}");
        Assert.Equal("SelectionSubmitted", defaults.Fields["Stage"]);
        Assert.Equal(30000d, defaults.Fields["TimeoutMs"]);
        Assert.Equal(750.25, logger.Events[2].Fields["TimeoutMs"]);
    }

    /// <summary>原生提交事件仅记录安全认证模式和记住状态, 不记录身份或凭据.</summary>
    /// <param name="explicitIdentity">是否使用原生指定的 Windows 身份.</param>
    /// <param name="remembered">是否记住连接.</param>
    /// <param name="expectedMode">原模板中的安全认证模式.</param>
    [Theory]
    [InlineData(false, false, "CurrentIdentity")]
    [InlineData(true, true, "WindowsExplicit")]
    public void RemoteDialogSubmissionKeepsSafeMode(bool explicitIdentity, bool remembered, string expectedMode)
    {
        var logger = new StrictLogger();
        BusinessLog.RemoteDialogSubmitted(logger, explicitIdentity, remembered);
        var entry = Assert.Single(logger.Events);
        AssertEvent(entry, LogLevel.Debug, "RemoteDialogSubmitted", "Remote dialog stage {Stage}; authentication {AuthenticationMode}; remembered {ConnectionRemembered}");
        Assert.Equal("Submitted", entry.Fields["Stage"]);
        Assert.Equal(expectedMode, entry.Fields["AuthenticationMode"]);
        Assert.Equal(remembered, entry.Fields["ConnectionRemembered"]);
        Assert.Equal(4, entry.Fields.Count);
    }

    /// <summary>动态调整最低等级后及时生效, 身份 getter 只在启用时求值一次.</summary>
    [Fact]
    public void DeferredIdentityFollowsCurrentMinimumLevel()
    {
        var logger = new StrictLogger { Minimum = LogLevel.Warning };
        var reads = 0;
        TokenImpersonationLevel? ReadLevel() { reads++; return TokenImpersonationLevel.Impersonation; }
        BusinessLog.WindowsNetworkContext(logger, ReadLevel);
        logger.Minimum = LogLevel.Debug;
        BusinessLog.WindowsNetworkContext(logger, ReadLevel);
        logger.Minimum = LogLevel.Warning;
        BusinessLog.WindowsNetworkContext(logger, ReadLevel);

        Assert.Equal(1, reads);
        var entry = Assert.Single(logger.Events);
        AssertEvent(entry, LogLevel.Debug, "WindowsNetworkContext", "Windows network identity stage {Stage}; impersonation {ImpersonationLevel}");
        Assert.Equal("OutboundContextApplied", entry.Fields["Stage"]);
        Assert.Equal("Impersonation", entry.Fields["ImpersonationLevel"]);
    }

    /// <summary>未取得上下文时保持既有 None 值, 不把缺失身份记录为真实模拟成功.</summary>
    [Fact]
    public void MissingIdentityKeepsNoneValue()
    {
        var logger = new StrictLogger();
        BusinessLog.WindowsNetworkContext(logger, () => null);
        Assert.Equal("None", Assert.Single(logger.Events).Fields["ImpersonationLevel"]);
    }

    /// <summary>错误记录最多遍历六层, 原始异常正文, Data 和 ToString 均不可访问.</summary>
    [Fact]
    public void ConnectionFailureBoundsChainAndNeverReadsPrivateExceptionContent()
    {
        Exception error = new PrivateException();
        for (var n = 0; n < 7; n++) error = new PrivateException(error);
        var logger = new StrictLogger();
        List<Exception> extracted = [];
        BusinessLog.ConnectionFailure(logger, "MetadataRead", error, current => { extracted.Add(current); return []; });

        Assert.Equal(6, extracted.Count);
        Assert.Same(error, extracted[0]);
        Assert.Equal(6, logger.Events.Count);
        for (var n = 0; n < logger.Events.Count; n++)
        {
            var entry = logger.Events[n];
            AssertEvent(entry, LogLevel.Debug, "ConnectionFailure", "Model connection failure at {Stage}; depth {Depth}; type {ErrorType}; HRESULT {HResult}; socket {SocketError}; native {NativeErrorCode}");
            Assert.Equal("MetadataRead", entry.Fields["Stage"]);
            Assert.Equal(n, entry.Fields["Depth"]);
            Assert.Equal(typeof(PrivateException).FullName, entry.Fields["ErrorType"]);
            Assert.Equal("0x80131500", entry.Fields["HResult"]);
            Assert.Null(entry.Fields["SocketError"]);
            Assert.Null(entry.Fields["NativeErrorCode"]);
            Assert.DoesNotContain("private", entry.Message);
            Assert.DoesNotContain(entry.Fields.Values, value => value is Exception);
        }
    }

    /// <summary>连接错误仅输出安全类型与数字码, XMLA 获取器在启用等级后按层执行.</summary>
    [Fact]
    public void ConnectionFailureKeepsSocketNativeAndXmlaDiagnostics()
    {
        var logger = new StrictLogger();
        var socket = new SocketException((int)SocketError.ConnectionReset);
        var native = new Win32Exception(1326, "private-account-password");
        var error = new PrivateException(native);
        var reads = 0;
        BusinessLog.ConnectionFailure(logger, "Connect", socket);
        BusinessLog.ConnectionFailure(logger, "Connect", error, current =>
        {
            reads++;
            return ReferenceEquals(current, native) ? ["0x80070005", "0xC1210001"] : [];
        });

        Assert.Equal(2, reads);
        Assert.Equal(4, logger.Events.Count);
        Assert.Equal("ConnectionReset", logger.Events[0].Fields["SocketError"]);
        Assert.Equal(10054, logger.Events[0].Fields["NativeErrorCode"]);
        Assert.Equal(1326, logger.Events[2].Fields["NativeErrorCode"]);
        Assert.Null(logger.Events[2].Fields["SocketError"]);
        var xmla = logger.Events[3];
        AssertEvent(xmla, LogLevel.Debug, "XmlaFailure", "Model XMLA failure at {Stage}; codes {XmlaErrorCodes}");
        Assert.Equal("Connect", xmla.Fields["Stage"]);
        Assert.Equal("0x80070005 -> 0xC1210001", xmla.Fields["XmlaErrorCodes"]);
        Assert.All(logger.Events, entry => Assert.DoesNotContain("private-account-password", entry.Message));
    }

    /// <summary>XMLA 回调仅允许前六项中的 ASCII 十六进制码, 任意正文不得进入模板或字段.</summary>
    [Fact]
    public void XmlaCallbackRejectsPrivateInvalidAndUnboundedValues()
    {
        var logger = new StrictLogger();
        BusinessLog.ConnectionFailure(logger, "Connect", new PrivateException(), _ =>
            ["private-secret-token", "0x80070005", "0x123", "0x１２３４５６７８", "0xC1210001\npassword", "0xC1210001", "0xF0000007"]);

        Assert.Equal(2, logger.Events.Count);
        var xmla = logger.Events[1];
        AssertEvent(xmla, LogLevel.Debug, "XmlaFailure", "Model XMLA failure at {Stage}; codes {XmlaErrorCodes}");
        Assert.Equal("0x80070005 -> 0xC1210001", xmla.Fields["XmlaErrorCodes"]);
        Assert.DoesNotContain("private", xmla.Message);
        Assert.DoesNotContain("password", xmla.Message);
        Assert.DoesNotContain("0xF0000007", xmla.Message);

        var before = logger.Events.Count;
        BusinessLog.ConnectionFailure(logger, "Connect", new PrivateException(), _ => ["private-secret-token", "not-a-code"]);
        Assert.Equal(before + 1, logger.Events.Count);
    }

    /// <summary>即使回调全部是安全码也只记录前六项, 保持连接诊断输出有界.</summary>
    [Fact]
    public void XmlaCallbackRecordsAtMostSixSafeCodes()
    {
        var logger = new StrictLogger();
        BusinessLog.ConnectionFailure(logger, "MetadataRead", new PrivateException(), _ =>
            ["0x00000001", "0x00000002", "0x00000003", "0x00000004", "0x00000005", "0x00000006", "0x00000007"]);

        Assert.Equal(2, logger.Events.Count);
        Assert.Equal("0x00000001 -> 0x00000002 -> 0x00000003 -> 0x00000004 -> 0x00000005 -> 0x00000006", logger.Events[1].Fields["XmlaErrorCodes"]);
        Assert.DoesNotContain("0x00000007", logger.Events[1].Message);
    }

    /// <summary>Warning 清理事件可独立于 Debug 开启, 提高最低等级后不调用日志后端.</summary>
    [Fact]
    public void CleanupWarningsRespectLevelAndKeepOriginalTemplates()
    {
        var logger = new StrictLogger { Minimum = LogLevel.Warning };
        BusinessLog.StagingCleanupIncomplete(logger);
        BusinessLog.HistoryCleanupIncomplete(logger);
        logger.Minimum = LogLevel.Error;
        BusinessLog.StagingCleanupIncomplete(logger);
        BusinessLog.HistoryCleanupIncomplete(logger);

        Assert.Equal(2, logger.Events.Count);
        AssertEvent(logger.Events[0], LogLevel.Warning, "StagingCleanupIncomplete", "Business staging cleanup incomplete; original and published files preserved");
        AssertEvent(logger.Events[1], LogLevel.Warning, "HistoryCleanupIncomplete", "Business history temporary cleanup incomplete; original history preserved");
        Assert.All(logger.Events, entry => Assert.Single(entry.Fields));
    }

    /// <summary>验证固定事件保持等级和原始模板, 使用明确事件名称且不附带原始异常.</summary>
    /// <param name="entry">接收器捕获的实际事件.</param>
    /// <param name="level">原日志等级.</param>
    /// <param name="eventName">公开业务事件的稳定名称.</param>
    /// <param name="template">原结构化模板.</param>
    private static void AssertEvent(CapturedLog entry, LogLevel level, string eventName, string template)
    {
        Assert.Equal(level, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Equal(eventName, entry.EventId.Name);
        Assert.Equal(template, entry.Fields["{OriginalFormat}"]);
        Assert.Null(entry.Exception);
    }

    /// <summary>保存提交给 ILogger 的模板, 格式化文本和结构化值.</summary>
    private sealed record CapturedLog(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Fields, Exception? Exception);

    /// <summary>若封装遗漏等级判断或原始异常脱敏, 立即使测试失败.</summary>
    private sealed class StrictLogger : ILogger
    {
        public LogLevel Minimum { get; set; } = LogLevel.Debug;
        public List<CapturedLog> Events { get; } = [];

        /// <summary>本测试仅捕获事件, 不模拟外部关联范围.</summary>
        /// <param name="state">日志范围的关联状态.</param>
        /// <typeparam name="TState">范围状态类型.</typeparam>
        /// <returns>无需释放资源.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>每次读取当前最低等级, 模拟关于页的即时切换.</summary>
        /// <param name="logLevel">拟记录的等级.</param>
        /// <returns>当前等级是否启用.</returns>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= Minimum;

        /// <summary>拒绝过滤事件或原始异常, 捕获框架实际收到的结构化数据.</summary>
        /// <param name="logLevel">事件等级.</param>
        /// <param name="eventId">事件身份.</param>
        /// <param name="state">源生成器传递的模板与字段.</param>
        /// <param name="exception">必须为空的原始异常槽.</param>
        /// <param name="formatter">日志提供器使用的格式化委托.</param>
        /// <typeparam name="TState">日志状态类型.</typeparam>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.True(IsEnabled(logLevel), "A disabled event reached ILogger.Log");
            Assert.Null(exception);
            var fields = Assert.IsType<IEnumerable<KeyValuePair<string, object?>>>(state, exactMatch: false).ToDictionary(item => item.Key, item => item.Value);
            Events.Add(new(logLevel, eventId, formatter(state, exception), fields, exception));
        }
    }

    /// <summary>任何正文, 数据或字符串化访问均失败, 证明日志只提取安全字段.</summary>
    private sealed class PrivateException(Exception? inner = null) : Exception("private-secret-token", inner)
    {
        public override string Message => throw new InvalidOperationException("Private exception Message was read");
        public override IDictionary Data => throw new InvalidOperationException("Private exception Data was read");

        /// <summary>禁止日志通过字符串化间接读取异常正文.</summary>
        /// <returns>始终抛出异常, 该路径不应执行.</returns>
        /// <exception cref="InvalidOperationException">日志尝试访问原始异常.</exception>
        public override string ToString() => throw new InvalidOperationException("Private exception ToString was read");
    }
}
