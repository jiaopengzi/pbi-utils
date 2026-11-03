// FilePath    : pbi-utils\src\PbiUtils.Application\Code\CodeFormatting.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : CLI 与本地 MCP 共用的有界本地 DAX/M 格式化, 不执行表达式.

using PbiUtils.Application.Code.PowerQuery;
using PbiUtils.Application.Code.Dax;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Application.Code;

/// <summary>不依赖模型或外部网络的格式化入口.</summary>
public static class CodeFormatting
{
    /// <summary>格式化 1–16000 字符的 DAX/M; DAX 固定共享 Long 策略, 输出和 JSON 均有界且不截断.</summary>
    /// <param name="language">dax 或 m, 不接受其他代码或命令语言.</param>
    /// <param name="code">需要保留词元和字符串的原始代码, 不执行任何表达式.</param>
    /// <param name="token">在共享扫描、布局和验证阶段传播取消.</param>
    /// <returns>完整的格式化代码, 不返回局部结果.</returns>
    /// <exception cref="AppException">语言、语法或输入输出预算无效.</exception>
    /// <exception cref="OperationCanceledException">格式化被调用方取消.</exception>
    public static string Format(string language, string code, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000) throw new AppException("VALIDATION_ERROR", "代码长度必须为 1–16000 字符");
        var result = language switch
        {
            "dax" => DaxFormatter.Format(code, DaxFormattingStyle.Long, token),
            "m" => PowerQueryFormatter.Format(code, token),
            _ => throw new AppException("VALIDATION_ERROR", "语言仅支持 dax 或 m")
        };
        if (result.Length > 16000 || JsonSerializer.Serialize(result).Length > 24000) throw new AppException("TOOL_RESULT_LIMIT", "格式化输出超出工具预算, 原代码保持不变", 3);
        return result;
    }
}
