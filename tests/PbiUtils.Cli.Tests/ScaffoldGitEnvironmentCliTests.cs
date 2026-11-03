// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ScaffoldGitEnvironmentCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实 CLI 子进程验证 Git 定位环境变量不能把新建仓库或对象目录引向项目外部.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>环境只设置到测试自有 CLI 子进程, 外部目标和发布目录均在唯一临时树内.</summary>
[Collection("CLI User Data")]
public sealed class ScaffoldGitEnvironmentCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-git-env-cli-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Digest, string Output)> ownReceipts = [];
    private static readonly string[] GitLocationVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY"];

    /// <summary>继承的仓库, 工作树, 共享目录或对象目录设置不能改变新建 PBIP 仓库的固定位置.</summary>
    /// <param name="variable">仅在自有 CLI 子进程中设置的一个 Git 定位环境变量.</param>
    [Theory]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_OBJECT_DIRECTORY")]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("GIT_WORK_TREE")]
    public async Task InheritedGitLocationDoesNotWriteOutsidePbipRepository(string variable)
    {
        Directory.CreateDirectory(root);
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep external directory unchanged\n");
        var before = Inventory(external);
        var (catalogCode, catalogJson, _) = await CliAsync(variable, external, "new", "templates", "--json");
        Assert.Equal(0, catalogCode);
        using var catalog = JsonDocument.Parse(catalogJson);
        Assert.True(catalog.RootElement.GetProperty("data").GetProperty("git").GetProperty("available").GetBoolean());
        var template = Assert.Single(catalog.RootElement.GetProperty("data").GetProperty("templates").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "builtin:blank");
        const string projectName = "GitEnvironment";
        var request = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            templateId = "builtin:blank",
            templateBaseline = template.GetProperty("baseline").GetString(),
            projectName,
            locale = "zh-cn"
        }));
        var output = Path.Combine(root, projectName);
        var (planCode, planJson, _) = await CliAsync(variable, external, "new", "plan", "--request", request, "--output", output, "--git", "--json");
        Assert.Equal(0, planCode);
        using var plan = JsonDocument.Parse(planJson);
        var planned = plan.RootElement.GetProperty("data");
        var digest = planned.GetProperty("digest").GetString()!;
        ownReceipts.Add((digest, output));
        Assert.Equal(output, planned.GetProperty("outputPath").GetString());
        Assert.True(planned.GetProperty("request").GetProperty("gitInitialize").GetBoolean());
        var (createCode, createJson, _) = await CliAsync(variable, external, "new", "create", "--request", request,
            "--output", output, "--approve", digest, "--git", "--json");
        Assert.Equal(0, createCode);
        using var created = JsonDocument.Parse(createJson);
        var result = created.RootElement.GetProperty("data");
        Assert.True(result.GetProperty("projectCreated").GetBoolean());
        Assert.True(result.GetProperty("verified").GetBoolean());
        var after = Inventory(external);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
        Assert.Equal("keep external directory unchanged\n", await File.ReadAllTextAsync(sentinel));
        Assert.True(result.GetProperty("gitInitialized").GetBoolean());
        var repository = Path.Combine(output, "pbip", ".git");
        Assert.True(File.Exists(Path.Combine(repository, "HEAD")));
        Assert.True(Directory.Exists(Path.Combine(repository, "objects")));
        Assert.False(Directory.Exists(Path.Combine(output, ".git")));
        Assert.Equal([repository], Directory.GetDirectories(output, ".git", SearchOption.AllDirectories));
    }

    /// <summary>真实运行当前构建的 CLI, 清除四种其他定位变量后只设置本轮一个控制变量.</summary>
    /// <param name="variable">被测 Git 定位环境变量名称.</param>
    /// <param name="external">测试拥有的工程外目标目录.</param>
    /// <param name="arguments">直接传入 CLI 的参数列表, 不经 shell 拼接.</param>
    /// <returns>已排空子进程的退出码, 单文档 stdout 和诊断 stderr.</returns>
    /// <exception cref="TimeoutException">子进程未在预算内退出, 仅结束本测试创建的准确进程树.</exception>
    private async Task<(int Code, string Out, string Error)> CliAsync(string variable, string external, params string[] arguments)
    {
        using var receipts = new CliReceiptScope(arguments, root);
        var start = new ProcessStartInfo(CliTests.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in GitLocationVariables) start.Environment.Remove(name);
        start.Environment[variable] = external;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>记录文件内容和目录节点, 对象目录仅新增空的 info/pack 子目录也必须被发现.</summary>
    /// <param name="directory">测试拥有的外部目标目录.</param>
    /// <returns>目录节点和文件 SHA256 的规范相对路径清单.</returns>
    private static Dictionary<string, string> Inventory(string directory) => Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
            path => Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    /// <summary>只删除 output 与 digest 同时匹配的本轮持久回执, 然后清理独占临时树.</summary>
    public void Dispose()
    {
        var plans = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils", "scaffold", "plans");
        foreach (var (digest, output) in ownReceipts)
        {
            var receipt = Path.Combine(plans, digest + ".json");
            if (!File.Exists(receipt)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
            var plan = document.RootElement.GetProperty("plan");
            if (plan.GetProperty("digest").GetString() != digest
                || !string.Equals(plan.GetProperty("outputPath").GetString(), output, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(receipt);
            var lease = Path.Combine(plans, digest + ".lock");
            if (File.Exists(lease)) File.Delete(lease);
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
