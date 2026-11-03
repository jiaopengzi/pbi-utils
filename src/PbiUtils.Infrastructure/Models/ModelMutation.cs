// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\ModelMutation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共用 TOM 内存修改, 不提交引擎或写文件.

using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.TemplateMeasures;

namespace PbiUtils.Infrastructure.Models;

/// <summary>只修改调用方拥有的内存模型, 保留模板和 UDF 的既有约束.</summary>
public static class ModelMutation
{
    /// <summary>修改批准的单对象或度量值批次; UDF 由 TOM 验证兼容性, 不升级模型.</summary>
    public static void Apply(TOM.Database db, ChangeRequest request)
    {
        var model = db.Model;
        if (request.Operation == "configuration.apply") ModelConfigurationMutation.Apply(db, request.Configuration ?? throw new AppException("CONFIGURATION_PLAN_REQUIRED", "配置必须由宿主准备", 3));
        else if (request.Operation == "measure.batch") ApplyMeasures(db, MeasureBatch.Parse(request.Value));
        else if (request.Operation == "table.deleteBatch") ApplyTableDeletion(model, TableDeleteBatch.Parse(request.Value));
        else if (request.Operation == "udf.deleteUnused") UdfModelLint.Delete(db, UdfDeleteBatch.Parse(request.Value));
        else if (request.Operation == "calendar.create") CalendarModelWriter.Calendar(model, request.ObjectName, CalendarTemplates.Parse<CalendarOptions>(request.Value));
        else if (request.Operation == "period.generate") CalendarModelWriter.Periods(model, request.ObjectName, CalendarTemplates.Parse<PeriodOptions>(request.Value));
        else if (request.Operation == "udf.set")
        {
            if (string.IsNullOrWhiteSpace(request.Value)) throw new AppException("VALIDATION_ERROR", "UDF 定义不可为空");
            var function = model.Functions.Find(request.ObjectName);
            if (function is null)
            {
                function = new TOM.Function { Name = request.ObjectName };
                try { model.Functions.Add(function); }
                catch (TOM.CompatibilityViolationException)
                {
                    throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "当前 TOM SDK 不支持在此兼容级别保存模型函数。这不代表查询内 DEFINE FUNCTION 无法执行; 未升级模型", 3);
                }
            }
            function.Expression = request.Value;
        }
        else
        {
            var table = model.Tables.Find(request.Operation == "table.set" ? request.ObjectName : request.Table ?? "")
                ?? throw new AppException("OBJECT_NOT_FOUND", "目标表不存在", 3);
            if (request.Operation == "table.set") table.Description = request.Value;
            else
            {
                var measure = table.Measures.Find(request.ObjectName) ?? throw new AppException("OBJECT_NOT_FOUND", "目标度量值不存在", 3);
                switch (request.Property)
                {
                    case "description": measure.Description = request.Value; break;
                    case "displayFolder": measure.DisplayFolder = request.Value; break;
                    case "formatString": measure.FormatString = request.Value; break;
                    case "expression":
                        if (string.IsNullOrWhiteSpace(request.Value)) throw new AppException("VALIDATION_ERROR", "表达式不可为空");
                        measure.Expression = request.Value;
                        break;
                }
            }
        }
    }

    /// <summary>先整体核验准确表和 SDK 关系端点, 再在本轮原对象中清理关联关系与所选表, 不提交引擎.</summary>
    /// <param name="model">本轮唯一操作上下文拥有的模型, 不创建第二个 SDK 操作模型.</param>
    /// <param name="batch">已经绑定身份和完整基线的严格删除集合.</param>
    /// <exception cref="AppException">目标缺失或关系端点无法安全识别时, 在任何修改前整批拒绝.</exception>
    private static void ApplyTableDeletion(TOM.Model model, TableDeleteBatch batch)
    {
        var tables = batch.Tables.Select(name => model.Tables.Find(name)
            ?? throw new AppException("OBJECT_NOT_FOUND", "目标表不存在", 3)).ToHashSet();
        var relationships = model.Relationships.ToArray();
        if (relationships.Any(relationship => relationship.FromTable is null || relationship.ToTable is null))
            throw new AppException("TABLE_RELATIONSHIP_UNSUPPORTED", "模型包含无法安全核验端点的关系, 未删除表", 3);
        // 先移除关系, 显式覆盖所选表作为任一端的情况; 保留全部无关关系及原对象.
        foreach (var relationship in relationships.Where(relationship => tables.Contains(relationship.FromTable) || tables.Contains(relationship.ToTable)))
            model.Relationships.Remove(relationship);
        foreach (var table in tables) model.Tables.Remove(table);
    }

    /// <summary>修改前按整批最终状态检查目标、重名和存活引用; 只读投影通过后才修改原对象.</summary>
    /// <param name="database">调用方拥有且尚未提交的完整模型, 不连接外部引擎.</param>
    /// <param name="batch">已解析的严格批次; null 属性不改, 空字符串明确清空.</param>
    /// <exception cref="AppException">MEASURE_REFERENCED 表示仍有引用或不能证明安全; 其他错误保持原操作契约.</exception>
    private static void ApplyMeasures(TOM.Database database, MeasureBatch batch)
    {
        ModelMeasureBatchValidation.RequireValid(database.Model, batch);
        ApplyMeasureItems(database.Model, batch);
    }

    /// <summary>按原条目顺序应用度量批次, 由调用方持有私有候选或已经通过完整引用预检的模型.</summary>
    /// <param name="model">本轮唯一拥有者提供的模型, 本方法不执行提交或保存.</param>
    /// <param name="batch">严格批次; 重名、缺失目标与 SDK 校验错误直接返回.</param>
    private static void ApplyMeasureItems(TOM.Model model, MeasureBatch batch)
    {
        foreach (var item in batch.Items)
        {
            var table = model.Tables.Find(item.Table) ?? throw new AppException("OBJECT_NOT_FOUND", "目标表不存在", 3);
            var measure = table.Measures.Find(item.Name);
            if (item.Action == "create")
            {
                if (model.Tables.Any(t => t.Measures.Any(m => string.Equals(m.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                    || table.Columns.Any(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new AppException("OBJECT_CONFLICT", "度量值名称已存在, 或与所属表的列重名", 3);
                measure = new TOM.Measure { Name = item.Name };
                table.Measures.Add(measure);
            }
            else if (measure is null) throw new AppException("OBJECT_NOT_FOUND", "目标度量值不存在", 3);
            if (item.Action == "delete") { table.Measures.Remove(measure!); continue; }
            if (item.Expression is not null) measure!.Expression = item.Expression;
            if (item.Description is not null) measure!.Description = item.Description;
            if (item.FormatString is not null) measure!.FormatString = item.FormatString;
            if (item.DisplayFolder is not null) measure!.DisplayFolder = item.DisplayFolder;
            if (item.IsHidden is { } hidden) measure!.IsHidden = hidden;
        }
    }

}
