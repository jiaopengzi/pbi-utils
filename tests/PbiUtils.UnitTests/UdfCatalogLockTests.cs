// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfCatalogLockTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前 UDF 锁要求明确依赖数组且拒绝畸形目录基线, 不改写模型注解.

using PbiUtils.Infrastructure.Udf.Model;
using System.Text.Json.Nodes;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class UdfCatalogLockTests
{
    /// <summary>当前固定版本锁可没有目录基线, 存在的精确摘要必须完整保留.</summary>
    [Fact]
    public void OptionalCatalogBaselineRoundTripsIncludingUnboundCurrentLocks()
    {
        foreach (var baseline in new[] { null, new string('a', 64) })
        {
            var json = ModelConfigurationUdfLocks.Serialize(Resolution(baseline));
            Assert.Equal(baseline, ModelConfigurationUdfLocks.Read(json).Packages.Single().CatalogBaseline);
        }
    }

    /// <summary>用户模型中的畸形摘要必须失败关闭, 不当作合法无基线值.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void CorruptedCatalogBaselineIsRejected(string baseline)
    {
        var json = ModelConfigurationUdfLocks.Serialize(Resolution(baseline));
        Assert.Equal("CONFIGURATION_UDF_LOCK_INVALID", Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.Read(json)).Code);
    }

    /// <summary>存储锁必须明确声明依赖数组, 缺少、null 或非数组都拒绝使用.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("object")]
    [InlineData("null-item")]
    public void DependenciesMustBeAnExplicitArray(string invalid)
    {
        var document = JsonNode.Parse(ModelConfigurationUdfLocks.Serialize(Resolution(null)))!;
        var package = document["packages"]![0]!.AsObject();
        if (invalid == "missing") package.Remove("dependencies");
        else package["dependencies"] = invalid switch
        {
            "null" => null,
            "null-item" => new JsonArray((JsonNode?)null),
            _ => new JsonObject()
        };
        Assert.Equal("CONFIGURATION_UDF_LOCK_INVALID", Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.Read(document.ToJsonString())).Code);
    }

    /// <summary>历史目录只表示安装时事实, 无关新增选择不会被旧目录代阻塞, 本轮再次选择仍保留新基线.</summary>
    [Fact]
    public void MergeRetainsCurrentSelectionGuardButClearsUntouchedHistoryGuard()
    {
        var previous = Resolution(new string('a', 64));
        Assert.Null(ModelConfigurationUdfLocks.Merge(previous, new([], [])).Packages.Single().CatalogBaseline);
        var current = Resolution(new string('c', 64));
        Assert.Equal(new string('c', 64), ModelConfigurationUdfLocks.Merge(previous, current).Packages.Single().CatalogBaseline);
    }

    /// <summary>构造精确锁历史, 不安装函数或连接真实模型.</summary>
    private static UdfResolution Resolution(string? baseline) => new([], [new("Library", "1.0.0", new string('b', 64), false, [], [], new Dictionary<string, string>(), [], CatalogBaseline: baseline)]);
}
