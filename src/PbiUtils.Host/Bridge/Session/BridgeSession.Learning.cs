// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Learning.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发学习页的固定网站打开、受限区域更新与释放, 不提供通用浏览器能力.

using System.Text.Json;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>仅将严格学习请求交给宿主适配器; 外站不能通过来源检查进入此入口.</summary>
    /// <param name="method">固定学习方法.</param>
    /// <param name="parameters">已核验无重复字段的参数.</param>
    /// <param name="flight">取消与请求生命周期.</param>
    /// <returns>宿主已受理结果; 缺少适配器明确失败.</returns>
    private async Task<object?> DispatchLearningAsync(string method, JsonElement parameters, Flight flight)
    {
        switch (method)
        {
            case "learning.update":
                if (parameters.EnumerateObject().Count() != 7) throw new AppException("VALIDATION_ERROR", "学习区域参数无效");
                var bounds = parameters.Deserialize<LearningViewRequest>(StrictJson) ?? throw new AppException("VALIDATION_ERROR", "学习区域参数无效");
                bounds.Validate();
                if (updateLearningView is null) throw new AppException("UNSUPPORTED", "当前宿主不支持嵌入学习网站", 3);
                await updateLearningView(bounds, flight.Cancellation.Token);
                return new { embedded = true, isSimulation = false };
            case "learning.close":
                RequireEmpty(parameters);
                if (closeLearningView is null) throw new AppException("UNSUPPORTED", "当前宿主不支持嵌入学习网站", 3);
                await closeLearningView(flight.Cancellation.Token);
                return new { closed = true, isSimulation = false };
            case "learning.open":
                RequireEmpty(parameters);
                if (openLearningSite is null) throw new AppException("UNSUPPORTED", "当前宿主不支持打开学习网站", 3);
                BeginCommit(flight);
                await openLearningSite(CancellationToken.None);
                return new { opened = true, isSimulation = false };
            default:
                throw new AppException("METHOD_NOT_FOUND", "不支持的方法");
        }
    }
}
