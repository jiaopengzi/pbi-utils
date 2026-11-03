// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Configuration\ModelConfigurationParameters.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立解析模型配置和恢复的受限参数, 不持有目标身份或模型资源.

using PbiUtils.Infrastructure.Input;
using PbiUtils.Host.Bridge.Calendar;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Input;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Udf;

namespace PbiUtils.Host.Bridge.Configuration;

/// <summary>配置和恢复共用原有严格 JSON 与段互斥校验, 不能构造写入授权.</summary>
internal static class ModelConfigurationParameters
{
    private static readonly JsonSerializerOptions Json = UdfInputJson.Create();

    internal sealed record ApplyInput(string TargetId, string BaselineId, string Section, ModelConfigurationRequest Request);
    internal sealed record RecoveryInput(string TargetId, string BaselineId);

    /// <summary>未知字段、重复属性与错误功能段在业务执行前拒绝.</summary>
    internal static ApplyInput Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        CalendarBridgeBudget.Parameters("configuration.apply", value);
        try
        {
            JsonInputValidation.RejectDuplicates(value);
            var input = value.Deserialize<ApplyInput>(Json) ?? throw Invalid();
            if (string.IsNullOrWhiteSpace(input.TargetId) || input.TargetId.Length > 64 || string.IsNullOrWhiteSpace(input.BaselineId)
                || input.BaselineId.Length > 128 || input.Request is null || input.Section is not ("sample" or "measures" or "calendar" or "udfs")) throw Invalid();
            if (input.Request.MeasureConfigurationRevision is { } revision && revision != "missing" && !InputValidation.IsDigest(revision)) throw Invalid();
            return input;
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>一次动作只写当前功能, 不接受其他步骤的隐含修改.</summary>
    internal static void ValidateSection(ApplyInput input)
    {
        var request = input.Request;
        if (input.Section != "measures" && request.MeasureConfigurationRevision is not null) throw Invalid();
        if (input.Section == "measures" && (request.Measures is null || request.Sample is not null || request.Calendar is not null || request.Periods is not null || request.Udfs is not null)
            || input.Section == "sample" && (request.Sample is null || request.Measures is not null || request.Calendar is not null || request.Periods is not null || request.Udfs is not null)
            || input.Section == "calendar" && (request.Calendar is null && request.Periods is null || request.Measures is not null || request.Sample is not null || request.Udfs is not null)
            || input.Section == "udfs" && (request.Udfs is not { Length: > 0 } || request.Measures is not null || request.Sample is not null || request.Calendar is not null || request.Periods is not null)) throw Invalid();
    }

    /// <summary>恢复仅解析原有目标和基线, 不接受新的配置段或路径授权.</summary>
    /// <param name="parameters">已核验来源的参数对象.</param>
    /// <returns>完整的恢复身份; 未知字段、重复或无效值返回固定校验错误.</returns>
    internal static RecoveryInput Recovery(JsonElement parameters)
    {
        RecoveryInput input;
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object) throw Invalid();
            JsonInputValidation.RejectDuplicates(parameters);
            input = parameters.Deserialize<RecoveryInput>(Json) ?? throw Invalid();
            if (string.IsNullOrWhiteSpace(input.TargetId) || input.TargetId.Length > 64 || string.IsNullOrWhiteSpace(input.BaselineId) || input.BaselineId.Length > 128) throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        return input;
    }

    /// <summary>配置输入各入口共用原有固定校验错误, 不泄露参数正文.</summary>
    internal static AppException Invalid() => new("VALIDATION_ERROR", "配置参数无效", 2);
}
