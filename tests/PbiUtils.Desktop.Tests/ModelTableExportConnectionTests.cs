// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelTableExportConnectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证真实 Windows 表数据导出适配器的注册、来源和调用前取消边界, 不访问模型或网络.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ModelTableExportConnectionTests
{
    /// <summary>网页伪造或已经释放的连接句柄不得进入 SDK 查询.</summary>
    [Fact]
    public async Task UnknownConnectionCannotReadData()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(Snapshot("model://unknown"), "Sales", null, (_, _) => throw new InvalidOperationException(), default));
        Assert.Equal("MODEL_DISCONNECTED", error.Code);
    }

    /// <summary>受限架构来源即使持有注册句柄也在创建真实 SDK 会话前被拒绝.</summary>
    [Fact]
    public async Task LimitedConnectionCannotBeUsedAsCompleteTable()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var id = reader.RegisterRemote(new("test-server", "database"));
        try
        {
            var snapshot = Snapshot(id) with { Connection = new("ssas-xmla", "SSAS", true) };
            var error = await Assert.ThrowsAsync<AppException>(() => reader.ReadAsync(snapshot, "Sales", null, (_, _) => throw new InvalidOperationException(), default));
            Assert.Equal("EXPORT_SOURCE_UNSUPPORTED", error.Code);
        }
        finally { reader.Disconnect(); }
    }

    /// <summary>调用前取消不探测远端网络, 不调用消费器, 后端注册仍可正常释放.</summary>
    [Fact]
    public async Task CancelledReadDoesNotReachNetwork()
    {
        var reader = new ConnectedModelReader(new NoFiles());
        var id = reader.RegisterRemote(new("test-server", "database"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Snapshot(id), "Sales", null, (_, _) => throw new InvalidOperationException(), cancelled.Token)); }
        finally { reader.Disconnect(); }
    }

    /// <summary>构造不含真实地址、模型内容或凭据的完整在线快照.</summary>
    private static ProjectSnapshot Snapshot(string path) => new(path, "project", "baseline", "TOM", "", "1", null,
        new("model", 1606, "en-US", [new("Sales", null, false, [new("Value", "Data", "Int64", false, null)], [])], [], [], 0, 0), [], [], [], new("ssas-xmla", "SSAS"));

    /// <summary>该组测试不得回落离线文件读取.</summary>
    private sealed class NoFiles : IProjectReader
    {
        public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => throw new InvalidOperationException("No file access is allowed");
    }
}
