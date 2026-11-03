// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\ModelMeasureBatchValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对原模型的完整定义执行度量批次只读目标和引用预检, 不创建 SDK 模型副本.

using PbiUtils.Application.Models.Editing;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Models;

/// <summary>扫描视图只表达本批最终定义, 不反序列化为 TOM、提交引擎或充当另一个操作模型.</summary>
internal static class ModelMeasureBatchValidation
{
    /// <summary>先核验全部目标和顺序冲突, 再用最终属性扫描删除依赖, 任何失败均不修改原对象.</summary>
    /// <param name="model">专属连接或文件事务拥有的完整原模型.</param>
    /// <param name="batch">已经严格解析并绑定基线的操作集合.</param>
    /// <exception cref="AppException">目标不存在、名称冲突或存活引用无法安全核实.</exception>
    internal static void RequireValid(TOM.Model model, MeasureBatch batch)
    {
        var definition = ModelConfigurationOwnership.Definition(model);
        var tables = definition["tables"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        foreach (var item in batch.Items)
        {
            var table = model.Tables.Find(item.Table) ?? throw new AppException("OBJECT_NOT_FOUND", "目标表不存在", 3);
            var tableDefinition = tables.Single(value => Text(value["name"]) == table.Name);
            var measures = tableDefinition["measures"]?.AsArray();
            if (measures is null) { measures = []; tableDefinition["measures"] = measures; }
            var measure = measures.OfType<JsonObject>().SingleOrDefault(value => string.Equals(Text(value["name"]), item.Name, StringComparison.OrdinalIgnoreCase));
            if (item.Action == "create")
            {
                if (tables.SelectMany(value => value["measures"]?.AsArray().OfType<JsonObject>() ?? [])
                    .Any(value => string.Equals(Text(value["name"]), item.Name, StringComparison.OrdinalIgnoreCase))
                    || table.Columns.Any(column => string.Equals(column.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new AppException("OBJECT_CONFLICT", "度量值名称已存在, 或与所属表的列重名", 3);
                measure = new JsonObject { ["name"] = item.Name };
                measures.Add(measure);
            }
            else if (measure is null) throw new AppException("OBJECT_NOT_FOUND", "目标度量值不存在", 3);
            if (item.Action == "delete") { measures.Remove(measure); continue; }
            if (item.Expression is not null) measure!["expression"] = item.Expression;
            if (item.Description is not null) measure!["description"] = item.Description;
            if (item.FormatString is not null) measure!["formatString"] = item.FormatString;
            if (item.DisplayFolder is not null) measure!["displayFolder"] = item.DisplayFolder;
            if (item.IsHidden is { } hidden) measure!["isHidden"] = hidden;
        }
        var removed = batch.Items.Where(item => item.Action == "delete")
            .Select(item => model.Tables[item.Table].Measures[item.Name]).ToArray();
        try { ScaffoldPeriodProtection.RequireUnreferenced(model, removed, finalDefinition: definition); }
        catch (AppException error) when (error.Code == "CONFIGURATION_PERIOD_REFERENCED")
        {
            throw new AppException("MEASURE_REFERENCED", "待删除的度量值仍被模型引用, 或其依赖无法安全核实; 请先处理引用后重试", 3);
        }
    }

    /// <summary>读取扫描视图中的已知 SDK 名称, 不猜测未知节点结构.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
