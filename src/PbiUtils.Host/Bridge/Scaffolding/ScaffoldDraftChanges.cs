// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Scaffolding\ScaffoldDraftChanges.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 纯计算脚手架分段编辑, 保留可选草稿、按日期覆盖与同包版本选择.

using System.Text.Json;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using static PbiUtils.Host.Bridge.Scaffolding.ScaffoldBridgeParameters;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Host.Bridge.Scaffolding;

/// <summary>只返回不可变候选请求, 文档版本、锁、计划撤销与提交仍由 ScaffoldBridge 负责.</summary>
internal static class ScaffoldDraftChanges
{
    /// <summary>仅计算一个已解析功能段的候选草稿, 不写 owner 状态或失效计划.</summary>
    /// <param name="request">当前草稿值, 原对象保持不变.</param>
    /// <param name="section">受支持的固定功能段.</param>
    /// <param name="value">当前段的严格 DTO 或明确 null.</param>
    /// <returns>候选值; 未知段、类型或索引错误使用原固定校验错误.</returns>
    internal static ScaffoldRequest Apply(ScaffoldRequest request, string section, JsonElement value)
        => section switch
        {
            "base" => Base(request, Read<BaseInput>(value)),
            "measures" => request with { Measures = Nullable<TemplateMeasureDefinition[]>(value) },
            "measuresMode" => request with { MeasuresMode = Read<string>(value) },
            "sample" => request with { Sample = Nullable<SampleDataOptions>(value) },
            "calendar" => request with { Calendar = Nullable<ScaffoldCalendarOptions>(value) },
            "periods" => request with { Periods = Nullable<ScaffoldPeriodOptions>(value) },
            "udfs" => request with { Udfs = Nullable<UdfSelection[]>(value) },
            "templateQueries" => request with { TemplateQueries = Nullable<string[]>(value) },
            "templateQueryGroups" => request with { TemplateQueryGroups = Nullable<Dictionary<string, string>>(value) },
            "measure" => SetMeasure(request, Read<MeasureInput>(value)),
            "workday" => SetWorkday(request, Read<WorkdayInput>(value)),
            "udfFunction" => SetUdfFunction(request, Read<UdfFunctionInput>(value)),
            _ => throw Invalid()
        };

    /// <summary>基础语言变更同步可选模拟语言, 不清空其业务参数.</summary>
    private static ScaffoldRequest Base(ScaffoldRequest request, BaseInput value) => request with
    {
        TemplateId = value.TemplateId,
        TemplateBaseline = value.TemplateBaseline,
        ProjectName = value.ProjectName,
        Locale = value.Locale,
        GitInitialize = value.GitInitialize,
        Sample = request.Sample is { } sample ? sample with { Locale = value.Locale } : null
    };

    /// <summary>大集合按索引操作, 不提高通用桥接消息上限.</summary>
    private static ScaffoldRequest SetMeasure(ScaffoldRequest request, MeasureInput input)
    {
        var items = request.Measures?.ToList() ?? [];
        if (input.Index < 0 || input.Index > items.Count || input.Index >= 128) throw Invalid();
        if (input.Item is null) { if (input.Index >= items.Count) throw Invalid(); items.RemoveAt(input.Index); }
        else if (input.Index == items.Count) items.Add(input.Item); else items[input.Index] = input.Item;
        return request with { Measures = [.. items] };
    }

    /// <summary>覆盖按公历日期替换, 日期合法性仍由共享业务校验.</summary>
    private static ScaffoldRequest SetWorkday(ScaffoldRequest request, WorkdayInput input)
    {
        var calendar = request.Calendar ?? throw Invalid();
        var items = calendar.Overrides?.Where(item => item.Date != input.Date).ToList() ?? [];
        if (!input.Remove) items.Add(new(input.Date, input.IsWorkday, input.Name));
        return request with { Calendar = calendar with { Overrides = [.. items] } };
    }

    /// <summary>函数按来源及包逐项选入, 同包选择共用版本.</summary>
    private static ScaffoldRequest SetUdfFunction(ScaffoldRequest request, UdfFunctionInput input)
    {
        var items = request.Udfs?.ToList() ?? [];
        var index = items.FindIndex(item => item.Source == input.Source && item.PackageId.Equals(input.PackageId, StringComparison.OrdinalIgnoreCase));
        var functions = index < 0 ? [] : items[index].Functions.ToList();
        functions.RemoveAll(name => name.Equals(input.FunctionName, StringComparison.OrdinalIgnoreCase));
        if (input.Selected) functions.Add(input.FunctionName);
        if (index >= 0) items.RemoveAt(index);
        if (functions.Count > 0) items.Add(new(input.Source, input.PackageId, input.Version, [.. functions], input.Baseline, input.SourceBaseline, input.CatalogBaseline));
        return request with { Udfs = items.Count == 0 ? null : [.. items] };
    }
}
