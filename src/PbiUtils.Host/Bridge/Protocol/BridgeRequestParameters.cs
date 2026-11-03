// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Protocol\BridgeRequestParameters.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 纯解析固定字段与有界参数, 不持有会话或宿主资源.

using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Protocol;

/// <summary>各业务分发共用相同严格字段规则, 参数不能构造路径或权限.</summary>
internal static class BridgeRequestParameters
{
    /// <summary>拒绝不需要参数的方法中的多余字段.</summary>
    /// <param name="parameters">参数对象.</param>
    internal static void RequireEmpty(JsonElement parameters)
    {
        if (parameters.EnumerateObject().Any()) throw new AppException("VALIDATION_ERROR", "此方法不接受参数");
    }

    /// <summary>登录仅接收有界邮箱、环境和记住标志, 不接受端点、密码或令牌.</summary>
    internal static PowerBiLoginRequest ServiceLoginRequest(JsonElement parameters)
    {
        if (parameters.EnumerateObject().Count() != 3 || !parameters.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String || email.GetString()!.Length > 254
            || !parameters.TryGetProperty("environmentName", out var environment) || environment.ValueKind != JsonValueKind.String || environment.GetString()!.Length > 64
            || !parameters.TryGetProperty("remember", out var remember) || remember.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AppException("VALIDATION_ERROR", "登录参数不正确");
        return new(email.GetString()!, environment.GetString()!, remember.GetBoolean());
    }

    /// <summary>删除请求严格限制为单个宿主记录身份, 拒绝额外路径、名称及伪造字段.</summary>
    /// <param name="parameters">来自可信来源的参数对象.</param>
    /// <returns>通过类型、长度及格式校验的固定删除请求.</returns>
    /// <exception cref="AppException">参数字段或身份格式无效.</exception>
    internal static RecentRecordDeletionRequest RecentDeletionRequest(JsonElement parameters) => new(RecentRecordDeletionRequest.ValidateId(Strings(parameters, ["id"], 64)["id"]));

    /// <summary>严格解析已声明字符串字段, 不接受额外路径或指令字段.</summary>
    internal static Dictionary<string, string> Strings(JsonElement parameters, string[] allowed, int maximumLength = 1000)
    {
        if (parameters.EnumerateObject().Count() != allowed.Length) throw new AppException("VALIDATION_ERROR", "参数字段数量不正确");
        Dictionary<string, string> result = [];
        foreach (var name in allowed)
        {
            if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximumLength)
                throw new AppException("VALIDATION_ERROR", "需要有界字符串参数");
            result[name] = value.GetString()!;
        }
        return result;
    }
}
