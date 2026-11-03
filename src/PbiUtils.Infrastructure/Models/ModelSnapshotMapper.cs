// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\ModelSnapshotMapper.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从只读 TOM 数据库映射模型摘要, 不依赖模型修改入口或执行保存.

using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.TemplateMeasures;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Models;

/// <summary>读模型、导出及编辑预览共享的摘要映射, 不输出连接字符串或凭据.</summary>
public static class ModelSnapshotMapper
{
    /// <summary>映射当前内存模型, 不输出连接字符串或凭据.</summary>
    /// <param name="db">调用方拥有的完整只读 TOM 数据库, 不建立编辑会话.</param>
    /// <param name="displayName">可选宿主核实显示名, 未提供时使用数据库名称.</param>
    /// <returns>展示及读取用的结构摘要, 不替代完整定义指纹或保存证明.</returns>
    public static ModelInfo Map(TOM.Database db, string? displayName = null)
    {
        var m = db.Model;
        return new(displayName ?? db.Name, db.CompatibilityLevel, m.Culture,
            m.Tables.Select(t => new TableInfo(t.Name, t.Description, t.IsHidden,
                t.Columns.Select(c => new ColumnInfo(c.Name, c.Type.ToString(), c.DataType.ToString(), c.IsHidden, c.Description)).ToArray(),
                t.Partitions.Select(p => p.Source?.GetType().Name ?? "unknown").ToArray(),
                ScaffoldCalendarCapabilities.Describe(t))).ToArray(),
            m.Tables.SelectMany(t => t.Measures.Select(v => new MeasureInfo(t.Name, v.Name, v.Expression, v.Description, v.FormatString, v.DisplayFolder, v.IsHidden))).ToArray(),
            m.Functions.Select(f => new FunctionInfo(f.Name, f.Expression, f.Description)).ToArray(), m.Relationships.Count, m.Roles.Count,
            ScaffoldPeriodConfiguration.Read(m), TemplateMeasureConfigurationStorage.Display(m));
    }
}
