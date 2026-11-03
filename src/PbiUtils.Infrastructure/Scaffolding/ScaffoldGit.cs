// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldGit.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共用 Git 探测与禁用模板和 hook 的有界初始化.

using PbiUtils.Infrastructure.Storage;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>只运行宿主指定的 Git 程序, 不接受网页的程序或命令参数.</summary>
public sealed class ScaffoldGit(string executable = "git", TimeSpan? timeout = null)
{
    private static readonly string[] RepositoryEnvironment =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_INDEX_FILE", "GIT_ALTERNATE_OBJECT_DIRECTORIES"];
    private readonly TimeSpan processTimeout = ValidateTimeout(timeout ?? TimeSpan.FromSeconds(5));

    /// <summary>探测 Git 版本, 缺失和失败以安全原因返回, 取消仍向上传播.</summary>
    public Task<GitCapability> ProbeAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            var result = await GitProcess.RunAsync(Start(["--version"]), processTimeout, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && result.StandardOutput.StartsWith("git version ", StringComparison.Ordinal)
                ? new GitCapability(true)
                : new GitCapability(false, "GIT_PROBE_FAILED");
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return new(false, "GIT_NOT_FOUND"); }
        catch (TimeoutException) { return new(false, "GIT_PROBE_TIMEOUT"); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(false, "GIT_PROBE_FAILED"); }
    }, cancellationToken);

    /// <summary>禁用模板及 hook 初始化新工程, 不暂存或提交文件.</summary>
    public Task<bool> InitializeAsync(string output, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            UserDataStore.Check(output);
            UserDataStore.Check(Path.Combine(output, ".git"));
            if (!Directory.Exists(output)) return false;
            var result = await GitProcess.RunAsync(Start(["-c", "core.hooksPath=NUL", "init", "--quiet", "--template=", "--", output]), processTimeout, cancellationToken).ConfigureAwait(false);
            UserDataStore.Check(Path.Combine(output, ".git"));
            return result.ExitCode == 0 && Directory.Exists(Path.Combine(output, ".git"));
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or AppException)
        { return false; }
    }, cancellationToken);

    /// <summary>构造不受继承仓库路径重定向的 Git 子进程, 不弹窗口或请求交互凭据.</summary>
    /// <param name="arguments">宿主固定的直接参数数组, 不经 shell 拼接.</param>
    /// <returns>仅清除仓库定位环境的启动配置, 不修改父进程或用户 Git 设置.</returns>
    private ProcessStartInfo Start(string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // Git 定位环境优先于 init 的目标参数, 必须阻止仓库或对象/索引落到 PBIP 目录外.
        foreach (var variable in RepositoryEnvironment) start.Environment.Remove(variable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return start;
    }

    /// <summary>禁止无限或过长等待, 宿主可选择零以上且最多30秒的预算.</summary>
    private static TimeSpan ValidateTimeout(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromSeconds(30)
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "Git 进程预算必须在0到30秒之间");
}

/// <summary>有界捕获标准输出和进程退出码, 不返回错误输出的环境细节.</summary>
internal sealed record GitProcessResult(int ExitCode, string StandardOutput);

/// <summary>Git 进程共用取消, 超时和输出排空边界.</summary>
internal static class GitProcess
{
    /// <summary>等待进程和管道共同终态, 取消或超时后结束自有进程树.</summary>
    internal static async Task<GitProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git 进程未启动");
        var stdout = DrainAsync(process.StandardOutput, 4096, bounded.Token);
        var stderr = DrainAsync(process.StandardError, 0, bounded.Token);
        var pending = Task.WhenAll(process.WaitForExitAsync(bounded.Token), stdout, stderr);
        try
        {
            await pending.WaitAsync(bounded.Token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch
        {
            var timedOut = bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            // 只结束本次直接启动的进程树, 不枚举或结束系统中其他 Git 进程.
            bounded.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            try { await pending.WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut) throw new TimeoutException("Git 进程超时");
            throw;
        }
    }

    /// <summary>始终消费两个管道, 仅保留限定字符数用于版本校验, 错误输出不外泄.</summary>
    private static async Task<string> DrainAsync(StreamReader stream, int maximumCharacters, CancellationToken cancellationToken)
    {
        StringBuilder captured = new();
        var buffer = new char[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = maximumCharacters - captured.Length;
            if (remaining > 0) captured.Append(buffer, 0, Math.Min(remaining, read));
        }
        return captured.ToString();
    }
}
