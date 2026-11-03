// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Code\PowerQuery\PowerQueryLiterals.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享 Power Query M 文本字面量转义, 供路径、参数和数据适配使用.

namespace PbiUtils.Infrastructure.Code.PowerQuery;

/// <summary>只编码单个 M 值, 不格式化查询或推断类型.</summary>
internal static class PowerQueryLiterals
{
    /// <summary>M 字符串先转义 # 避免路径或名称成为转义表达式.</summary>
    /// <param name="text">需要作为单个 M 文本值保存的原始内容.</param>
    /// <returns>保留原始文本含义的带引号 M 字面量, 不解释其中的 # 或控制字符.</returns>
    internal static string Text(string text) => "\"" + text.Replace("#", "#(#)", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal).Replace("\r", "#(cr)", StringComparison.Ordinal).Replace("\n", "#(lf)", StringComparison.Ordinal).Replace("\t", "#(tab)", StringComparison.Ordinal) + "\"";
}
