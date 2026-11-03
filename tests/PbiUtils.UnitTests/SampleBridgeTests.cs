// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SampleBridgeTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 实际示例桥接的目录授权、原生确认、提交取消及关闭排空测试.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>直接执行实际桥接, 适配器门闩只用于可取消和已提交的竞争验证.</summary>
public sealed class SampleBridgeTests
{
    private static readonly object Options = new { mode = "full", locale = "zh-cn", productCount = 2, storeCount = 2, inventoryCycle = 5, startDate = "2024-01-01", endDate = "2024-03-01" };

    /// <summary>大小写别名、未知字段和重复字段在原生确认之前拒绝, 与机器 schema 对齐.</summary>
    [Theory]
    [InlineData("{\"Mode\":\"full\",\"locale\":\"zh-cn\"}")]
    [InlineData("{\"mode\":\"full\",\"mode\":\"incremental\"}")]
    [InlineData("{\"mode\":\"full\",\"locale\":\"zh-cn\",\"path\":\"unexpected\"}")]
    [InlineData("{\"mode\":\"full\",\"locale\":\"zh-cn\",\"seed\":2147483648}")]
    public async Task InvalidGenerationFieldsNeverAskForNativeApproval(string parameters)
    {
        var confirmations = 0;
        var adapter = new Adapter(false) { HasData = true };
        ConcurrentQueue<JsonElement> output = new();
        using var session = Create(adapter, output, (_, _, _) => { confirmations++; return Task.FromResult(true); });
        await session.ReceiveAsync(OriginPolicy.Production, "{\"protocolVersion\":1,\"id\":\"invalid\",\"method\":\"sample.generate\",\"params\":" + parameters + "}");
        Assert.True(output.Last().TryGetProperty("error", out _));
        Assert.Equal(0, confirmations);
        Assert.Equal(0, adapter.Writes);
    }

    /// <summary>目录只能由宿主选择, 全量覆盖和清空都需要独立确认.</summary>
    [Fact]
    public async Task DirectoryBindingAndConfirmationStayInHost()
    {
        var adapter = new Adapter(false) { HasData = true };
        ConcurrentQueue<JsonElement> output = new();
        using var session = Create(adapter, output, (_, _, _) => Task.FromResult(false));
        await Call(session, "status", "sample.status", new { });
        Assert.Equal("bound", output.Last().GetProperty("result").GetProperty("outputPath").GetString());
        await Call(session, "injected", "sample.chooseDirectory", new { path = "untrusted" });
        Assert.Equal("VALIDATION_ERROR", output.Last().GetProperty("error").GetProperty("code").GetString());
        await Call(session, "choose", "sample.chooseDirectory", new { });
        Assert.Equal("chosen", output.Last().GetProperty("result").GetProperty("outputPath").GetString());
        await Call(session, "generate", "sample.generate", Options);
        Assert.Equal("CANCELLED", output.Last().GetProperty("error").GetProperty("code").GetString());
        await Call(session, "clear", "sample.clear", new { });
        Assert.Equal("CANCELLED", output.Last().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, adapter.Writes);
        await Call(session, "removed", "init.prepare", new { });
        Assert.Equal("UNKNOWN_METHOD", output.Last().GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>提交前接受取消且不写入, 提交后拒绝取消并由关闭路径等待结果.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndShutdownHonorSampleCommit(bool committed)
    {
        var adapter = new Adapter(committed);
        ConcurrentQueue<JsonElement> output = new();
        using var session = Create(adapter, output, (_, _, _) => Task.FromResult(true));
        var generation = Call(session, "generate", "sample.generate", Options);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Call(session, "cancel", "request.cancel", new { requestId = "generate" });
        Assert.Equal(!committed, output.Last().GetProperty("result").GetProperty("accepted").GetBoolean());
        var closing = session.ShutdownAsync();
        Assert.False(closing.IsCompleted);
        adapter.Release.SetResult();
        await Task.WhenAll(generation, closing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(committed ? 1 : 0, adapter.Writes);
        // 已关闭的 WebView 不再发送结果, 但业务必须保留实际提交事实并完成排空.
        Assert.Equal(0, session.PendingCount);
        Assert.Equal("Disposed", session.Lifecycle);
    }

    /// <summary>构造固定原生回调, 适配器不访问用户目录.</summary>
    private static BridgeSession Create(Adapter adapter, ConcurrentQueue<JsonElement> output, Func<string, string, CancellationToken, Task<bool>> confirm)
    {
        return new(new(false, null), new(new RuntimeEnvironment()),
            text => output.Enqueue(JsonSerializer.Deserialize<JsonElement>(text)), "test", samples: new(adapter), sampleDirectory: "bound",
            chooseSampleDirectory: _ => Task.FromResult<string?>("chosen"), confirmSample: confirm);
    }

    /// <summary>通过真实 JSON 边界发送请求.</summary>
    private static Task Call(BridgeSession session, string id, string method, object parameters) =>
        session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));

    /// <summary>门闩控制实际服务边界, 不依赖时间猜测提交先后.</summary>
    private sealed class Adapter(bool committed) : ISampleDataAdapter
    {
        public bool HasData { get; init; }
        public int Writes { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>返回宿主绑定的目录, 证明网页无法更改路径.</summary>
        public Task<SampleDataState> InspectAsync(string path, CancellationToken token) => Task.FromResult(new SampleDataState(path, HasData, null, []));
        /// <summary>轻量绑定支持损坏目录修复, 不将统计失败当成空目录.</summary>
        public Task<SampleDataState> SelectAsync(string path, CancellationToken token) => InspectAsync(path, token);
        /// <summary>在回调前后固定门闩, 验证取消及关闭协议.</summary>
        public async Task<SampleDataResponse> GenerateAsync(string path, SampleDataOptions options, bool incremental, bool overwrite, IProgress<SampleDataProgress>? progress, Action? beginCommit, CancellationToken token, Func<CancellationToken, Task>? beforeCommit = null)
        {
            if (beforeCommit is not null) await beforeCommit(token);
            token.ThrowIfCancellationRequested();
            if (committed) beginCommit?.Invoke();
            Entered.SetResult();
            await Release.Task;
            if (!committed) { token.ThrowIfCancellationRequested(); beginCommit?.Invoke(); }
            Writes++;
            return new(new(path, true, options.EndDate, []), new(2, 2, 0, 0, 0, 0));
        }
        /// <summary>清空必须先通过宿主确认; 此测试失败路径不应到达这里.</summary>
        public Task<SampleDataState> ClearAsync(string path, Action? beginCommit, CancellationToken token) { Writes++; return InspectAsync(path, token); }
    }
}
