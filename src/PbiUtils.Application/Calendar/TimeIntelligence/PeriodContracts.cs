// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\TimeIntelligence\PeriodContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建与持续配置共用的期间选择及模板依赖契约.

namespace PbiUtils.Application.Calendar.TimeIntelligence;

/// <summary>明确基础指标与日期角色, Templates 为声明式期间标识.</summary>
public sealed record ScaffoldPeriodOptions(string DateTable, string DateColumn, string MeasureTable, string[] BaseMeasures,
    string[] Templates, string Folder = "时间智能", int NumberDigits = 2, int PercentDigits = 2);
/// <summary>期间选择描述, Dependencies 列出自动补入的模板标识.</summary>
public sealed record ScaffoldPeriodTemplate(string Id, string Group, string Label, string Description, string[] Dependencies);
