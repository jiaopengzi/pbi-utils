// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxFormatter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 本地 DAX 词法保真格式化, 只修改空白且不依赖网络或模型引擎.

using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Code.Dax;

/// <summary>单个或批量表达式使用同一格式化函数; 结果不是语义或计算验证.</summary>
public static partial class DaxFormatter
{
    private enum Kind { Word, Number, Literal, Reference, Symbol, Comment, LineComment }
    private sealed record Token(Kind Kind, string Text, bool StartsLine = false);

    /// <summary>整个批次成功后才返回结果, 取消、超限和词法错误不会返回部分内容.</summary>
    /// <param name="expressions">1–128 个非空表达式, 合计不超过30000字符.</param>
    /// <param name="cancellationToken">扫描、布局和输出期间检查的取消信号.</param>
    /// <param name="style">整个批次统一使用的 Short/Long 样式, 默认 Long.</param>
    /// <returns>顺序对应输入的全部格式化正文, 不附加度量定义头缩进.</returns>
    /// <exception cref="AppException">输入、样式或词法无效, 或输出/JSON 预算超限.</exception>
    /// <exception cref="OperationCanceledException">取消不返回部分批次.</exception>
    public static IReadOnlyList<string> FormatMany(IReadOnlyList<string> expressions, DaxFormattingStyle style = DaxFormattingStyle.Long, CancellationToken cancellationToken = default)
        => FormatBatchCore(expressions, false, style, cancellationToken);

    /// <summary>批量格式化模型度量值正文, 为 Desktop 定义头保留首个换行, 全部成功后才返回.</summary>
    /// <param name="expressions">1–128 个度量值正文, 不包含度量值名称和定义等号, 合计最多30000字符.</param>
    /// <param name="cancellationToken">整个批次共享的取消信号, 取消不返回部分结果.</param>
    /// <param name="style">整个批次统一使用的 Short/Long 样式, 默认 Long.</param>
    /// <returns>各项以一个 LF 开头、顶级行顶格的正文, 总长度及 JSON 传输预算均不超过30000字符.</returns>
    /// <exception cref="AppException">输入无效、词法结构失败或格式化输出超过安全预算.</exception>
    /// <exception cref="OperationCanceledException">调用方取消当前批次.</exception>
    public static IReadOnlyList<string> FormatMeasures(IReadOnlyList<string> expressions, DaxFormattingStyle style = DaxFormattingStyle.Long, CancellationToken cancellationToken = default)
        => FormatBatchCore(expressions, true, style, cancellationToken);

    /// <summary>按明确的表达式上下文格式化批次, 统一验证字符预算、JSON 转义预算与取消, 不返回部分内容.</summary>
    private static string[] FormatBatchCore(IReadOnlyList<string> expressions, bool measureExpression, DaxFormattingStyle style, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expressions is not { Count: > 0 and <= 128 } || expressions.Any(string.IsNullOrWhiteSpace)
            || expressions.Sum(s => (long)s.Length) > 30000) throw new AppException("VALIDATION_ERROR", "请选择 1–128 个非空 DAX, 总长度不超过 30000 字符");
        var formatted = new string[expressions.Count];
        var totalLength = 0L;
        var jsonLength = 2L;
        for (var index = 0; index < expressions.Count; index++)
        {
            formatted[index] = FormatCore(expressions[index], measureExpression, style, true, cancellationToken);
            totalLength += formatted[index].Length;
            jsonLength += JsonStringLength(formatted[index], cancellationToken) + (index > 0 ? 1 : 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (totalLength > 30000 || jsonLength > 30000)
                throw new AppException("DAX_FORMAT_LIMIT", "批量格式化结果超过 30000 字符传输上限, 请拆分表达式");
        }
        return formatted;
    }

