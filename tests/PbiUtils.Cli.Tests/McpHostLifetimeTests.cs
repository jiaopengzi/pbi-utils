// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\McpHostLifetimeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用正式 CLI 与共享 Host 验证启动客户端标准流和后台生命周期彼此独立.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>仅调用读取工具, 不连接模型、修改文件或结束共享后台.</summary>
[Collection("CLI User Data")]
public sealed class McpHostLifetimeTests
{
    /// <summary>先拉起 Host 的客户端退出后两个输出流必须 EOF; 同伴仍可调用, 最后同伴退出才结束后台.</summary>
    [Fact]
    public async Task LauncherStdioClosesWhilePeerKeepsHostAlive()
    {
        await WaitForPreviousTestHostExitAsync();
        var started = DateTime.UtcNow;
        await using var launcher = new McpWireClient();
        var launcherSession = await SessionAsync(launcher);
        using var host = Process.GetProcessById(launcherSession.GetProperty("processId").GetInt32());
        _ = host.SafeHandle;
        Assert.True(host.StartTime.ToUniversalTime() >= started, "必须由本轮第一个客户端启动新的共享后台");
        try
        {
            // 嵌套生命周期使断言失败后先关闭 peer, 再释放 launcher, 红测也不会等待仍存活的同伴.
            await using var peer = new McpWireClient();
            var peerSession = await SessionAsync(peer);
            Assert.Equal(host.Id, peerSession.GetProperty("processId").GetInt32());
            Assert.NotEqual(launcherSession.GetProperty("id").GetString(), peerSession.GetProperty("id").GetString());

            Assert.Equal(0, await launcher.CloseAsync());
            var streams = await Task.WhenAll(launcher.ReadRemainingOutputAsync(TimeSpan.FromSeconds(5)), launcher.ReadErrorsAsync(TimeSpan.FromSeconds(5)));
            Assert.All(streams, text => Assert.Empty(text));
            Assert.False(host.HasExited);
            var info = await peer.RequestAsync("tools/call", new { name = "desktop_app_info", arguments = new { parameters = new { } } });
            Assert.False(info.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("result").GetProperty("structuredContent").GetProperty("version").GetString()));
            Assert.Equal(0, await peer.CloseAsync());
        }
        finally
        {
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, host.ExitCode);
        }
    }

    /// <summary>读取本客户端唯一后台上下文, 返回会话身份; 工具错误或会话歧义使测试失败.</summary>
    /// <param name="client">本测试创建的只读客户端.</param>
    /// <returns>带 processId 和 id 的唯一会话对象.</returns>
    private static async Task<JsonElement> SessionAsync(McpWireClient client)
    {
        var response = await client.RequestAsync("tools/call", new { name = "desktop_sessions", arguments = new { } });
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        return Assert.Single(response.GetProperty("result").GetProperty("structuredContent").GetProperty("sessions").EnumerateArray());
    }

    /// <summary>只等待同测试安装上轮后台自然排空, 保证本轮观察冷启动; 超时明确失败且不结束任何进程.</summary>
    private static async Task WaitForPreviousTestHostExitAsync()
    {
        var executable = Path.Combine(Path.GetDirectoryName(CliTests.Executable)!, "PbiUtils.Host.exe");
        var processes = Process.GetProcessesByName("PbiUtils.Host");
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited) continue;
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
