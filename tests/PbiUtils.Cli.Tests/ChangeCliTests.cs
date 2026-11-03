// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ChangeCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实 CLI 跨进程预览、精确摘要批准和副本防覆盖验收.

using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>仅在自己创建的临时目录构造夹具, 不访问用户 PBIP.</summary>
[Collection("CLI User Data")]
public sealed class ChangeCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-cli", Guid.NewGuid().ToString("N"));
    /// <summary>带中文路径的内容契约夹具, 不冒充 Desktop 黄金模板.</summary>
    public ChangeCliTests()
    {
        Write("source/测试.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Write("source/report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        Write("source/report/definition/report.json", "{}");
        Write("source/report/definition/pages/pages.json", """{"pageOrder":[]}""");
        Write("source/model/definition.pbism", """{"version":"1.0"}""");
        Write("source/model/model.bim", """{"name":"test","compatibilityLevel":1606,"model":{"culture":"en-US","tables":[{"name":"销售","description":"原说明"}]}}""");
        Write("request.json", JsonSerializer.Serialize(new { projectPath = Path.Combine(root, "source/测试.pbip"), outputPath = Path.Combine(root, "copy"), operation = "table.set", objectName = "销售", property = "description", value = "副本说明" }));
    }
    private void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
    }
    private static Task<(int Code, string Out, string Error)> Cli(params string[] args) => CliTests.Run("dotnet", [typeof(PbiUtils.Cli.Program).Assembly.Location, .. args]);

    /// <summary>计划跨进程重建稳定, 批准错误不发布, 成功后拒绝覆盖与重放.</summary>
    [Fact]
    public async Task PreviewAndApplyRequireSameDigestAndDoNotOverwriteSource()
    {
        var file = Path.Combine(root, "request.json");
        var source = File.ReadAllBytes(Path.Combine(root, "source/model/model.bim"));
        var (Code, Out, Error) = await Cli("change", "plan", "--request", file, "--json");
        Assert.Equal(0, Code); Assert.Empty(Error);
        using var json = JsonDocument.Parse(Out);
        var digest = json.RootElement.GetProperty("data").GetProperty("digest").GetString()!;
        Assert.False(Directory.Exists(Path.Combine(root, "copy")));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*"));
        var wrong = await Cli("change", "apply", "--request", file, "--approve", "forged", "--json");
        Assert.Equal(3, wrong.Code); Assert.Contains("APPROVAL_MISMATCH", wrong.Out);
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*"));
        var applied = await Cli("change", "apply", "--request", file, "--approve", digest, "--json");
        Assert.Equal(0, applied.Code); Assert.Contains("files_created", applied.Out);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(root, "source/model/model.bim")));
        var copy = await Cli("model", "inspect", "--project", Path.Combine(root, "copy/测试.pbip"), "--json");
        Assert.Equal(0, copy.Code);
        using var copied = JsonDocument.Parse(copy.Out);
        Assert.Equal("副本说明", copied.RootElement.GetProperty("data").GetProperty("model").GetProperty("tables")[0].GetProperty("description").GetString());
        var replay = await Cli("change", "apply", "--request", file, "--approve", digest, "--json");
        Assert.Equal(3, replay.Code); Assert.Contains("OUTPUT_CONFLICT", replay.Out);
    }

    /// <summary>未知字段、重复字段和缺失确认都明确拒绝, stdout仍是唯一JSON文档.</summary>
    [Fact]
    public async Task RejectsMalformedRequestAndMissingApproval()
    {
        var file = Path.Combine(root, "request.json");
        var (Code, _, _) = await Cli("change", "apply", "--request", file, "--json");
        Assert.Equal(2, Code);
        Write("request.json", """{"operation":"table.set","operation":"udf.set"}""");
        var duplicate = await Cli("change", "plan", "--request", file, "--json");
        Assert.Equal(2, duplicate.Code);
        using var json = JsonDocument.Parse(duplicate.Out);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>测试目录是本实例创建的普通目录; 不枚举到根外.</summary>
    public void Dispose() => Directory.Delete(root, true);
}
