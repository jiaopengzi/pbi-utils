// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\ScaffoldContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义桌面和 CLI 共用的脚手架业务参数及模板读取边界.

using PbiUtils.Application.SampleData;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Udf;

namespace PbiUtils.Application.Scaffolding;

/// <summary>全部选择及辅助表分组进入同一不可变计划, 不接受模板或输出文件路径; 分组缺省沿用目录, 空字符串表示不分组.</summary>
public sealed record ScaffoldRequest(int SchemaVersion, string TemplateId, string TemplateBaseline, string ProjectName, string Locale,
    TemplateMeasureDefinition[]? Measures, SampleDataOptions? Sample, ScaffoldCalendarOptions? Calendar,
    ScaffoldPeriodOptions? Periods, UdfSelection[]? Udfs, bool GitInitialize = false, string[]? TemplateQueries = null,
    Dictionary<string, string>? TemplateQueryGroups = null, string MeasuresMode = "tmdl");
/// <summary>可显示模板信息, 不将任意路径授权交给网页.</summary>
public sealed record PbipTemplateInfo(string Id, string Name, string Source, string Baseline, string Description, string[] Warnings);
/// <summary>后台模板快照文件, RelativePath 必须位于模板根内.</summary>
public sealed record ScaffoldTemplateFile(string RelativePath, byte[] Content);
/// <summary>冻结的模板读取结果, 不得作为桥接响应序列化.</summary>
public sealed record ResolvedPbipTemplate(PbipTemplateInfo Info, string EntryFile, IReadOnlyList<ScaffoldTemplateFile> Files);
/// <summary>供界面显式选择日期角色的真实列信息.</summary>
public sealed record ScaffoldTemplateColumn(string Name, string DataType);
/// <summary>真实模型表及基础指标, 不包含任意数据源连接内容.</summary>
public sealed record ScaffoldTemplateTable(string Name, IReadOnlyList<ScaffoldTemplateColumn> Columns, IReadOnlyList<string> Measures, bool IsDate);
/// <summary>从已绑定模板读取的元数据默认值, 用户可明确改选.</summary>
public sealed record ScaffoldTemplateDetails(IReadOnlyList<ScaffoldTemplateTable> Tables, string? DefaultDateTable, string? DefaultDateColumn, string? DefaultMeasureTable,
    IReadOnlyList<ScaffoldCalendarField>? CalendarFields = null, IReadOnlyList<ScaffoldCalendarCapability>? CalendarCapabilities = null,
    ScaffoldPeriodOptions? Periods = null);
/// <summary>受限模板来源, 网页只通过宿主分配的 ID 读取.</summary>
public interface IPbipTemplateCatalog
{
    /// <summary>发现内置及固定用户目录的模板, 返回逐项诊断.</summary>
    Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken);
    /// <summary>读取唯一模板并冻结文件快照, 失效或不安全时明确失败.</summary>
    Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken);
}
