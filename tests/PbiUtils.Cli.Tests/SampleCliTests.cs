// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\SampleCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实 CLI 跨进程验证示例全量、增量、统计、清空和错误参数协议.

using System.Globalization;
using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>只在本测试创建的临时目录运行实际生成服务.</summary>
[Collection("CLI User Data")]
public sealed class SampleCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-sample-cli-" + Guid.NewGuid().ToString("N"));
    /// <summary>准备中文路径, 覆盖 JSON 之外的 stderr 进度协议.</summary>
    public SampleCliTests() => Directory.CreateDirectory(root);
    /// <summary>无 shell 的产品跨进程调用.</summary>
    private static Task<(int Code, string Out, string Error)> Cli(params string[] args) => CliTests.Run("dotnet", [typeof(PbiUtils.Cli.Program).Assembly.Location, .. args]);

    /// <summary>全量与增量共用服务, 输出一个 JSON, 未确认不能覆盖或清空.</summary>
    [Fact]
    public async Task GeneratesAppendsInspectsAndClearsActualFiles()
    {
        var output = Path.Combine(root, "中文示例");
        var request = Path.Combine(root, "request.json");
        WriteOptions(request, "2024-01-01", "2024-05-01");
        var (Code, Out, Error) = await Cli("sample", "generate", "--output", output, "--request", request, "--json");
        Assert.Equal(0, Code);
        using var generated = JsonDocument.Parse(Out);
        Assert.Equal("sample generate", generated.RootElement.GetProperty("command").GetString());
        Assert.Equal(11, generated.RootElement.GetProperty("data").GetProperty("state").GetProperty("tables").GetArrayLength());
        Assert.Contains("sampleProgress", Error);
        Assert.True(File.Exists(Path.Combine(output, "pbip", "demo.pbip")));
        var overwrite = await Cli("sample", "generate", "--output", output, "--request", request, "--json");
        Assert.Equal(3, overwrite.Code); Assert.Contains("SAMPLE_OVERWRITE_REQUIRED", overwrite.Out);
        var last = generated.RootElement.GetProperty("data").GetProperty("state").GetProperty("lastFactDate").GetString()!;
        var start = DateOnly.ParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1);
        WriteOptions(request, start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), start.AddDays(65).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var append = await Cli("sample", "append", "--output", output, "--request", request, "--json");
        Assert.Equal(0, append.Code);
        using var appended = JsonDocument.Parse(append.Out);
        Assert.True(appended.RootElement.GetProperty("ok").GetBoolean());
        var status = await Cli("sample", "inspect", "--output", output, "--json");
        Assert.Equal(0, status.Code); Assert.Empty(status.Error);
        using var inspected = JsonDocument.Parse(status.Out);
        Assert.True(inspected.RootElement.GetProperty("data").GetProperty("hasData").GetBoolean());
        File.WriteAllText(Path.Combine(output, "keep.txt"), "保留");
        var noApproval = await Cli("sample", "clear", "--output", output, "--json");
        Assert.Equal(2, noApproval.Code); Assert.True(Directory.Exists(Path.Combine(output, "pbip")));
        var cleared = await Cli("sample", "clear", "--output", output, "--confirm", "--json");
        Assert.Equal(0, cleared.Code);
        Assert.False(Directory.Exists(Path.Combine(output, "pbip")));
        Assert.Equal("保留", File.ReadAllText(Path.Combine(output, "keep.txt")));
    }

    /// <summary>参数重复、未知字段、旧初始化命令和未授权目录都返回单个失败文档.</summary>
    [Theory]
    [InlineData("{\"locale\":\"zh-cn\",\"locale\":\"en-us\"}")]
    [InlineData("{\"path\":\"unexpected\"}")]
    [InlineData("{\"Locale\":\"zh-cn\"}")]
    public async Task RejectsMalformedOptionsAndRemovedInitialization(string invalid)
    {
        var request = Path.Combine(root, "invalid.json");
        File.WriteAllText(request, invalid);
        var (Code, Out, _) = await Cli("sample", "generate", "--output", Path.Combine(root, "out"), "--request", request, "--json");
        Assert.Equal(2, Code);
        using var failed = JsonDocument.Parse(Out);
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        var old = await Cli("init", "plan", "--request", request, "--json");
        Assert.Equal(2, old.Code); Assert.Contains("INVALID_ARGUMENT", old.Out);
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    /// <summary>写入确定种子的业务参数, 不包含目录或机器环境.</summary>
    private static void WriteOptions(string request, string startDate, string endDate) =>
        File.WriteAllText(request, JsonSerializer.Serialize(new { locale = "zh-cn", productCount = 3, storeCount = 3, inventoryCycle = 5, startDate, endDate, seed = 42 }));
    /// <summary>清理自己创建且无外部输入的临时目录.</summary>
    public void Dispose() => Directory.Delete(root, true);
}
