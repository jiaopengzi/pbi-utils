// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Sample.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发示例数据目录、生成和清空, 沿用实际进度与提交后排空.

using System.Text.Json;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>网页只能指定有界业务参数, 目录和文件访问由当前原生会话绑定.</summary>
    private sealed record SampleGenerationInput(string Mode, string Locale, int ProductCount, int StoreCount, int InventoryCycle, string StartDate, string EndDate, int? Seed = null);

    /// <summary>同步回调使阶段序号在结果返回之前发出, 不捕获 UI 上下文排队.</summary>
    private sealed class SampleProgressSink(Action<SampleDataProgress> report) : IProgress<SampleDataProgress>
    {
        /// <summary>转发生成器真实进度, 不根据时间估算百分比.</summary>
        public void Report(SampleDataProgress value) => report(value);
    }

    /// <summary>分发示例数据目录、生成和清空, 沿用实际进度与提交后排空.</summary>
    /// <param name="method">属于当前业务的固定方法名.</param>
    /// <param name="parameters">来源和重复字段已经核验的参数对象.</param>
    /// <param name="id">当前请求身份, 用于阶段及事件关联.</param>
    /// <param name="flight">同一会话持有的取消、计时和提交状态.</param>
    /// <returns>业务结果; 所有错误保持原码并交回协议完成路径.</returns>
    private async Task<object?> DispatchSampleAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        object? result = null;
        switch (method)
        {
            case "sample.status":
            case "sample.chooseDirectory":
            case "sample.generate":
            case "sample.clear":
            case "sample.openDirectory":
                if (samples is null) throw new AppException("UNSUPPORTED", "当前宿主未提供示例数据", 3);
                if (!await sampleGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "示例数据操作正在执行", 3);
                try
                {
                    if (method != "sample.generate") RequireEmpty(parameters);
                    if (method == "sample.chooseDirectory")
                    {
                        var chosen = (chooseSampleDirectory is null ? null : await chooseSampleDirectory(flight.Cancellation.Token))
                            ?? throw new AppException("CANCELLED", "已取消选择目录", 130);
                        var selected = await samples.GetSelectionAsync(chosen, flight.Cancellation.Token);
                        lock (sync)
                        {
                            flight.Cancellation.Token.ThrowIfCancellationRequested();
                            BeginCommit(flight);
                            sampleOutput = selected.OutputPath;
                            result = selected;
                        }
                    }
                    else if (method == "sample.generate")
                    {
                        var input = parameters.Deserialize<SampleGenerationInput>(SampleJson) ?? throw new AppException("VALIDATION_ERROR", "生成参数为空");
                        var sampleFields = new[] { "mode", "locale", "productCount", "storeCount", "inventoryCycle", "startDate", "endDate", "seed" };
                        if (parameters.EnumerateObject().Any(field => !sampleFields.Contains(field.Name, StringComparer.Ordinal)) || sampleFields.Take(7).Any(name => !parameters.TryGetProperty(name, out _)))
                            throw new AppException("VALIDATION_ERROR", "生成参数字段无效");
                        if (input.Mode is not ("full" or "incremental")) throw new AppException("VALIDATION_ERROR", "生成模式无效");
                        var options = new SampleDataOptions(input.Locale, input.ProductCount, input.StoreCount, input.InventoryCycle, input.StartDate, input.EndDate, input.Seed);
                        options.Validate();
                        var state = await samples.GetSelectionAsync(sampleOutput, flight.Cancellation.Token);
                        if (!flight.IsMcp && input.Mode == "full" && state.HasData && (confirmSample is null || !await confirmSample("overwrite", sampleOutput, flight.Cancellation.Token)))
                            throw new AppException("CANCELLED", "已取消覆盖示例数据", 130);
                        flight.Timer?.Dispose();
                        flight.Timer = ArmTimeout(id, flight, timeout ?? Timeout.InfiniteTimeSpan, "SampleGeneration");
                        Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "generating" } });
                        var sampleSequence = 0;
                        double lastSamplePercent = -1;
                        string? lastSampleStage = null;
                        var progress = new SampleProgressSink(value =>
                        {
                            lock (sync)
                            {
                                if (lastSampleStage == value.Stage && Math.Abs(value.Percent - lastSamplePercent) < 0.1) return;
                                lastSamplePercent = value.Percent; lastSampleStage = value.Stage;
                                if (!disposed && !flight.IsMcp && flights.TryGetValue(id, out var current) && ReferenceEquals(current, flight))
                                    Send(new { protocolVersion = 1, @event = new OperationEvent(id, ++sampleSequence, "sampleProgress", DateTimeOffset.UtcNow.ToString("O"), value) });
                            }
                        });
                        result = input.Mode == "full"
                            ? await samples.GenerateAsync(sampleOutput, options, state.HasData, progress, () => BeginModelCommit(id, flight), cancellationToken: flight.Cancellation.Token)
                            : await samples.IncrementAsync(sampleOutput, options, progress, () => BeginModelCommit(id, flight), flight.Cancellation.Token);
                    }
                    else if (method == "sample.clear")
                    {
                        if (!flight.IsMcp && (confirmSample is null || !await confirmSample("clear", sampleOutput, flight.Cancellation.Token)))
                            throw new AppException("CANCELLED", "已取消清空示例数据", 130);
                        result = await samples.ClearAsync(sampleOutput, () => BeginModelCommit(id, flight), flight.Cancellation.Token);
                    }
                    else
                    {
                        result = method == "sample.openDirectory" ? await samples.GetSelectionAsync(sampleOutput, flight.Cancellation.Token)
                            : await samples.GetAsync(sampleOutput, flight.Cancellation.Token);
                        if (method == "sample.openDirectory")
                        {
                            if (openSampleDirectory is null) throw new AppException("UNSUPPORTED", "打开目录不可用", 3);
                            BeginCommit(flight);
                            await openSampleDirectory(sampleOutput, CancellationToken.None);
                            result = new { opened = true };
                        }
                    }
                }
                finally { sampleGate.Release(); }
                break;
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
        return result;
    }
}
