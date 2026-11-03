// FilePath    : pbi-utils\src\PbiUtils.Host\Program.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 启动同用户按需业务后台, 不初始化 GUI/WebView2, 最后上下文排空后退出.

using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Hosting;

namespace PbiUtils.Host;

internal static class Program
{
    /// <summary>仅固定无参数入口, 不提供任意反射、文件执行或网络监听参数.</summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return 2;
        try
        {
            var root = RuntimeEnvironment.UserDataDirectory();
            using var logging = new LoggingRuntime(root, "host");
            logging.EnableDiagnostics();
            using var lifetime = BusinessLog.Begin(logging.Factory.CreateLogger("Host.Lifetime"), "Host.Run");
            try
            {
                await using var server = await BusinessHostServer.StartAsync(root,
                    (registration, peer, token, mcpConnections) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return BusinessContext.CreateAsync(registration, peer, root, logging, mcpConnections);
                    });
                await server.Completion;
                lifetime.Complete();
                return 0;
            }
            catch (Exception error)
            {
                lifetime.Fail(error, fatal: true);
                return 4;
            }
        }
        catch
        {
            // 日志初始化前仅输出固定诊断, 不泄漏路径、凭据或业务正文.
            await Console.Error.WriteLineAsync("pbi-utils 业务后台启动失败, 请检查安装和用户数据目录权限.");
            return 3;
        }
    }
}
