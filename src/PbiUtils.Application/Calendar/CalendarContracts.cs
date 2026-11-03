// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\CalendarContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 日期生成、配置和能力读取共用的输入及元数据契约.

using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Application.Calendar;

/// <summary>日期表类型为 simple 或 full, 年份包含完整公历年.</summary>
public sealed record ScaffoldCalendarOptions(string Kind, int StartYear, int EndYear, string TableName = "Calendar", WorkdayOverride[]? Overrides = null,
    string[]? Fields = null, bool? NumberedColumns = null, int[]? Workdays = null);
/// <summary>实际输出名称与稳定字段语义, 允许同一语义有多个已有别名.</summary>
public sealed record ScaffoldCalendarColumn(string Name, string Field);
/// <summary>指定公历日期的业务工作日覆盖, 不表示官方调休数据.</summary>
public sealed record WorkdayOverride(string Date, bool IsWorkday, string Name);
/// <summary>纯业务日期表数据, 类型使用模型标准名称, 不含磁盘能力.</summary>
public sealed record ScaffoldCalendarData(string[] Columns, string[] DataTypes, IReadOnlyList<string[]> Rows, string DateColumn);
/// <summary>宿主核验的必需字段及保护已有引用的原因.</summary>
public sealed record ScaffoldCalendarLockedField(string Field, string Reason);
/// <summary>宿主核验的日期能力与实际输出别名, 不从网页表名推断归属.</summary>
public sealed record ScaffoldCalendarCapability(string TableName, string Mode, string[] Fields, IReadOnlyList<ScaffoldCalendarColumn> Columns,
    IReadOnlyList<ScaffoldCalendarLockedField> LockedFields, string? DateColumn = null, string? Warning = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ModelConfigurationCalendarColumn>? OutputColumns = null,
    [property: System.Text.Json.Serialization.JsonIgnore] string? FunctionName = null);
