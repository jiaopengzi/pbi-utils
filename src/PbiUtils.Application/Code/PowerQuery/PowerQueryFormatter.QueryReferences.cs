// FilePath    : pbi-utils\src\PbiUtils.Application\Code\PowerQuery\PowerQueryFormatter.QueryReferences.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用有界 M 词法保守识别查询名和动态环境依赖, 用于日期旧函数重命名保护.

using System.Globalization;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Code.PowerQuery;

public static partial class PowerQueryFormatter
{
    /// <summary>只读判断表达式是否可能依赖查询; 动态环境、未知词法和超限输入均不能证明可安全重命名.</summary>
    /// <param name="expression">其他 M 查询或分区的完整源码, 不执行或改写.</param>
    /// <param name="queryName">将被重命名的完整原查询名.</param>
    /// <param name="cancellationToken">取消词法扫描和标识符解码的令牌.</param>
    /// <returns>存在引用或不能证明没有引用时为 true; 注释和普通字符串不作为静态名称引用.</returns>
    /// <exception cref="ArgumentException">待检查的查询名称为空.</exception>
    /// <exception cref="OperationCanceledException">调用方取消分析.</exception>
    public static bool MayReferenceQuery(string expression, string queryName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(expression) || Utf8.GetByteCount(expression) > MaximumInputBytes) return true;
            var tokens = Lex(expression, cancellationToken).Where(value => value.Kind is not (TokenKind.LineComment or TokenKind.BlockComment)).ToArray();
            for (var index = 0; index < tokens.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = tokens[index];
                var name = QueryIdentifier(token, cancellationToken);
                if (name.Equals(queryName, StringComparison.OrdinalIgnoreCase)
                    || name.Equals("#shared", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("#sections", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Expression.Evaluate", StringComparison.OrdinalIgnoreCase)
                    || token.Kind == TokenKind.Verbatim) return true;
                if (name.Equals("Expression", StringComparison.OrdinalIgnoreCase) && index + 2 < tokens.Length
                    && tokens[index + 1].Text == "." && QueryIdentifier(tokens[index + 2], cancellationToken).Equals("Evaluate", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        // 词法或 UTF8 无法可靠读取时保留旧查询; 取消异常不在此捕获, 继续遵守调用方取消边界.
        catch (AppException) { return true; }
        catch (ArgumentException) { return true; }
        catch (OverflowException) { return true; }
    }

    /// <summary>解码词法已校验的引号标识符, 支持双引号及单次四位/八位字符转义; 普通字符串返回空名.</summary>
    private static string QueryIdentifier(Token token, CancellationToken cancellationToken)
    {
        if (token.Kind == TokenKind.Word) return token.Text;
        if (token.Kind != TokenKind.QuotedName) return "";
        var source = token.Text.AsSpan(2, token.Text.Length - 3);
        var result = new StringBuilder();
        for (var index = 0; index < source.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source[index] == '"') { result.Append('"'); index++; }
            else if (source[index..].StartsWith("#("))
            {
                var end = index + source[index..].IndexOf(')');
                foreach (var escape in source[(index + 2)..end].ToString().Split(','))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Append(escape switch
                    {
                        "cr" => "\r",
                        "lf" => "\n",
                        "tab" => "\t",
                        "#" => "#",
                        _ when escape.Length == 4 => ((char)int.Parse(escape, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(),
                        _ => char.ConvertFromUtf32(int.Parse(escape, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    });
                }
                index = end;
            }
            else result.Append(source[index]);
        }
        return result.ToString();
    }
}
