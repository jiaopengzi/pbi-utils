// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Configuration\ModelConfigurationContext.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 投影中性的模型配置展示上下文, 可写标记只来自真实快照与原策略.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Host.Bridge.Configuration;

/// <summary>展示能力由宿主快照决定, TargetId 只能用于当前文档, 不接受路径授权.</summary>
public sealed record ModelConfigurationContext(string TargetId, ProjectSnapshot Project, ScaffoldTemplateDetails Details,
    bool CanWrite, bool CanGenerateSample, bool IsCreatedTarget, bool NoChanges = false, bool CanUseMeasureConfig = false,
    TemplateMeasureConfiguration? MeasureConfiguration = null, string? MeasureConfigurationRevision = null,
    TemplateMeasureDefinition[]? ConfigMeasures = null, string? MeasureConfigUnavailableReason = null)
{
    /// <summary>由 snapshot 生成引擎目录, assets 提供已核实的磁盘配置; measureSourceRead=false 表示恢复后尚未探测来源, 不伪报目录失效.</summary>
    internal static ModelConfigurationContext Create(string id, ProjectSnapshot snapshot, bool isCreated, ModelConfigurationAssetContext? assets = null,
        bool measureSourceRead = true)
    {
        var tables = snapshot.Model.Tables.Select(table => new ScaffoldTemplateTable(table.Name,
            table.Columns.Select(column => new ScaffoldTemplateColumn(column.Name, column.DataType)).ToArray(),
            snapshot.Model.Measures.Where(measure => measure.Table == table.Name).Select(measure => measure.Name).ToArray(),
            table.Columns.Any(column => column.DataType == "DateTime"))).ToArray();
        var date = tables.FirstOrDefault(table => table.IsDate);
        var measures = tables.FirstOrDefault(table => table.Measures.Count > 0)?.Name;
        var capabilities = snapshot.Model.Tables.Select(table => table.CalendarCapability).OfType<ScaffoldCalendarCapability>().ToArray();
        var details = new ScaffoldTemplateDetails(tables, date?.Name, date?.Columns.FirstOrDefault(column => column.DataType == "DateTime")?.Name, measures,
            ScaffoldCalendarFields.All, capabilities, snapshot.Model.Periods);
        var writable = !ModelAccessPolicy.IsReadOnly(snapshot) && !snapshot.HasPendingChanges && snapshot.SaveResult?.ErrorCode is null;
        var hasFile = Path.GetExtension(snapshot.ProjectPath).Equals(".pbip", StringComparison.OrdinalIgnoreCase);
        var available = writable && assets?.CanUseMeasureConfig == true;
        var engine = snapshot.Model.TemplateMeasureConfiguration;
        var file = available ? assets?.ExistingConfiguration : null;
        var effective = engine;
        if (writable && measureSourceRead && engine is not null)
            effective = engine.Mode == "config" && file is not null ? file : engine with { Mode = "tmdl" };
        return new(id, snapshot, details, writable, writable && hasFile, isCreated, CanUseMeasureConfig: available,
            MeasureConfiguration: effective, MeasureConfigurationRevision: available ? assets?.ConfigurationRevision : null,
            ConfigMeasures: file?.Measures, MeasureConfigUnavailableReason: writable && measureSourceRead && !available ? assets?.UnavailableReason ?? "layout" : null);
    }
}
