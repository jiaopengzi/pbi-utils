// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ScaffoldTemplateQueryCliTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实 CLI 验证当前模板查询可选字段和严格输入拒绝.

using System.Text.Json;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>使用无效批准在实际发布前停止, 仅写测试拥有的请求文件.</summary>
[Collection("CLI User Data")]
public sealed class ScaffoldTemplateQueryCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-query-cli-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前可选字段支持省略、显式 null、空集和标准选择, 随后由无效批准阻止创建.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"Hour24\",\"MQHY\",\"TypeDetails\"]")]
    [InlineData("[\"RefreshTime\"]")]
    [InlineData("[\"Hour24\",\"MQHY\",\"RefreshTime\",\"TypeDetails\"]")]
    public async Task OptionalQuerySelectionAcceptsCurrentRequestShapes(string? selection)
    {
        var (Code, Out, _) = await RunAsync(selection);
        Assert.Equal(3, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.Equal("INVALID_SCAFFOLD_PLAN", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
    }

    /// <summary>未知、空白、大小写和重复选择先于批准检查失败, 不触发模板准备或创建.</summary>
    [Theory]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"hour24\"]")]
    [InlineData("[\"Hour24\",\"Hour24\"]")]
    public async Task InvalidQuerySelectionFailsBeforeApproval(string selection)
    {
        var (Code, Out, _) = await RunAsync(selection);
        Assert.Equal(2, Code);
        using var document = JsonDocument.Parse(Out);
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
    }

    /// <summary>生成字段唯一的真实请求并运行正式 CLI, 无效批准避免操作用户计划存储.</summary>
    /// <param name="selection">查询段 JSON, null 表示完全省略字段.</param>
    /// <returns>真实退出码、单文档 stdout 及 stderr.</returns>
    private async Task<(int Code, string Out, string Error)> RunAsync(string? selection)
    {
        Directory.CreateDirectory(root);
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["templateId"] = "builtin:blank",
            ["templateBaseline"] = new string('a', 64),
            ["projectName"] = "output",
            ["locale"] = "zh-cn"
        };
        if (selection is not null) values["templateQueries"] = JsonSerializer.Deserialize<JsonElement>(selection);
        var path = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(values));
        return await CliTests.Run("dotnet", [typeof(PbiUtils.Cli.Program).Assembly.Location, "new", "create", "--request", path,
            "--output", Path.Combine(root, "output"), "--approve", "invalid", "--json"]);
    }

    /// <summary>仅删除本测试自行分配的临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
