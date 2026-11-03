// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\ModelEditValueLimits.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一当前模型和显式副本的表说明应用输入上限, 不缩减代码容量.

using PbiUtils.Core;

namespace PbiUtils.Application.Models.Editing;

/// <summary>应用自身的字段编辑规则, 不将其描述为 Power BI 或 TOM 的统一上限.</summary>
internal static class ModelEditValueLimits
{
    private const int TableDescriptionMaxLength = 2000;

    /// <summary>在准备修改前检查表说明长度, 其他属性沿用其各自的现有边界.</summary>
    /// <param name="operation">已由调用方核实的编辑操作.</param>
    /// <param name="property">已通过白名单的属性键.</param>
    /// <param name="value">已检查非 null 的原始输入, 不截断或改写已有内容.</param>
    /// <exception cref="AppException">表说明超过应用上限时返回 VALIDATION_ERROR, 不进入模型或文件准备.</exception>
    internal static void Validate(string operation, string property, string value)
    {
        if (operation == "table.set" && property == "description" && value.Length > TableDescriptionMaxLength)
            throw new AppException("VALIDATION_ERROR", "表说明最多支持 2000 字符, 请缩短后应用");
    }
}
