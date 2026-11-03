// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\ScaffoldCalendarCapabilities.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从完整模型定义核验日期编辑能力, 不以日期列或名称代替生成归属.

using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>能力只供展示, 真正写入仍需完整身份、基线及对象归属检查.</summary>
public static class ScaffoldCalendarCapabilities
{
    /// <summary>精确内置模板允许首次转换, 已有表必须有有效的完整生成归属和日期选项.</summary>
    public static ScaffoldCalendarCapability? Describe(TOM.Table table, bool freshBuiltin = false)
    {
        var annotation = table.Annotations.Find("PbiUtils.Configuration.Calendar.v1");
        if (!freshBuiltin && annotation is null && !table.Columns.Any(column => column.DataType == TOM.DataType.DateTime)) return null;
        var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
        var mapping = columns.Select(column => new ScaffoldCalendarColumn(column.Name, Semantic(column.Name))).ToArray();
        var verified = freshBuiltin || annotation is not null && ModelConfigurationOwnership.IsOwned(table, table.Annotations) && KnownVersion(annotation.Value);
        if (verified && !freshBuiltin)
        {
            try
            {
                _ = ScaffoldCalendarWriter.ReadOptions(table);
                var sourceMapping = ScaffoldCalendarWriter.ReadMapping(table);
                mapping = [.. columns.Select(column => new ScaffoldCalendarColumn(column.Name,
                    sourceMapping.Single(value => value.Name == column.SourceColumn).Field))];
            }
            catch (AppException) { verified = false; }
        }
        var known = mapping.All(column => ScaffoldCalendarFields.All.Any(field => field.Id == column.Field));
        if (!verified || !known)
            return new(table.Name, "existing", [], [], [], Warning: "已有表没有可验证的日期生成归属, 请使用新的日期表名称");
        var fields = mapping.Select(column => column.Field).Distinct(StringComparer.Ordinal).ToArray();
        var required = freshBuiltin ? ["Dates"]
            : CalendarColumnProtection.RequiredFields(table).Order(StringComparer.Ordinal).ToArray();
        var key = columns.FirstOrDefault(column => column.IsKey && mapping.Any(value => value.Name == column.Name && value.Field == "Dates"))?.Name
            ?? mapping.FirstOrDefault(column => column.Field == "Dates")?.Name;
        return new(table.Name, freshBuiltin ? "new" : "existing", freshBuiltin ? required : fields, mapping,
            required.Select(field => new ScaffoldCalendarLockedField(field, freshBuiltin ? "templateReference" : "existingReference")).ToArray(), key, OutputColumns: columns.Select((column, index) =>
                new ModelConfigurationCalendarColumn(column.Name, column.SourceColumn, mapping[index].Field, false)).ToArray(),
            FunctionName: freshBuiltin ? null : ScaffoldCalendarWriter.FunctionBinding(table)?.FunctionName);
    }

    /// <summary>只接受固定目录的精确语义, 历史错误编号不参与判断.</summary>
    public static string Semantic(string name)
    {
        if (name.StartsWith('C') && name.IndexOf('_') is var index && index > 0) name = name[(index + 1)..];
        return name switch { "日期" => "Dates", "天" => "Day", "月份" => "MonthM", "年月" => "YearMonthM", "年" => "FY00", _ => name };
    }

    /// <summary>未知或损坏选项不能赋予编辑能力.</summary>
    private static bool KnownVersion(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            return (root.TryGetProperty("SchemaVersion", out var version) || root.TryGetProperty("schemaVersion", out version))
                && version.TryGetInt32(out var number) && number == 3;
        }
        catch (JsonException) { return false; }
    }
}
