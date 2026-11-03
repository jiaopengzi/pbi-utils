// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfLint.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 未使用 UDF 只读分析结果与绑定当前完整基线的删除批次.

using PbiUtils.Core;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Udf;

/// <summary>仅完整分析成功时提供可删名称; 阻塞时不返回猜测候选, 不包含表达式.</summary>
public sealed record UdfLintResult(string ProjectId, string BaselineId, IReadOnlyList<string> Unused, int TotalCount, bool Complete);

/// <summary>受信完整 TOM 对象的只读分析能力, 不修改对象、文件或引擎.</summary>
public interface IUdfLintDraft
{
    Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token);
}

/// <summary>绑定当前目标与基线的准确删除集合, 保存时仍须重新分析完整模型.</summary>
public sealed record UdfDeleteBatch(string ProjectId, string BaselineId, IReadOnlyList<string> Names)
{
    /// <summary>拒绝空集、重复、未知字段和超限请求; 失败不产生任何删除.</summary>
    public static UdfDeleteBatch Parse(string value)
    {
        var result = StructuredInput.Parse<UdfDeleteBatch>(value);
        if (string.IsNullOrWhiteSpace(result.ProjectId) || result.ProjectId.Length > 200
            || string.IsNullOrWhiteSpace(result.BaselineId) || result.BaselineId.Length > 200
            || result.Names is not { Count: > 0 and <= 8192 }
            || result.Names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 200)
            || result.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Names.Count)
            throw new AppException("VALIDATION_ERROR", "请选择 1–8192 个未使用函数并重新分析模型");
        return result;
    }
}
