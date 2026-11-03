// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureCompilerReviewTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板配置编译器在受信来源读取结束时取消, 不返回可消费计划或伪报核验成功.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用随机资产根和宿主来源替身, 不连接用户模型或写入目标工程.</summary>
public sealed class TemplateMeasureCompilerReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-compiler-review-" + Guid.NewGuid().ToString("N"));

    /// <summary>来源适配完成只读工作时才观察到取消, 编译器仍须取消并不得返回冻结资产计划.</summary>
    [Fact]
    public async Task CancelledSourceVerificationDoesNotReturnPreparedConfig()
    {
        Directory.CreateDirectory(root);
        using var compiler = new ModelConfigurationCompiler();
        using var cancellation = new CancellationTokenSource();
        var context = new ModelConfigurationAssetContext(root, new Dictionary<string, string>(), _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }, CanUseMeasureConfig: true, ConfigurationRevision: "missing");
        ModelEditRequest? unexpected = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => unexpected = await compiler.PrepareAsync(
                new(Measures: [], MeasuresMode: "config", MeasureConfigurationRevision: "missing"), Snapshot(), cancellation.Token, context));
            Assert.False(Directory.Exists(Path.Combine(root, "pbi-utils")));
        }
        finally { if (unexpected is not null) compiler.Release(unexpected); }
    }

    /// <summary>空资产计划核验期间取消同样必须传播, 不能因无文件循环而返回成功.</summary>
    [Fact]
    public async Task CancelledVerificationOfAssetlessTmdlPlanDoesNotSucceed()
    {
        Directory.CreateDirectory(root);
        using var compiler = new ModelConfigurationCompiler();
        using var cancellation = new CancellationTokenSource();
        var cancelOnRead = false;
        var context = new ModelConfigurationAssetContext(root, new Dictionary<string, string>(), _ =>
        {
            if (cancelOnRead) cancellation.Cancel();
            return Task.CompletedTask;
        }, CanUseMeasureConfig: true, ConfigurationRevision: "missing");
        var prepared = await compiler.PrepareAsync(new(Measures: [], MeasuresMode: "tmdl", MeasureConfigurationRevision: "missing"), Snapshot(), default, context);
        try
        {
            Assert.Empty(prepared.Configuration!.Assets);
            cancelOnRead = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiler.VerifyAsync(prepared, cancellation.Token));
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>创建受信的本机连接展示快照, 没有文件路径或外部网络权限.</summary>
    private static ProjectSnapshot Snapshot() => new("model://compiler-review", "project", "baseline", "TMDL", "", "", null,
        new("test", 1606, "en-US", [], [], [], 0, 0), [], [], [], new("desktop", "Power BI Desktop"));

    /// <summary>仅清理本测试创建的隔离目录; 编译器已先释放临时资产.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
