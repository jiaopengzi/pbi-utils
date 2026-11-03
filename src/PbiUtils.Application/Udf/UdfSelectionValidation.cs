// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfSelectionValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建与持续配置共用精确 UDF 选择段的输入校验.

using PbiUtils.Application.Input;
using PbiUtils.Application.Models;
using PbiUtils.Core;

namespace PbiUtils.Application.Udf;

/// <summary>函数选择的有界身份和来源基线规则, 不访问目录或网络.</summary>
public static class UdfSelectionValidation
{
    /// <summary>验证来源、唯一包选择、版本文本与函数名称; null 表示未选择此段.</summary>
    /// <param name="selections">用户选择的 UDF 包及函数集合或 null.</param>
    /// <exception cref="AppException">名称、来源身份或输入预算无效.</exception>
    public static void Validate(UdfSelection[]? selections)
    {
        if (selections is null) return;
        if (selections.Length > 128 || selections.Any(item => item is null)
            || selections.Select(item => item.Source + ":" + item.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selections.Length) throw Invalid();
        foreach (var item in selections)
        {
            if (!UdfValidation.IsSourceId(item.Source) || item.Source.StartsWith("source:", StringComparison.Ordinal) && !InputValidation.IsDigest(item.SourceBaseline)
                || item.CatalogBaseline is not null && !InputValidation.IsDigest(item.CatalogBaseline)
                || string.IsNullOrWhiteSpace(item.PackageId) || item.PackageId.Length > 200
                || item.PackageId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':'))
                || item.Functions is null || item.Functions.Length is < 1 or > 512 || (item.Version is not null && !InputValidation.Text(item.Version, 100))) throw Invalid();
            foreach (var name in item.Functions) ModelNames.Validate(name);
        }
    }

    /// <summary>保留原创建及配置入口的稳定输入错误, 不包含任意用户内容.</summary>
    private static AppException Invalid() => new("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
}
