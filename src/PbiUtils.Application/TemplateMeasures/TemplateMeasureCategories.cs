// FilePath    : pbi-utils\src\PbiUtils.Application\TemplateMeasures\TemplateMeasureCategories.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享文本模板度量值标准分类与模型元数据映射, 拒绝未声明的应用扩展.

using PbiUtils.Core;

namespace PbiUtils.Application.TemplateMeasures;

/// <summary>只定义文本模板度量值的分类, 不代表所有 TOM 自定义字符串或数值度量值分类.</summary>
public static class TemplateMeasureCategories
{
    /// <summary>官方 Desktop 对 Text/Variant 度量值提供的分类, ImageUrl 保持既有产品协议拼写.</summary>
    public static IReadOnlyList<string> ModelCategories { get; } = Array.AsReadOnly<string>([
        "Uncategorized", "Address", "Place", "City", "County", "StateOrProvince", "PostalCode",
        "Country", "Continent", "Latitude", "Longitude", "WebUrl", "ImageUrl", "Barcode"
    ]);

    /// <summary>全部可选业务分类与标准模型分类使用同一只读目录.</summary>
    public static IReadOnlyList<string> All { get; } = ModelCategories;

    /// <summary>按协议精确拼写判断业务分类, 拒绝未知字符串、空白和未声明别名.</summary>
    public static bool IsSupported(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>返回应写入模型的标准分类, 不将已移除的扩展或未知值降级为未分类.</summary>
    /// <param name="value">使用固定大小写协议拼写的分类.</param>
    /// <returns>经过校验的原始标准分类.</returns>
    /// <exception cref="AppException">分类不受支持时返回稳定的输入错误.</exception>
    public static string ToModelCategory(string value)
    {
        if (!IsSupported(value)) throw new AppException("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
        return value;
    }
}