    /// <summary>格式化标准模型 DAX, 保留所有词元原文; 未闭合字符串、注释和括号明确失败.</summary>
    /// <param name="expression">非空且不超过30000字符的表达式或查询, 不推测模型对象类型.</param>
    /// <param name="cancellationToken">词法扫描、布局与输出期间检查的取消信号.</param>
    /// <param name="style">Short 参数展开或 Long 紧凑布局, 默认 Long; 非法枚举值明确拒绝.</param>
    /// <returns>采用所选 SQLBI 布局的文本, 原标识符与字面量保持.</returns>
    /// <exception cref="AppException">输入无效、结构不完整、超出布局边界或词元无法安全保持.</exception>
    /// <exception cref="OperationCanceledException">调用方取消, 不返回部分内容.</exception>
    public static string Format(string expression, DaxFormattingStyle style = DaxFormattingStyle.Long, CancellationToken cancellationToken = default)
        => FormatCore(expression, false, style, true, cancellationToken);

    /// <summary>格式化模型度量值正文, 以单个 LF 让 Desktop 的 "名称 =" 后另起一行, 顶级行不添加缩进.</summary>
    /// <param name="expression">不含定义头的度量值正文, 已有首部空白会规范化, 不修改字面量内部内容.</param>
    /// <param name="cancellationToken">词法、布局及输出期间传播的取消信号.</param>
    /// <param name="style">Short 参数展开或 Long 紧凑布局, 默认 Long.</param>
    /// <returns>以一个 LF 开头、仅按语法层级缩进且可重复格式化的正文, 含新增空白最多30000字符.</returns>
    /// <exception cref="AppException">输入无效、结构不完整、词元无法保持或输出超限.</exception>
    /// <exception cref="OperationCanceledException">调用方取消当前格式化.</exception>
    public static string FormatMeasure(string expression, DaxFormattingStyle style = DaxFormattingStyle.Long, CancellationToken cancellationToken = default)
        => FormatCore(expression, true, style, true, cancellationToken);

    /// <summary>仅供后台固定查询生成器复用 Long 排版, 不将合法大模型投影套入编辑器的字符预算.</summary>
    /// <param name="expression">由可信模板及已核验、已转义的模型标识构造的查询; 不接受网页自由表达式.</param>
    /// <param name="cancellationToken">词法扫描、布局和输出期间检查的取消信号.</param>
    /// <returns>保留全部词元的 Long 查询, 不施加交互字符预算, 仍保留结构深度保护.</returns>
    /// <exception cref="AppException">输入为空、词法结构无效、嵌套过深或词元无法安全保持.</exception>
    /// <exception cref="OperationCanceledException">调用方取消, 不返回部分查询.</exception>
    public static string FormatGeneratedQuery(string expression, CancellationToken cancellationToken = default)
        => FormatCore(expression, false, DaxFormattingStyle.Long, false, cancellationToken);

    /// <summary>复用同一词元保真写入器; interactiveLimits 仅对编辑器入口施加字符预算, 后台固定查询仍核验结构、取消和词元.</summary>
    private static string FormatCore(string expression, bool measureExpression, DaxFormattingStyle style, bool interactiveLimits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (style is not (DaxFormattingStyle.Short or DaxFormattingStyle.Long)) throw new AppException("VALIDATION_ERROR", "DAX 格式必须为 short 或 long");
        if (string.IsNullOrWhiteSpace(expression) || interactiveLimits && expression.Length > 30000) throw new AppException("VALIDATION_ERROR", "DAX 需为 1–30000 字符");
        var tokens = Lex(expression, cancellationToken);
        var layout = CreateLayout(tokens, measureExpression, style, cancellationToken);
        var output = new StringBuilder(measureExpression ? "\n" : "");
        for (var i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[i];
            if (layout[i].NewLine) NewLine(output, layout[i].BlankLine);
            else if (NeedsSpace(tokens, i)) Space(output);
            Append(output, token.Text, layout[i].Indent);
            // 行注释的换行是语法边界, 且尾随空格属于注释原文, 不能 Trim.
            if (token.Kind == Kind.LineComment) output.Append('\n');
            if (interactiveLimits && output.Length > 120000) throw new AppException("DAX_FORMAT_LIMIT", "格式化结果过大, 请拆分表达式");
        }
        var formatted = output.ToString().TrimEnd('\r', '\n');
        cancellationToken.ThrowIfCancellationRequested();
        if (interactiveLimits && formatted.Length > 30000) throw new AppException("DAX_FORMAT_LIMIT", "格式化结果超过 30000 字符, 请拆分表达式");
        if (!tokens.Select(t => (t.Kind, t.Text)).SequenceEqual(Lex(formatted, cancellationToken).Select(t => (t.Kind, t.Text))))
            throw new AppException("DAX_FORMAT_UNSAFE", "此表达式暂不支持安全格式化, 原内容已保留");
        return formatted;
    }

