// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxFormatter.UdfReferences.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用本地 DAX 词法器识别真实函数调用, 不将字符串和注释作为引用.

using PbiUtils.Core;

namespace PbiUtils.Application.Code.Dax;

public static partial class DaxFormatter
{
    /// <summary>读取裸标识符及命名空间限定调用; 词法未知或括号破损返回 false, 取消继续传播.</summary>
    /// <param name="expression">完整 DAX, 不执行表达式.</param>
    /// <param name="calls">大小写无关的调用名称集合, 不含字符串、表列引用及注释.</param>
    /// <param name="token">扫描期间检查的取消令牌.</param>
    public static bool TryReadFunctionCalls(string expression, out HashSet<string> calls, CancellationToken token = default)
    {
        calls = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var tokens = Lex(expression, token).Where(item => item.Kind is not (Kind.Comment or Kind.LineComment)).ToArray();
            var stack = new Stack<string>();
            for (var i = 0; i < tokens.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = tokens[i];
                if (item.Text == "." && (i == 0 || i + 1 >= tokens.Length || tokens[i - 1].Kind != Kind.Word || tokens[i + 1].Kind != Kind.Word)
                    || item.Text == ":" && i + 1 < tokens.Length && tokens[i + 1].Text == ":"
                    || item.Kind is Kind.Literal or Kind.Reference && i + 1 < tokens.Length && tokens[i + 1].Text == "(") return false;
                if (item.Text is "(" or "{") stack.Push(item.Text);
                else if (item.Text is ")" or "}")
                {
                    if (!stack.TryPop(out var opening) || opening != (item.Text == ")" ? "(" : "{")) return false;
                }
                if (item.Kind != Kind.Word || i > 0 && tokens[i - 1].Text == ".") continue;
                var name = item.Text;
                var end = i + 1;
                while (end + 1 < tokens.Length && tokens[end].Text == "." && tokens[end + 1].Kind == Kind.Word)
                {
                    name += "." + tokens[end + 1].Text;
                    end += 2;
                }
                if (end < tokens.Length && tokens[end].Text == "(") calls.Add(name);
            }
            return stack.Count == 0;
        }
        catch (AppException) { return false; }
    }
}
