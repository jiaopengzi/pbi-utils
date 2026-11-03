// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\TomModelMetadata.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为明确生成的 TOM 导入对象写入一致类型元数据, 不修改分区或提交模型.

using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Models;

/// <summary>仅处理调用方明确生成的导入列及文本参数, 不推断用户定义.</summary>
internal static class TomModelMetadata
{
    /// <summary>仅本工具明确重写的类型化导入分区调用; M转datetime后旧Date提示会被Desktop删除.</summary>
    /// <param name="table">调用方已确认类型并拥有的导入表, 不扫描模型中的其他表.</param>
    internal static void ImportedDateMetadata(TOM.Table table)
    {
        foreach (var column in table.Columns.OfType<TOM.DataColumn>().Where(column => column.DataType == TOM.DataType.DateTime))
            if (column.Annotations.Find("UnderlyingDateTimeDataType") is { Value: "Date" } oldDate) column.Annotations.Remove(oldDate);
    }

    /// <summary>项目根是本工具明确生成的Text参数, 提前记录Desktop会推导的相同结果类型.</summary>
    /// <param name="expression">调用方明确生成的文本参数, 不根据内容推断用户查询类型.</param>
    internal static void TextParameterMetadata(TOM.NamedExpression expression)
    {
        if (expression.Annotations.Find("PBI_ResultType") is { } previous) expression.Annotations.Remove(previous);
        expression.Annotations.Add(new TOM.Annotation { Name = "PBI_ResultType", Value = "Text" });
    }
}
