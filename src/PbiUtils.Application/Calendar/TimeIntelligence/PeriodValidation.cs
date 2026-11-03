// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\TimeIntelligence\PeriodValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建与持续配置共用的期间选择输入边界, 不改变生成器的独立校验.

using PbiUtils.Application.Input;
using PbiUtils.Application.Models;
using PbiUtils.Core;

namespace PbiUtils.Application.Calendar.TimeIntelligence;

/// <summary>期间配置段的共享校验, 不构造占位创建请求.</summary>
public static class PeriodValidation
{
    /// <summary>验证日期角色、基础指标、模板标识和精度, 保留配置段的原有限制.</summary>
    /// <param name="periods">本次明确选择的期间配置.</param>
    /// <exception cref="AppException">名称或期间输入无效.</exception>
    public static void Validate(ScaffoldPeriodOptions periods)
    {
        ModelNames.Validate(periods.DateTable); ModelNames.Validate(periods.DateColumn); ModelNames.Validate(periods.MeasureTable);
        if (periods.BaseMeasures is null || periods.BaseMeasures.Length is < 1 or > 128 || periods.Templates is null
            || periods.Templates.Length > 51 || periods.NumberDigits is < 0 or > 8 || periods.PercentDigits is < 0 or > 8
            || !InputValidation.Text(periods.Folder, 200)) throw Invalid();
        foreach (var name in periods.BaseMeasures) ModelNames.Validate(name);
        foreach (var id in periods.Templates) if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(char.IsControl)) throw Invalid();
    }

    /// <summary>保留原创建及配置入口的稳定输入错误, 不包含任意用户内容.</summary>
    private static AppException Invalid() => new("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
}
