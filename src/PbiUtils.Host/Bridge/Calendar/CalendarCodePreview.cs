// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Calendar\CalendarCodePreview.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 日期代码预览的精确上下文输入、摘要和有界响应, 不携带模型写权限.

using PbiUtils.Infrastructure.Input;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Input;

namespace PbiUtils.Host.Bridge.Calendar;

internal sealed record CalendarPreviewInput(JsonElement Context, ScaffoldCalendarOptions Calendar, string Locale)
{
    /// <summary>嵌套上下文严格二选一; 网页只传宿主身份, 不传路径、映射或函数定义.</summary>
    internal static CalendarPreviewInput Parse(JsonElement parameters)
    {
        CalendarBridgeBudget.Parameters("calendar.preview", parameters);
        JsonInputValidation.RejectDuplicates(parameters);
        var input = parameters.Deserialize<CalendarPreviewInput>(InputJson.CreateStrict()) ?? throw Invalid();
        if (input.Context.ValueKind != JsonValueKind.Object || input.Calendar is null || input.Locale is not ("zh-cn" or "en-us")) throw Invalid();
        var kind = input.Text("kind");
        string[] expected = kind switch { "template" => ["kind", "templateId", "templateBaseline"], "model" => ["kind", "targetId", "baselineId"], _ => throw Invalid() };
        if (!input.Context.EnumerateObject().Select(value => value.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw Invalid();
        foreach (var name in expected)
        {
            var value = input.Text(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > (name == "templateId" ? 200 : 64)) throw Invalid();
            if (name is "templateBaseline" or "baselineId" && (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))) throw Invalid();
        }
        ScaffoldCalendar.ValidateOptions(input.Calendar);
        return input;
    }

    /// <summary>读取已验证上下文中的字符串, 残缺输入统一失败.</summary>
    internal string Text(string name) => Context.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    private static AppException Invalid() => new("VALIDATION_ERROR", "日期代码预览参数或目标身份无效");
}

internal sealed record CalendarPreviewResult(string FunctionName, string FunctionExpression, string InvocationExpression,
    string InputDigest, string ContextBaseline, string GeneratorVersion)
{
    /// <summary>两个代码文本只返回一次; 摘要关联当前输入和宿主核定基线, 不提供提交授权.</summary>
    internal static CalendarPreviewResult Create(CalendarPreviewInput input, ScaffoldCalendarProgram program)
    {
        if ((long)Encoding.UTF8.GetByteCount(program.FunctionExpression) + Encoding.UTF8.GetByteCount(program.InvocationExpression) > 3 * 1024 * 1024)
            throw new AppException("MESSAGE_TOO_LARGE", "日期代码预览超出内容大小限制");
        var baseline = input.Text(input.Text("kind") == "template" ? "templateBaseline" : "baselineId");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            input.Context,
            input.Calendar,
            input.Locale,
            program.FunctionName,
            program.GeneratorVersion
        })));
        return new(program.FunctionName, program.FunctionExpression, program.InvocationExpression, digest, baseline, program.GeneratorVersion);
    }
}
