// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\ScaffoldRequestValidationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在独立用户根验证 CLI 业务入口的完整脚手架请求矩阵.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Commands;
using Xunit;

namespace PbiUtils.Cli.Tests;

/// <summary>通过真实 CLI 业务分发、请求读取、校验及批准流程核对请求矩阵.</summary>
public sealed class ScaffoldRequestValidationTests : IDisposable
{
    private readonly ScaffoldRequestFixture request = new();

    /// <summary>省略、显式 null、空集和标准选择均通过请求校验, 随后由无效批准阻止创建.</summary>
    /// <param name="selection">模板查询选择的 JSON 值, null 表示省略字段.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"Hour24\",\"MQHY\",\"TypeDetails\"]")]
    [InlineData("[\"RefreshTime\"]")]
    [InlineData("[\"Hour24\",\"MQHY\",\"RefreshTime\",\"TypeDetails\"]")]
    public Task OptionalQuerySelectionAcceptsCurrentRequestShapes(string? selection) =>
        AssertRequestFailureAsync("templateQueries", selection, "INVALID_SCAFFOLD_PLAN", 3);

    /// <summary>未知、空白、大小写和重复选择先于批准检查失败.</summary>
    /// <param name="selection">包含无效选择的 JSON 数组.</param>
    [Theory]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"hour24\"]")]
    [InlineData("[\"Hour24\",\"Hour24\"]")]
    public Task InvalidQuerySelectionFailsBeforeApproval(string selection) =>
        AssertRequestFailureAsync("templateQueries", selection, "INVALID_SCAFFOLD_REQUEST", 2);

    /// <summary>缺失必需布尔值在读取阶段拒绝, 不到达批准或文件操作.</summary>
    /// <param name="section">请求字段.</param>
    /// <param name="value">缺少必需属性的 JSON 值.</param>
    [Theory]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"description\":\"\",\"displayFolder\":\"\"}]")]
    [InlineData("calendar", "{\"kind\":\"simple\",\"startYear\":2026,\"endYear\":2026,\"overrides\":[{\"date\":\"2026-10-19\",\"name\":\"Missing boolean\"}]}")]
    public Task RejectsMissingRequiredRequestFields(string section, string value) =>
        AssertRequestFailureAsync(section, value, "INVALID_ARGUMENT", 2);

    /// <summary>省略可选字段和明确 false 能通过读取, 随后仍被无效批准挡住.</summary>
    /// <param name="section">请求字段.</param>
    /// <param name="value">合法的 JSON 值.</param>
    [Theory]
    [InlineData("measures", "[{\"name\":\"Test\",\"value\":\"1\",\"dataCategory\":\"Uncategorized\",\"enabled\":false,\"description\":\"\",\"displayFolder\":\"\"}]")]
    [InlineData("calendar", "{\"kind\":\"simple\",\"startYear\":2026,\"endYear\":2026,\"overrides\":[{\"date\":\"2026-10-19\",\"isWorkday\":false,\"name\":\"Explicit false\"}]}")]
    [InlineData("udfs", "[{\"source\":\"daxlib\",\"packageId\":\"test.package\",\"functions\":[\"Test.Function\"]}]")]
    public Task AcceptsOptionalFieldsAndExplicitFalseBeforeApproval(string section, string value) =>
        AssertRequestFailureAsync(section, value, "INVALID_SCAFFOLD_PLAN", 3);

    /// <summary>核对真实业务拒绝和无文件提交. 请求及回执只使用测试独占根.</summary>
    /// <param name="section">生成请求时覆盖的字段.</param>
    /// <param name="value">该字段的 JSON 值, null 表示省略字段.</param>
    /// <param name="errorCode">业务应返回的稳定错误码.</param>
    /// <param name="exitCode">该业务错误映射的 CLI 退出码.</param>
    private async Task AssertRequestFailureAsync(string section, string? value, string errorCode, int exitCode)
    {
        var arguments = await request.CreateArgumentsAsync(section, value);
        var error = await Assert.ThrowsAsync<AppException>(() => CliBusinessCommands.RunAsync(
            arguments, request.UserRoot, NullLoggerFactory.Instance, CancellationToken.None));
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(exitCode, error.ExitCode);
        Assert.False(Directory.Exists(request.Output));
    }

    /// <summary>释放本测试的独占请求目录及用户根.</summary>
    public void Dispose() => request.Dispose();
}

/// <summary>为真实进程和独立业务入口构造相同请求, 无效批准阻止发布到测试输出目录.</summary>
internal sealed class ScaffoldRequestFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-scaffold-request-" + Guid.NewGuid().ToString("N"));
    public string UserRoot => Path.Combine(root, "user");
    public string Output => Path.Combine(root, "新工程");

    /// <summary>用指定字段生成请求文件并返回正式 CLI 参数. JSON 或写入失败直接传播.</summary>
    /// <param name="section">要加入的请求字段.</param>
    /// <param name="value">该字段的 JSON 值, null 表示省略字段.</param>
    /// <returns>包含请求文件、独立输出路径和无效批准的参数数组.</returns>
    public async Task<string[]> CreateArgumentsAsync(string section, string? value)
    {
        Directory.CreateDirectory(root);
        var fields = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["templateId"] = "builtin:blank",
            ["templateBaseline"] = new string('a', 64),
            ["projectName"] = Path.GetFileName(Output),
            ["locale"] = "zh-cn"
        };
        if (value is not null) fields[section] = JsonSerializer.Deserialize<JsonElement>(value);
        var path = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fields));
        return ["new", "create", "--request", path, "--output", Output, "--approve", "invalid", "--json"];
    }

    /// <summary>仅删除本测试自行创建的独占路径.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
