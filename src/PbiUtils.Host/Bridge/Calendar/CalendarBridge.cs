// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Calendar\CalendarBridge.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 处理日期预览、偏好和工作日解析; 串行门闩、文档代际与关闭排空仍由原宿主拥有.

using PbiUtils.Infrastructure.Calendar;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Calendar;
using PbiUtils.Infrastructure;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

namespace PbiUtils.Host.Bridge.Calendar;

/// <summary>无门闩或生命周期状态的日期业务协作者, 原 owner 在进入前完成排队和当前代次核验.</summary>
internal sealed class CalendarBridge(string userRoot,
    Func<string, string, ScaffoldCalendarOptions, string, CancellationToken, Task<ScaffoldCalendarProgram>>? previewCalendar)
{
    internal sealed record CalendarPreferenceInput(string[] Fields, bool NumberedColumns);
    internal sealed record WorkdayParseInput(string Text, string Delimiter, int StartYear, int EndYear);

    /// <summary>按原方法执行日期业务; 读取完成后和真实写入前核验原文档代次.</summary>
    /// <param name="method">已经受理的固定日期方法.</param>
    /// <param name="parameters">经原预算与重复字段校验的参数.</param>
    /// <param name="ensureCurrent">由唯一 owner 提供的原代际复核.</param>
    /// <param name="committing">原 flight 的提交通知, 只在存储提交边界触发.</param>
    /// <param name="token">原请求的排队和提交前取消令牌.</param>
    /// <returns>原业务结果; 未支持方法、过期文档或存储错误按原路径传播.</returns>
    internal async Task<object> HandleAsync(string method, JsonElement parameters, Action ensureCurrent, Action? committing, CancellationToken token)
    {
        switch (method)
        {
            case "calendar.preview":
                var preview = CalendarPreviewInput.Parse(parameters);
                if (preview.Text("kind") != "template" || previewCalendar is null) throw Unsupported();
                var program = await previewCalendar(preview.Text("templateId"), preview.Text("templateBaseline"), preview.Calendar, preview.Locale, token);
                token.ThrowIfCancellationRequested(); ensureCurrent();
                return CalendarPreviewResult.Create(preview, program);
            case "calendar.preferences":
                Empty(parameters);
                var value = await Task.Run(() => new CalendarPreferencesService(new LocalCalendarPreferencesStore(userRoot)).Read(), token);
                token.ThrowIfCancellationRequested(); ensureCurrent();
                return value;
            case "calendar.savePreferences":
                var preferences = Read<CalendarPreferenceInput>(parameters);
                return await Task.Run(() => new CalendarPreferencesService(new LocalCalendarPreferencesStore(userRoot))
                    .Save(preferences.Fields, preferences.NumberedColumns, token, () => { ensureCurrent(); committing?.Invoke(); }), token);
            case "calendar.parseWorkdays":
                var importing = Read<WorkdayParseInput>(parameters);
                var overrides = await Task.Run(() => WorkdayImport.Parse(importing.Text, importing.Delimiter, importing.StartYear, importing.EndYear), token);
                token.ThrowIfCancellationRequested(); ensureCurrent();
                return new { overrides };
            default: throw Unsupported();
        }
    }
}
