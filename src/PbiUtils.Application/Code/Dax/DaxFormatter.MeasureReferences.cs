// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxFormatter.MeasureReferences.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用 DAX 词法器读取实际方括号引用和可选表限定名, 不把注释和字符串当引用.

using PbiUtils.Core;

namespace PbiUtils.Application.Code.Dax;

public static partial class DaxFormatter
{
    /// <summary>只读提取可能指向度量值的方括号引用, 列与度量值的消歧由完整模型调用方处理.</summary>
    /// <param name="expression">完整 DAX, 不执行或验证业务计算.</param>
    /// <param name="references">已解码的可选表名与对象名, 不包含字符串和注释内容.</param>
    /// <param name="token">词法扫描取消令牌, 取消继续传播.</param>
    /// <returns>词法和括号完整时为 true; 未知词元或不完整结构返回 false, 不提供部分引用.</returns>
    public static bool TryReadMeasureReferences(string expression, out IReadOnlyList<(string? Table, string Name)> references, CancellationToken token = default)
    {
        references = [];
        try
        {
            var tokens = Lex(expression, token).Where(item => item.Kind is not (Kind.Comment or Kind.LineComment)).ToArray();
            var result = new List<(string? Table, string Name)>();
            var stack = new Stack<string>();
            for (var index = 0; index < tokens.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var item = tokens[index];
                if (item.Text == "." && (index == 0 || index + 1 >= tokens.Length || tokens[index - 1].Kind != Kind.Word || tokens[index + 1].Kind != Kind.Word)
                    || item.Text == ":" && index + 1 < tokens.Length && tokens[index + 1].Text == ":"
                    || item.Kind is Kind.Literal or Kind.Reference && index + 1 < tokens.Length && tokens[index + 1].Text == "(") return false;
                if (item.Text is "(" or "{") stack.Push(item.Text);
                else if (item.Text is ")" or "}")
                {
                    if (!stack.TryPop(out var opening) || opening != (item.Text == ")" ? "(" : "{")) return false;
                }
                if (item.Kind != Kind.Reference) continue;
                string? table = null;
                if (index > 0)
                {
                    var prior = tokens[index - 1];
                    if (prior.Kind == Kind.Literal && prior.Text.StartsWith('\'')) table = prior.Text[1..^1].Replace("''", "'", StringComparison.Ordinal);
                    else if (prior.Kind == Kind.Word && !IsControlWord(prior)) table = prior.Text;
                }
                result.Add((table, item.Text[1..^1].Replace("]]", "]", StringComparison.Ordinal)));
            }
            if (stack.Count != 0) return false;
            references = result;
            return true;
        }
        catch (AppException) { return false; }
    }
}
