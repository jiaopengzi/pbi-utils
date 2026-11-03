// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureStorageReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复核公共配置自定义段、未知结构及空列表在双模式切换中的存储边界.

using PbiUtils.Infrastructure.TemplateMeasures;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只操作随机测试目录和完整 JSON 字节, 不访问真实用户配置或模型.</summary>
public sealed class TemplateMeasureStorageReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-measure-storage-review-" + Guid.NewGuid().ToString("N"));

    /// <summary>空 Config 仍产生明确空数组, 切 TMDL 只退休模板段, 多轮往返保留其他复杂 JSON 段.</summary>
    [Fact]
    public void EmptyConfigurationRoundTripPreservesCustomSections()
    {
        var original = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"Custom":{"a":[1,null,{"text":"<svg> & 中文"}],"flag":true},"Another":[]} """);
        var expected = JsonNode.Parse(original)!;
        byte[]? bytes = original;
        foreach (var mode in new[] { "config", "tmdl", "config", "tmdl" })
        {
            bytes = TemplateMeasureConfigurationStorage.JsonBytes(bytes, mode, [])!;
            var actual = JsonNode.Parse(bytes)!;
            Assert.True(JsonNode.DeepEquals(expected["Custom"], actual["Custom"]));
            Assert.True(JsonNode.DeepEquals(expected["Another"], actual["Another"]));
            var parsed = TemplateMeasureConfigurationFile.Parse(bytes);
            Assert.Empty(parsed.Measures);
            Assert.Equal(mode == "config", actual["MeasureTemplates"] is JsonArray);
        }
    }

    /// <summary>缺少或未知 schema、非法模板段及重复业务键只撤销文件能力, 原文保持不变.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"MeasureTemplates\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"MeasureTemplates\":null}")]
    [InlineData("{\"schemaVersion\":1,\"Custom\":1,\"Custom\":2}")]
    public void UnknownOrBrokenConfigurationIsNeverClaimed(string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "pbi-utils"));
        var entry = Path.Combine(root, "model.pbix");
        File.WriteAllBytes(entry, [0, 255]);
        var path = Path.Combine(root, "pbi-utils", "config.json");
        File.WriteAllText(path, content);

        var state = TemplateMeasureConfigurationFile.Read(root, entry);

        Assert.False(state.Available);
        Assert.Null(state.Configuration);
        Assert.Null(state.Revision);
        Assert.Equal("invalid", state.UnavailableReason);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Throws<JsonException>(() => TemplateMeasureConfigurationFile.Parse(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>有效且没有模板段的公共配置仍可接收首次 Config, TMDL 请求不改写无关原始字节.</summary>
    [Fact]
    public void ConfigurationWithoutMeasureSectionRetainsItsExactBytesOnTmdl()
    {
        var bytes = Encoding.UTF8.GetBytes("""{ "schemaVersion":1, "Custom": [1,2] }""");
        Assert.Empty(TemplateMeasureConfigurationFile.Parse(bytes).Measures);
        Assert.Same(bytes, TemplateMeasureConfigurationStorage.JsonBytes(bytes, "tmdl", []));
        Assert.Null(TemplateMeasureConfigurationStorage.JsonBytes(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"MeasureTemplates":[]}"""), "tmdl", []));
    }

    /// <summary>仅清理本测试创建的随机根, 不影响其他测试或用户文件.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
