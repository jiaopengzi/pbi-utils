// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\WindowsNetworkIdentityTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实原生网络身份范围, 恢复和安全日志, 不向任何目标验证假账号.

using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class WindowsNetworkIdentityTests
{
    /// <summary>NEW_CREDENTIALS 克隆本机身份, 回调确实处于模拟上下文, 成功和失败均恢复调用方.</summary>
    [Fact]
    public void NativeOutboundIdentityIsAppliedAndRestoredWithoutNetworkLogin()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-network-identity-" + Guid.NewGuid().ToString("N"));
        var account = Environment.MachineName + "\\pbi-" + Guid.NewGuid().ToString("N")[..12];
        const string fakePassword = "not-a-real-password";
        using var secret = new SecureString(); foreach (var character in fakePassword) secret.AppendChar(character);
        using var credential = new RemoteWindowsCredential(account, secret);
        using var original = WindowsIdentity.GetCurrent();
        var beforeLevel = original.ImpersonationLevel;
        try
        {
            using (var runtime = new LoggingRuntime(root, "test"))
            {
                new LoggingService(runtime).Save("Debug", default);
                var logger = runtime.Factory.CreateLogger("NetworkIdentityTest");
                var value = WindowsNetworkIdentity.Run(credential, logger, () =>
                {
                    using var inside = WindowsIdentity.GetCurrent(true);
                    Assert.NotNull(inside);
                    Assert.Equal(original.User, inside.User);
                    Assert.Equal(TokenImpersonationLevel.Impersonation, inside.ImpersonationLevel);
                    return "scope-applied";
                });
                Assert.Equal("scope-applied", value);
                using (var restored = WindowsIdentity.GetCurrent()) Assert.Equal(beforeLevel, restored.ImpersonationLevel);
                var failure = Assert.Throws<AppException>(() => WindowsNetworkIdentity.Run<bool>(credential, logger,
                    () => throw new AppException("TEST_FAILURE", "测试范围失败")));
                Assert.Equal("TEST_FAILURE", failure.Code);
                using (var restored = WindowsIdentity.GetCurrent()) Assert.Equal(beforeLevel, restored.ImpersonationLevel);
            }
            var text = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines));
            Assert.Contains("OutboundContextApplied", text);
            Assert.Contains("CallerContextRestored", text);
            Assert.DoesNotContain(fakePassword, text);
            Assert.DoesNotContain(account, text);
            Assert.DoesNotContain("pbi-" + account.Split("pbi-")[1], text);
            var events = text.Split('\n').Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(2, events.Count(e => e.TryGetProperty("Stage", out var stage) && stage.GetString() == "CallerContextRestored"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