    /// <summary>计算 JavaScript JSON.stringify 字符串的 UTF-16 长度, 包含引号和必要转义, 不创建传输副本.</summary>
    /// <param name="value">已格式化表达式, 中文及有效代理对按原 UTF-16 长度计数.</param>
    /// <param name="cancellationToken">预算扫描期间检查的取消信号, 取消直接传播.</param>
    /// <returns>包含 JSON 两侧引号的字符数; 控制符及孤立代理码位按实际 JSON 转义长度计数.</returns>
    private static long JsonStringLength(string value, CancellationToken cancellationToken)
    {
        var length = 2L;
        for (var index = 0; index < value.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = value[index];
            if (character is '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r') length += 2;
            else if (character < ' ') length += 6;
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                length += 2;
                index++;
            }
            else length += char.IsSurrogate(character) ? 6 : 1;
        }
        return length;
    }

    /// <summary>按 SQLBI 约定添加括号与运算符空格, 保持一元正负号、限定名与成员引用的词法边界.</summary>
    private static bool NeedsSpace(IReadOnlyList<Token> tokens, int index)
    {
        if (index == 0) return false;
        var current = tokens[index]; var previous = tokens[index - 1];
        if (current.Kind is Kind.Comment or Kind.LineComment || previous.Kind is Kind.Comment or Kind.LineComment) return true;
        if (current.Text is "," or ";" or "." || previous.Text == ".") return false;
        if (current.Text == "}") return previous.Text != "{";
        if (current.Text == ")") return previous.Text != "(";
        if (previous.Text == "(") return true;
        if (current.Kind == Kind.Reference && (previous.Kind == Kind.Word && !IsControlWord(previous)
            || previous.Kind == Kind.Literal && previous.Text.StartsWith('\''))) return false;
        if (previous.Text is "+" or "-" && IsUnary(tokens, index - 1)) return current.Kind != Kind.Number;
        return true;
    }

    private static readonly string[] ExpressionPrefixKeywords = ["RETURN", "IN", "NOT", "EVALUATE"];

    /// <summary>用左侧非注释词元区分一元符号, 不把两个减号拼成注释.</summary>
    private static bool IsUnary(IReadOnlyList<Token> tokens, int index)
    {
        while (--index >= 0 && tokens[index].Kind is Kind.Comment or Kind.LineComment) { }
        return index < 0 || tokens[index].Kind == Kind.Symbol && tokens[index].Text is not (")" or "}")
            || tokens[index].Kind == Kind.Word && ExpressionPrefixKeywords.Any(word => IsKeyword(tokens[index], word));
    }

    /// <summary>关键字只匹配裸词元, 不会识别名称、字符串或注释内的文字.</summary>
    private static bool IsKeyword(Token token, string keyword) => token.Kind == Kind.Word && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>查询和流程关键字不是表名, 后接度量值引用时必须保留分隔.</summary>
    private static bool IsControlWord(Token token) => token.Kind == Kind.Word && token.Text.ToUpperInvariant()
        is "VAR" or "RETURN" or "IN" or "NOT" or "DEFINE" or "EVALUATE" or "ORDER" or "BY" or "START" or "AT" or "ASC" or "DESC";

    /// <summary>以最长匹配扫描注释、转义名称、日期、数字和复合运算符, 保留每个词元的原始文本.</summary>
    private static List<Token> Lex(string input, CancellationToken token)
    {
        var result = new List<Token>();
        var previousEnd = 0;
        for (var i = 0; i < input.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            var start = i;
            var kind = Kind.Symbol;
            if (input.AsSpan(i).StartsWith("//") || input.AsSpan(i).StartsWith("--"))
            {
                kind = Kind.LineComment; i += 2;
                while (i < input.Length && input[i] is not ('\r' or '\n')) i++;
            }
            else if (input.AsSpan(i).StartsWith("/*"))
            {
                kind = Kind.Comment;
                var end = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw Invalid();
                i = end + 2;
            }
            else if (input[i] is '\'' or '"' or '[' || i + 2 < input.Length && input.AsSpan(i, 3).Equals("dt\"", StringComparison.OrdinalIgnoreCase))
            {
                kind = input[i] == '[' ? Kind.Reference : Kind.Literal;
                if (input[i] is not ('\'' or '"' or '[')) i += 2;
                var close = input[i] == '[' ? ']' : input[i]; i++;
                var closed = false;
                while (i < input.Length)
                {
                    if (input[i++] != close) continue;
                    if (i < input.Length && input[i] == close) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw Invalid();
            }
            else if (char.IsDigit(input[i]) || input[i] == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1]))
            {
                kind = Kind.Number;
                while (i < input.Length && char.IsDigit(input[i])) i++;
                if (i < input.Length && input[i] == '.') { i++; while (i < input.Length && char.IsDigit(input[i])) i++; }
                if (i < input.Length && input[i] is 'e' or 'E')
                {
                    i++; if (i < input.Length && input[i] is '+' or '-') i++;
                    var digits = i; while (i < input.Length && char.IsDigit(input[i])) i++;
                    if (i == digits) throw Invalid();
                }
            }
            else if (char.IsLetter(input[i]) || input[i] == '_')
            {
                kind = Kind.Word; i++;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || char.GetUnicodeCategory(input[i]) == System.Globalization.UnicodeCategory.NonSpacingMark)) i++;
            }
            else
            {
                if (!"(){}.,;+-*/^&=<>:|@".Contains(input[i], StringComparison.Ordinal)) throw Invalid();
                i++;
                if (i < input.Length && input.Substring(start, 2) is "&&" or "||" or "<=" or ">=" or "<>" or "==" or ":=" or "=>") i++;
            }
            var startsLine = input.AsSpan(previousEnd, start - previousEnd).IndexOfAny('\r', '\n') >= 0;
            result.Add(new(kind, input[start..i], startsLine));
            previousEnd = i;
        }
        return result;
    }

    /// <summary>将内容放到当前缩进, 不修改内容内部的空白.</summary>
    private static void Append(StringBuilder output, string value, int indent)
    {
        if (output.Length == 0 || output[^1] == '\n') output.Append(' ', indent * 4);
        output.Append(value);
    }
    /// <summary>只在行内补一个词元分隔空格.</summary>
    private static void Space(StringBuilder output) { if (output.Length > 0 && !char.IsWhiteSpace(output[^1])) output.Append(' '); }
    /// <summary>只移除排版追加的行尾空格, 不扫描词元内部.</summary>
    private static void TrimSpaces(StringBuilder output) { while (output.Length > 0 && output[^1] == ' ') output.Length--; }
    /// <summary>规范化词元之间的换行与变量块空行, 保留行注释终止边界及词元内部空白.</summary>
    private static void NewLine(StringBuilder output, bool blankLine = false)
    {
        TrimSpaces(output);
        if (output.Length == 0) return;
        if (output[^1] != '\n') output.Append('\n');
        if (blankLine && (output.Length < 2 || output[^2] != '\n')) output.Append('\n');
    }
    /// <summary>错误不包含原表达式, 调用方保留用户输入.</summary>
    private static AppException Invalid() => new("DAX_FORMAT_INVALID", "DAX 含未闭合的引号、注释、括号或不完整、暂不支持的结构, 原内容已保留");
}
