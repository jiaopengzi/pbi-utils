// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\CliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 CLI 进程验证参数, 文本转义, JSON 输出与退出码.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>真实产品进程的 JSON、转义与退出码测试.</summary>
public sealed class CliTests
{
    /// <summary>正式apphost身份, 与测试输出内真实业务后台属于同一安装副本.</summary>
    public static string Executable => Path.Combine(Path.GetDirectoryName(typeof(PbiUtils.Cli.Program).Assembly.Location)!, "pbi-utils.exe");
    /// <summary>已移除的演示命令必须返回参数错误, 不能继续执行旧问候或模拟工具.</summary>
    /// <param name="arguments">历史演示命令参数.</param>
    [Theory]
    [InlineData("hello", "--json")]
    [InlineData("agent", "hello", "--provider", "mock", "--json")]
    public async Task RemovedDemoCommandsAreRejected(params string[] arguments)
    {
        var (code, output, _) = await Cli(arguments);
        Assert.Equal(2, code);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>无 shell 拼接启动进程, 按 UTF-8 分离读取输出并限制执行时间.</summary>
    /// <param name="file">可执行文件.</param>
    /// <param name="arguments">原样传递的参数.</param>
    /// <returns>退出码与两个输出流.</returns>
    public static Task<(int Code, string Out, string Error)> Run(string file, params string[] arguments) =>
        RunWithTimeout(file, TimeSpan.FromSeconds(30), arguments);

    /// <summary>为包含多个独立场景的脚本合集使用明确总预算, 普通 CLI 仍由 Run 限制为 30 秒.</summary>
    /// <param name="file">当前测试明确启动并负责清理的可执行文件.</param>
    /// <param name="timeout">覆盖整个子进程的测试预算, 不改变产品内部超时.</param>
    /// <param name="arguments">原样传递的受信测试参数.</param>
    /// <returns>子进程退出码和两个独立输出流.</returns>
    /// <exception cref="TimeoutException">测试总预算耗尽, 仅结束本次启动的准确进程树.</exception>
    public static async Task<(int Code, string Out, string Error)> RunWithTimeout(string file, TimeSpan timeout, params string[] arguments)
    {
        if (arguments.FirstOrDefault() == typeof(PbiUtils.Cli.Program).Assembly.Location)
        {
            file = Executable;
            arguments = arguments[1..];
        }
        using var receipts = file == Executable ? new CliReceiptScope(arguments, Environment.CurrentDirectory) : null;
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>运行已复制到测试输出的 CLI 程序.</summary>
    private static Task<(int Code, string Out, string Error)> Cli(params string[] arguments) => Run("dotnet", [typeof(PbiUtils.Cli.Program).Assembly.Location, .. arguments]);

    /// <summary>复杂文本安全传递, stdout 恰好一个 JSON 文档.</summary>
    [Fact]
    public async Task UnicodeAndScriptTextAreData()
    {
        var command = "世界 \"朋友\"; $(echo secret) <script>alert(1)</script>";
        var (Code, Out, Error) = await Cli(command, "--json");
        Assert.Equal(2, Code);
        Assert.Empty(Error);
        using var json = JsonDocument.Parse(Out);
        Assert.Equal(command, json.RootElement.GetProperty("command").GetString());
        Assert.Equal("INVALID_ARGUMENT", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>参数错误采用统一错误包并返回 2.</summary>
    /// <param name="arguments">无效命令数组.</param>
    [Theory]
    [InlineData("doctor", "unexpected", "--json")]
    [InlineData("doctor", "--provider", "online", "--json")]
    [InlineData("doctor", "--name", "x", "--json")]
    [InlineData("new", "templates", "--json", "--json")]
    [InlineData("unknown", "--json")]
    [InlineData("doctor", "--json", "--json")]
    public async Task InvalidArguments(params string[] arguments)
    {
        var (Code, Out, _) = await Cli(arguments);
        Assert.Equal(2, Code);
        using var json = JsonDocument.Parse(Out);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>元命令优先且不产生业务事件, help 高于 version.</summary>
    [Fact]
    public async Task MetaPrecedence()
    {
        var (Code, Out, Error) = await Cli("doctor", "--name", "", "--json", "--version", "--help");
        Assert.Equal(0, Code);
        Assert.Contains("pbi-utils doctor", Out);
        Assert.Contains("pbi-utils new templates", Out);
        Assert.DoesNotContain("pbi-utils hello", Out);
        Assert.DoesNotContain("pbi-utils agent hello", Out);
        Assert.Empty(Error);
        var version = await Cli("unknown", "--json", "--version");
        Assert.Equal(PbiUtils.Core.Product.Version, version.Out.Trim());
        Assert.Equal(0, version.Code);
    }

    /// <summary>只读 doctor 不依赖已打开的 Power BI, stdout 保持单一 JSON 文档.</summary>
    [Fact]
    public async Task DoctorReturnsRuntimeAndProductVersion()
    {
        var (Code, Out, Error) = await Cli("doctor", "--json");
        Assert.Equal(0, Code);
        Assert.Empty(Error);
        using var json = JsonDocument.Parse(Out);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("doctor", json.RootElement.GetProperty("command").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(PbiUtils.Core.Product.Version, data.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("environment").GetProperty("os").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("environment").GetProperty("runtime").GetString()));
    }
}
