// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\ScaffoldValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 校验创建入口参数并组合同一组独立领域校验, 不进行环境访问.

using PbiUtils.Core;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Scaffolding;

/// <summary>所有入口在准备文件或发起网络前共用同一输入边界.</summary>
public static class ScaffoldValidation
{
    /// <summary>验证完整请求; 不接触文件系统, 失败为稳定输入错误.</summary>
    /// <param name="request">包含固定辅助表 ID、已选分组及可选生成段的完整创建请求.</param>
    /// <exception cref="AppException">版本、参数、重复/未知目录项或分组无效时拒绝创建.</exception>
    public static void Validate(ScaffoldRequest request)
    {
        if (request is null || request.SchemaVersion != 1 || request.Locale is not ("zh-cn" or "en-us")
            || string.IsNullOrWhiteSpace(request.TemplateId) || request.TemplateId.Length > 200
            || request.TemplateId.Any(char.IsControl) || !InputValidation.IsDigest(request.TemplateBaseline)) throw Invalid();
        TemplateMeasureValidation.ValidateMode(request.MeasuresMode);
        ProjectName(request.ProjectName);
        if (request.TemplateQueries is { } queries && (queries.Length > ScaffoldTemplateQueries.All.Count
            || queries.Distinct(StringComparer.Ordinal).Count() != queries.Length
            || queries.Any(id => !ScaffoldTemplateQueries.All.Any(query => query.Id == id)))) throw Invalid();
        if (request.TemplateQueryGroups is { } groups && (groups.Count > ScaffoldTemplateQueries.All.Count || groups.Any(pair =>
            !ScaffoldTemplateQueries.All.Any(query => query.Id == pair.Key) || pair.Value is null || pair.Value.Length > 200
            || pair.Value.Length > 0 && string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Any(char.IsControl)))) throw Invalid();
        TemplateMeasureValidation.Validate(request.Measures);
        request.Sample?.Validate();
        if (request.Calendar is { } calendar) CalendarValidation.Validate(calendar);
        if (request.Periods is { } periods) PeriodValidation.Validate(periods);
        UdfSelectionValidation.Validate(request.Udfs);
    }

    /// <summary>验证可作为单个输出子目录的名称, 禁止分隔符、Windows 设备名和尾部点空格.</summary>
    public static string ProjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() || value.EndsWith('.')
            || value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) || value is "." or "..") throw Invalid();
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) throw Invalid();
        return value;
    }

    /// <summary>固定输入提示不含任意用户内容.</summary>
    private static AppException Invalid() => new("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
}
