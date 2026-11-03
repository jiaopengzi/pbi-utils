// FilePath    : pbi-utils\src\PbiUtils.Application\TemplateMeasures\TemplateMeasureValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建、持续配置与偏好共用的模板度量校验和默认配置.

using PbiUtils.Application.Input;
using PbiUtils.Application.Models;
using PbiUtils.Core;

namespace PbiUtils.Application.TemplateMeasures;

/// <summary>独立校验模板度量段, 不需要创建请求或访问外部环境.</summary>
public static class TemplateMeasureValidation
{
    /// <summary>验证当前实际支持的模板度量存储模式, 不访问模型或配置文件.</summary>
    /// <param name="mode">精确的 tmdl 或 config 模式标识.</param>
    /// <exception cref="AppException">模式不受支持.</exception>
    public static void ValidateMode(string mode)
    {
        if (mode is not ("tmdl" or "config")) throw Invalid();
    }

    /// <summary>验证名称唯一性、支持类别和原有 UTF-8 文本预算; null 表示未选择此段.</summary>
    /// <param name="measures">当前入口提供的完整模板度量定义或 null.</param>
    /// <exception cref="AppException">名称无效或定义超过共享输入边界.</exception>
    public static void Validate(TemplateMeasureDefinition[]? measures)
    {
        if (measures is null) return;
        if (measures.Length > 128 || measures.Any(item => item is null)
            || measures.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != measures.Length) throw Invalid();
        foreach (var item in measures)
        {
            ModelNames.Validate(item.Name);
            if (!TemplateMeasureCategories.IsSupported(item.DataCategory)
                || !InputValidation.Text(item.Value, 32768) || !InputValidation.Text(item.Description, 8192) || !InputValidation.Text(item.DisplayFolder, 200)) throw Invalid();
        }
    }

    /// <summary>构造独立默认配置, 不共享可变数组或写入用户偏好.</summary>
    /// <returns>包含当前六个默认业务度量的新数组.</returns>
    public static TemplateMeasureDefinition[] DefaultMeasures() =>
    [
        new("refresh_time", "", "Uncategorized", true, "数据刷新时间", "模板"),
        new("version", "v1.0.0", "Uncategorized", true, "项目版本", "模板"),
        new("author", "", "Uncategorized", true, "项目作者", "模板"),
        new("weburl_text", "焦棚子", "Uncategorized", true, "网站文字", "模板"),
        new("weburl", "https://jiaopengzi.com", "WebUrl", true, "网站链接", "模板"),
        new("weburl_tip", "https://jiaopengzi.com", "Uncategorized", true, "网站提示", "模板")
    ];

    /// <summary>保留原创建及配置入口的稳定输入错误, 不包含任意用户内容.</summary>
    private static AppException Invalid() => new("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
}
