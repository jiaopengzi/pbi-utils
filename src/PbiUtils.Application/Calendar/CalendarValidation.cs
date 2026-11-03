// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\CalendarValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建与持续配置共用完整年份、日期字段及工作日覆盖校验.

using PbiUtils.Core;

namespace PbiUtils.Application.Calendar;

/// <summary>日期领域完整输入边界, 不依赖创建流程.</summary>
public static class CalendarValidation
{
    /// <summary>统一校验完整年、离线农历范围和业务工作日覆盖.</summary>
    /// <param name="options">本次明确选择的日期表参数.</param>
    /// <exception cref="AppException">日期字段、范围或工作日覆盖无效.</exception>
    public static void Validate(ScaffoldCalendarOptions options)
    {
        ScaffoldCalendar.ValidateOptions(options);
        _ = WorkdayImport.ValidateFinal(options.Overrides, options.StartYear, options.EndYear);
    }
}
