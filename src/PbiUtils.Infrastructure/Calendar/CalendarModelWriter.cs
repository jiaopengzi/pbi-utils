// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarModelWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将共享日期与期间模板写入 staging 模型, 拒绝名称碰撞和人工修改覆盖.

using PbiUtils.Application.Models;
using PbiUtils.Application.Calendar;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>只操作调用方拥有的离线模型, 不刷新、不创建业务关系.</summary>
internal static class CalendarModelWriter
{
    private const string Stamp = "PbiUtils.Generated.Hash";

    /// <summary>创建计算日期表, 显式日期键/Time 标记与年月排序; 重复相同内容为无变更.</summary>
    internal static void Calendar(TOM.Model model, string name, CalendarOptions options)
    {
        ModelNames.Validate(name);
        var expression = CalendarTemplates.Calendar(options);
        var table = new TOM.Table { Name = name, DataCategory = "Time", Description = "pbi-utils 独立公历 v1; 周一开始, 不表示 ISO 周; 不含农历/节假日." };
        table.Partitions.Add(new TOM.Partition { Name = name, Mode = TOM.ModeType.Import, Source = new TOM.CalculatedPartitionSource { Expression = expression } });
        foreach (var column in new[] { "Date", "Year", "MonthNo", "Quarter", "YearMonthKey", "YearMonth", "StartOfWeek" })
        {
            var date = column is "Date" or "StartOfWeek";
            var item = new TOM.CalculatedTableColumn
            {
                Name = column,
                SourceColumn = "[" + column + "]",
                DataType = date ? TOM.DataType.DateTime : column == "YearMonth" ? TOM.DataType.String : TOM.DataType.Int64,
                IsNameInferred = false,
                IsDataTypeInferred = false,
                SummarizeBy = TOM.AggregateFunction.None,
                FormatString = date ? "yyyy-MM-dd" : "",
                IsKey = column == "Date"
            };
            if (date) item.Annotations.Add(new TOM.Annotation { Name = "UnderlyingDateTimeDataType", Value = "Date" });
            table.Columns.Add(item);
        }
        table.Columns["YearMonth"].SortByColumn = table.Columns["YearMonthKey"];
        var expected = Fingerprint(table);
        if (model.Tables.Find(name) is { } existing)
        {
            if (existing.Annotations.Find(Stamp)?.Value != expected || Fingerprint(existing) != expected)
                throw new AppException("GENERATED_OBJECT_CONFLICT", "同名日期表已存在或被修改; 不会覆盖, 请映射已有日期表或使用新名称", 3);
            return;
        }
        table.Annotations.Add(new TOM.Annotation { Name = Stamp, Value = expected });
        model.Tables.Add(table);
    }

    /// <summary>验证实际基础指标和标记的日期键; 显式目标表, 全部冲突先检查再修改.</summary>
    internal static void Periods(TOM.Model model, string baseMeasure, PeriodOptions options)
    {
        var generated = CalendarTemplates.Periods(baseMeasure, options, model.Tables.Select(table => table.Name));
        var sources = model.Tables.SelectMany(t => t.Measures).Where(m => m.Name.Equals(baseMeasure, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length != 1) throw new AppException("BASE_MEASURE_NOT_FOUND", "基础指标必须唯一存在", 3);
        if (sources[0].Annotations.Find(Stamp) is not null) throw new AppException("GENERATED_BASE_UNSUPPORTED", "请选择原始基础指标, 不重复使用生成结果", 3);
        var dateTable = model.Tables.Find(options.DateTable);
        var dateColumn = dateTable?.Columns.Find(options.DateColumn);
        if (dateTable?.DataCategory != "Time" || dateColumn?.DataType != TOM.DataType.DateTime || !dateColumn.IsKey)
            throw new AppException("DATE_MAPPING_REQUIRED", "请选择已标记日期表的日期键; 离线无法证明日期连续性, 仍需 Desktop 数据验收", 3);
        var target = model.Tables.Find(options.MeasureTable) ?? throw new AppException("MEASURE_TABLE_NOT_FOUND", "输出度量值表不存在", 3);
        List<TOM.Measure> additions = [];
        foreach (var definition in generated)
        {
            var item = new TOM.Measure
            {
                Name = definition.Name,
                Expression = definition.Expression,
                FormatString = definition.FormatString,
                DisplayFolder = definition.Folder,
                Description = $"pbi-utils {definition.TemplateId}; 基础指标={baseMeasure}; 当前筛选期间; 未证明与原51状态等价."
            };
            var expected = Fingerprint(item);
            var existing = model.Tables.SelectMany(t => t.Measures).FirstOrDefault(m => m.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Table != target || existing.Annotations.Find(Stamp)?.Value != expected || Fingerprint(existing) != expected)
                    throw new AppException("GENERATED_OBJECT_CONFLICT", "生成指标名称冲突或内容被修改, 不覆盖现有对象", 3);
                continue;
            }
            item.Annotations.Add(new TOM.Annotation { Name = Stamp, Value = expected });
            additions.Add(item);
        }
        foreach (var item in additions) target.Measures.Add(item);
    }

    /// <summary>复制元数据并移除自身指纹, 因此人工修改任何已序列化属性都会触发冲突.</summary>
    private static string Fingerprint(TOM.MetadataObject item)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(TOM.JsonSerializer.SerializeObject(item))!.AsObject();
        if (json["annotations"] is System.Text.Json.Nodes.JsonArray annotations)
        {
            foreach (var annotation in annotations.Where(a => a?["name"]?.GetValue<string>() == Stamp).ToArray()) annotations.Remove(annotation);
            if (annotations.Count == 0) json.Remove("annotations");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
