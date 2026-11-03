// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ExternalToolTargetTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证外部工具端口与数据库绑定策略, TOM 对象仅在内存创建且不连接真实 Desktop.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Application.Connections;
using Microsoft.AnalysisServices.Tabular;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ExternalToolTargetTests
{
    /// <summary>目标端口必须唯一匹配完整 Desktop/引擎身份, 失效或歧义不回退连接另一实例.</summary>
    [Fact]
    public void EndpointResolutionRejectsStaleAmbiguousAndIncompleteTargets()
    {
        var target = new ExternalToolLaunchTarget(50432, "database");
        PowerBiEndpoint expected = new(100, "report", 200, 1234, 50432);
        PowerBiEndpoint other = new(101, "report", 201, 1235, 50433);
        Assert.Equal(expected, ConnectedModelReader.ResolveExternalToolEndpoint(target, [other, expected]));
        IReadOnlyList<PowerBiEndpoint>[] invalid = [[], [other], [expected with { EngineStarted = null }],
            [expected, other with { Port = 50432 }], [expected, expected with { Port = 50434 }]];
        foreach (var endpoints in invalid) Assert.Equal("EXTERNAL_TOOL_TARGET_UNAVAILABLE",
            Assert.Throws<AppException>(() => ConnectedModelReader.ResolveExternalToolEndpoint(target, endpoints)).Code);
    }

    /// <summary>启动时名称或 ID 可准确匹配唯一模型, 固定 ID 后名称相同但 ID 变化必须拒绝.</summary>
    [Fact]
    public void DatabaseMustMatchAndRemainBoundToItsOriginalId()
    {
        var database = new Database { ID = "id-1", Name = "model-name" };
        Assert.Same(database, ConnectedModelReader.RequireExternalToolDatabase([database], "id-1"));
        Assert.Same(database, ConnectedModelReader.RequireExternalToolDatabase([database], "model-name"));
        Assert.Same(database, ConnectedModelReader.RequireExternalToolDatabase([database], "id-1", idOnly: true));
        Assert.Equal("EXTERNAL_TOOL_DATABASE_MISMATCH", Assert.Throws<AppException>(() => ConnectedModelReader.RequireExternalToolDatabase([database], "wrong")).Code);
        Assert.Throws<AppException>(() => ConnectedModelReader.RequireExternalToolDatabase([], "id-1"));
        Assert.Throws<AppException>(() => ConnectedModelReader.RequireExternalToolDatabase([database, new() { ID = "id-2", Name = "other" }], "id-1"));
        Assert.Throws<AppException>(() => ConnectedModelReader.RequireExternalToolDatabase([new() { ID = "id-2", Name = "id-1" }], "id-1", idOnly: true));
    }
}
