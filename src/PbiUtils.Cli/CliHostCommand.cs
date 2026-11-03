// FilePath    : pbi-utils\src\PbiUtils.Cli\CliHostCommand.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 普通CLI作为共享后台客户端, 保持stdout业务JSON和stderr进度.

using PbiUtils.Infrastructure.Diagnostics;
using System.Text.Json;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Hosting;

namespace PbiUtils.Cli;

/// <summary>不在客户端执行文件提交, 断线不会自动重放业务.</summary>
internal static class CliHostCommand
{
    /// <summary>传递固定参数和工作目录, 最后只排空本客户端上下文.</summary>
    /// <param name="args">独立参数数组.</param>
    /// <param name="token">提交前取消, 提交后等待后台确定结果.</param>
    /// <returns>原业务可序列化结果.</returns>
    internal static async Task<JsonElement> RunAsync(string[] args, CancellationToken token)
    {
        await using var client = await BusinessHostClient.ConnectAsync(RuntimeEnvironment.UserDataDirectory(),
            new BusinessHostClientOptions("cli", true), eventHandler: ProgressAsync, cancellationToken: token);
        return await client.Peer.InvokeAsync("cli.run", JsonSerializer.SerializeToElement(new { args, workingDirectory = Environment.CurrentDirectory }), token);
    }

    /// <summary>只接受后台固定阶段事件, 不向stdout输出控制消息.</summary>
    private static Task ProgressAsync(string name, JsonElement value, CancellationToken token)
    {
        if (name == "cli.progress") Console.Error.WriteLine(value.GetRawText());
        return Task.CompletedTask;
    }
}
