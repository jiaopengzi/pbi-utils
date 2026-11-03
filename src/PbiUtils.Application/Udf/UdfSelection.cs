// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfSelection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建和持续配置共用的精确 UDF 选择及来源基线契约.

namespace PbiUtils.Application.Udf;

/// <summary>精确函数选择, 明确预发行 tag 及其固定依赖自动解析; 缺省版本只取稳定版, 不接受下载 URL.</summary>
public sealed record UdfSelection(string Source, string PackageId, string? Version, string[] Functions, string? Baseline = null, string? SourceBaseline = null, string? CatalogBaseline = null);
