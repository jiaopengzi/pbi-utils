// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Updates.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 严格分发软件更新设置/查询与已核验链接, 不持有模型操作锁或执行安装包.

using System.Text.Json;
using PbiUtils.Application.Updates;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>查询不影响模型操作; 设置与系统浏览器打开保留现有取消/提交边界.</summary>
    /// <param name="method">固定的更新方法名称.</param>
    /// <param name="parameters">已拒绝重复字段的参数对象, 此处继续执行精确白名单.</param>
    /// <param name="flight">本请求独有的取消和提交状态.</param>
    /// <returns>共享服务结果; 缺能力和非法参数明确失败.</returns>
    private async Task<object?> DispatchUpdatesAsync(string method, JsonElement parameters, Flight flight)
    {
        switch (method)
        {
            case "updates.status":
                RequireEmpty(parameters);
                return (productUpdates ?? throw UpdatesUnsupported()).Read();
            case "updates.save":
                var save = ReadUpdatePatch(parameters);
                return (productUpdates ?? throw UpdatesUnsupported()).Save(save,
                    flight.Cancellation.Token, () => BeginCommit(flight));
            case "updates.check":
                var (Source, Value) = ReadUpdateBoolean(parameters, "automatic");
                return await (productUpdates ?? throw UpdatesUnsupported()).CheckAsync(Source, Value, flight.Cancellation.Token);
            case "updates.open":
                var values = Strings(parameters, ["source", "version", "target"], 64);
                RequireUpdateSource(values["source"]);
                if (values["target"] is not ("release" or "without-dotnet")) throw UpdatesInvalid();
                if (productUpdates is null || openUpdateLink is null) throw UpdatesUnsupported();
                // 页面只提交身份, 真实地址必须由当前服务实例已核验的发布结果解析.
                var uri = productUpdates.ResolveLink(values["source"], values["version"], values["target"]);
                BeginCommit(flight);
                await openUpdateLink(uri, CancellationToken.None);
                return new { opened = true, isSimulation = false };
            default:
                throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
    }

    /// <summary>仅接收本次修改的设置字段, 跨窗口保存由存储在锁内合并最新权威值.</summary>
    private static ProductUpdateSettingsPatch ReadUpdatePatch(JsonElement parameters)
    {
        var fields = parameters.EnumerateObject().ToArray();
        if (fields.Length is < 1 or > 2 || fields.Any(field => field.Name is not ("source" or "automaticCheck"))) throw UpdatesInvalid();
        string? source = null;
        bool? automatic = null;
        if (parameters.TryGetProperty("source", out var sourceValue))
        {
            if (sourceValue.ValueKind != JsonValueKind.String) throw UpdatesInvalid();
            source = sourceValue.GetString();
            RequireUpdateSource(source!);
        }
        if (parameters.TryGetProperty("automaticCheck", out var automaticValue))
        {
            if (automaticValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw UpdatesInvalid();
            automatic = automaticValue.GetBoolean();
        }
        return new(automatic, source);
    }

    /// <summary>读取唯一固定来源及明确布尔字段, 不允许未知字段或隐式字符串转换.</summary>
    private static (string Source, bool Value) ReadUpdateBoolean(JsonElement parameters, string field)
    {
        if (parameters.EnumerateObject().Count() != 2
            || !parameters.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
            || !parameters.TryGetProperty(field, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw UpdatesInvalid();
        RequireUpdateSource(source.GetString()!);
        return (source.GetString()!, value.GetBoolean());
    }

    /// <summary>网页不能指定仓库、API、代理或下载主机.</summary>
    private static void RequireUpdateSource(string source)
    {
        if (source is not ("github" or "gitee")) throw UpdatesInvalid();
    }

    /// <summary>统一拒绝格式错误的更新参数.</summary>
    private static AppException UpdatesInvalid() => new("VALIDATION_ERROR", "软件更新参数无效");

    /// <summary>没有实际服务/平台能力时不返回模拟成功.</summary>
    private static AppException UpdatesUnsupported() => new("UNSUPPORTED", "当前宿主不支持软件更新", 3);
}
