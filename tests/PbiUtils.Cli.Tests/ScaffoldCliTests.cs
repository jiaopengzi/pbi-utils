// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ScaffoldCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实 CLI 进程验证模板目录, 跨进程精确预览与新目录创建.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>输出使用测试独占目录, 回执只清理本测试登记的精确摘要.</summary>
[Collection("CLI User Data")]
public sealed class ScaffoldCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-cli-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> digests = [];

    /// <summary>准备实际输出父目录.</summary>
    public ScaffoldCliTests() => Directory.CreateDirectory(root);

    /// <summary>内置模板来自发布资源, 新命令也保持帮助优先.</summary>
    [Fact]
    public async Task ListsCurrentBuiltInTemplateAndPreservesHelpPrecedence()
    {
        var (Code, Out, Error) = await Cli("new", "templates", "--json");
        Assert.Equal(0, Code);
        using var document = JsonDocument.Parse(Out);
        var templates = document.RootElement.GetProperty("data").GetProperty("templates").EnumerateArray().ToArray();
        Assert.Equal(["builtin:blank"], templates.Where(item => item.GetProperty("source").GetString() == "builtin")
            .Select(item => item.GetProperty("id").GetString()));
        var help = await Cli("new", "create", "--json", "--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("pbi-utils new plan", help.Out);
    }

    /// <summary>真实子进程没有 Git 时仍返回可用模板和明确安装原因.</summary>
    [Fact]
    public async Task MissingGitIsExposedAlongsideReadOnlyTemplates()
    {
        var (Code, Out, Error) = await CliWithoutGit("new", "templates", "--json");
        Assert.Equal(0, Code);
        using var document = JsonDocument.Parse(Out);
        var data = document.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("git").GetProperty("available").GetBoolean());
        Assert.Equal("GIT_NOT_FOUND", data.GetProperty("git").GetProperty("reason").GetString());
        Assert.Contains(data.GetProperty("templates").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "builtin:blank");
    }

    /// <summary>CLI 显式 Git 请求原样冻结, 缺 Git 仍创建一次且回执提供手动处理提示.</summary>
    [Fact]
    public async Task MissingGitPreservesExplicitIntentAndCreatesProjectOnce()
    {
        var (Code, Out, Error) = await CliWithoutGit("new", "templates", "--json");
        using var catalog = JsonDocument.Parse(Out);
        var template = catalog.RootElement.GetProperty("data").GetProperty("templates").EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == "builtin:blank");
        var request = Path.Combine(root, "git-request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            templateId = "builtin:blank",
            templateBaseline = template.GetProperty("baseline").GetString(),
            projectName = "GitMissing",
            locale = "zh-cn"
        }));
        var output = Path.Combine(root, "GitMissing");
        var planned = await CliWithoutGit("new", "plan", "--request", request, "--output", output, "--git", "--json");
        Assert.Equal(0, planned.Code);
        using var plan = JsonDocument.Parse(planned.Out);
        var data = plan.RootElement.GetProperty("data");
        var digest = data.GetProperty("digest").GetString()!;
        digests.Add(digest);
        Assert.True(data.GetProperty("request").GetProperty("gitInitialize").GetBoolean());
        Assert.Contains(data.GetProperty("warnings").EnumerateArray(), warning => warning.GetString() == "GIT_NOT_FOUND");
        var created = await CliWithoutGit("new", "create", "--request", request, "--output", output, "--approve", digest, "--git", "--json");
        Assert.Equal(0, created.Code);
        using var result = JsonDocument.Parse(created.Out);
        var facts = result.RootElement.GetProperty("data");
        Assert.True(facts.GetProperty("projectCreated").GetBoolean());
        Assert.True(facts.GetProperty("verified").GetBoolean());
        Assert.False(facts.GetProperty("gitInitialized").GetBoolean());
        Assert.Equal("GIT_NOT_FOUND", facts.GetProperty("errorCode").GetString());
        Assert.Contains(facts.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("不要重复创建", StringComparison.Ordinal));
        var project = facts.GetProperty("projectPath").GetString()!;
        var modified = File.GetLastWriteTimeUtc(project);
        var replay = await CliWithoutGit("new", "create", "--request", request, "--output", output, "--approve", digest, "--git", "--json");
        Assert.Equal(0, replay.Code);
        using var replayed = JsonDocument.Parse(replay.Out);
        Assert.Equal(facts.GetProperty("planId").GetString(), replayed.RootElement.GetProperty("data").GetProperty("planId").GetString());
        Assert.Equal(modified, File.GetLastWriteTimeUtc(project));
        Assert.False(Directory.Exists(Path.Combine(output, ".git")));
        Assert.False(Directory.Exists(Path.Combine(output, "pbip", ".git")));
        Assert.Equal("**/.pbi/localSettings.json\n**/.pbi/cache.abf\n", await File.ReadAllTextAsync(Path.Combine(output, "pbip", ".gitignore")));
    }

    /// <summary>未批准只准备 staging, 下个进程匹配摘要才发布, 重复调用不重放模型组合.</summary>
    [Fact]
    public async Task PlansAndCreatesAcrossProcessesWithExactApproval()
    {
        var (Code, Out, Error) = await Cli("new", "templates", "--json");
        Assert.Equal(0, Code);
        using var catalog = JsonDocument.Parse(Out);
        var template = catalog.RootElement.GetProperty("data").GetProperty("templates").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "builtin:blank");
        var request = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            templateId = "builtin:blank",
            templateBaseline = template.GetProperty("baseline").GetString(),
            projectName = "新工程",
            locale = "zh-cn"
        }));
        var output = Path.Combine(root, "新工程");
        var planned = await Cli("new", "plan", "--request", request, "--output", output, "--json");
        Assert.Equal(0, planned.Code);
        using var plan = JsonDocument.Parse(planned.Out);
        var digest = plan.RootElement.GetProperty("data").GetProperty("digest").GetString()!;
        digests.Add(digest);
        Assert.False(Directory.Exists(output));
        var mismatch = await Cli("new", "create", "--request", request, "--output", output, "--approve", digest, "--git", "--json");
        Assert.Equal(3, mismatch.Code);
        Assert.False(Directory.Exists(output));
        var created = await Cli("new", "create", "--request", request, "--output", output, "--approve", digest, "--json");
        Assert.Equal(0, created.Code);
        using var result = JsonDocument.Parse(created.Out);
        Assert.True(result.RootElement.GetProperty("data").GetProperty("projectCreated").GetBoolean());
        Assert.True(result.RootElement.GetProperty("data").GetProperty("verified").GetBoolean());
        var projectPath = result.RootElement.GetProperty("data").GetProperty("projectPath").GetString()!;
        Assert.Equal(Path.Combine(output, "pbip", "新工程.pbip"), projectPath);
        Assert.True(File.Exists(projectPath));
        Assert.True(Directory.Exists(Path.Combine(output, "pbip", "新工程.Report")));
        Assert.True(Directory.Exists(Path.Combine(output, "pbip", "新工程.SemanticModel")));
        Assert.False(File.Exists(Path.Combine(output, "pbi-utils.scaffold.json")));
        var replay = await Cli("new", "create", "--request", request, "--output", output, "--approve", digest, "--json");
        Assert.Equal(0, replay.Code);
    }

    /// <summary>未知、重复及不适用参数不能触发模板处理或写入.</summary>
    [Theory]
    [InlineData("new", "templates", "--output", "unexpected", "--json")]
    [InlineData("new", "templates", "--json", "--json")]
    [InlineData("new", "create", "--json")]
    [InlineData("new", "plan", "--approve", "x", "--json")]
    [InlineData("udf", "catalog", "rollback", "--snapshot", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--revision", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "--json")]
    [InlineData("udf", "catalog", "rollback", "--unexpected", "x", "--confirm", "--json")]
    [InlineData("udf", "catalog", "history", "--json")]
    [InlineData("udf", "catalog", "rollback", "--snapshot", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--revision", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "--confirm", "--json")]
    [InlineData("udf", "catalog", "history", "--snapshot", "x", "--json")]
    [InlineData("udf", "catalog", "history", "--confirm", "--json")]
    public async Task RejectsInvalidArguments(params string[] arguments)
    {
        var (Code, Out, _) = await Cli(arguments);
        Assert.Equal(2, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>包版本参数必须进入实际严格业务解析, 不被产品版本元命令截获.</summary>
    [Theory]
    [InlineData("0.1.8")]
    [InlineData(null)]
    public async Task PackageVersionOptionIsNotProductVersion(string? version)
    {
        string[] arguments = version is null
            ? ["udf", "catalog", "show", "--package", "!invalid", "--version", "--json"]
            : ["udf", "catalog", "show", "--package", "!invalid", "--version", version, "--json"];
        var (code, output, _) = await Cli(arguments);
        Assert.NotEqual(0, code);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>缺失必需布尔值在请求读取阶段拒绝, 不得到达批准或文件操作.</summary>
    [Theory]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"description\":\"\",\"displayFolder\":\"\"}]")]
    [InlineData("calendar", "{\"kind\":\"simple\",\"startYear\":2026,\"endYear\":2026,\"overrides\":[{\"date\":\"2026-10-19\",\"name\":\"Missing boolean\"}]}")]
    public async Task RejectsMissingRequiredRequestFields(string section, string value)
    {
        var (Code, Out, _) = await ParseRequestThroughCreate(section, value);
        Assert.Equal(2, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "新工程")));
    }

    /// <summary>合法可选字段省略和明确 false 能通过读取, 随后仍被无效批准挡住而不写用户计划.</summary>
    [Theory]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"enabled\":false,\"description\":\"\",\"displayFolder\":\"\"}]")]
    [InlineData("calendar", "{\"kind\":\"simple\",\"startYear\":2026,\"endYear\":2026,\"overrides\":[{\"date\":\"2026-10-19\",\"isWorkday\":false,\"name\":\"Explicit false\"}]}")]
    [InlineData("udfs", "[{\"source\":\"daxlib\",\"packageId\":\"test.package\",\"functions\":[\"Test.Function\"]}]")]
    public async Task AcceptsOptionalFieldsAndExplicitFalseBeforeApproval(string section, string value)
    {
        var (Code, Out, _) = await ParseRequestThroughCreate(section, value);
        Assert.Equal(3, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.Equal("INVALID_SCAFFOLD_PLAN", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "新工程")));
    }

    /// <summary>通过真实 CLI 读取请求, 使用无效批准在环境访问前停止, 避免真实下载或用户计划写入.</summary>
    private async Task<(int Code, string Out, string Error)> ParseRequestThroughCreate(string section, string value)
    {
        using var parsed = JsonDocument.Parse(value);
        var request = Path.Combine(root, "strict-request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["templateId"] = "builtin:blank",
            ["templateBaseline"] = new string('a', 64),
            ["projectName"] = "新工程",
            ["locale"] = "zh-cn",
            [section] = parsed.RootElement
        }));
        return await Cli("new", "create", "--request", request, "--output", Path.Combine(root, "新工程"), "--approve", "invalid", "--json");
    }

    /// <summary>运行真正的控制台程序, 进度与业务 stdout 分离.</summary>
    private static Task<(int Code, string Out, string Error)> Cli(params string[] arguments) =>
        CliTests.Run("dotnet", [typeof(PbiUtils.Cli.Program).Assembly.Location, .. arguments]);

    /// <summary>只清空自有 CLI 子进程的 PATH, 不修改测试宿主或系统 Git 环境.</summary>
    private async Task<(int Code, string Out, string Error)> CliWithoutGit(params string[] arguments)
    {
        using var receipts = new CliReceiptScope(arguments, root);
        var executable = CliTests.Executable;
        var start = new ProcessStartInfo(executable)
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
        start.Environment["PATH"] = root;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>只删除输出身份匹配本测试根的回执及测试独占临时目录.</summary>
    public void Dispose()
    {
        var plans = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils", "scaffold", "plans");
        foreach (var digest in digests)
        {
            var receipt = Path.Combine(plans, digest + ".json");
            if (!File.Exists(receipt)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
            var output = document.RootElement.GetProperty("plan").GetProperty("outputPath").GetString()!;
            if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(receipt);
            var lease = Path.Combine(plans, digest + ".lock");
            if (File.Exists(lease)) File.Delete(lease);
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
