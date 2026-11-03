// FilePath    : pbi-utils\src\PbiUtils.Application\Code\PowerQuery\PowerQueryFormatter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 无网络依赖的有界 M 词法, 结构解析和保真格式化入口.

using System.Globalization;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Code.PowerQuery;

/// <summary>仅排版本机 M 源码, 不执行表达式或读取任何数据源.</summary>
public static partial class PowerQueryFormatter
{
    /// <summary>输入的 UTF8 字节上限, 与 DAX 的字符预算独立.</summary>
    public const int MaximumInputBytes = 3 * 1024 * 1024;
    /// <summary>输出的 UTF8 字节上限, 不截断字符串或返回部分结果.</summary>
    public const int MaximumOutputBytes = 3 * 1024 * 1024;
    /// <summary>单次扫描的词元总上限, 包含原始注释.</summary>
    public const int MaximumTokens = 262144;
    /// <summary>表达式与容器的最大嵌套深度.</summary>
    public const int MaximumDepth = 128;
    private const int TargetWidth = 120;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Intrinsics = new("#binary #date #datetime #datetimezone #duration #infinity #nan #sections #shared #table #time".Split(' '), StringComparer.Ordinal);
    private enum TokenKind { Word, Number, Text, QuotedName, Verbatim, Symbol, BlockComment, LineComment }
    private sealed record Token(TokenKind Kind, string Text, int Start, int End, bool StartsLine);
    private sealed record Node(string Kind, int Start, int End, IReadOnlyList<Node> Children, int Marker = -1);

    /// <summary>解析后仅调整可修改的空白, 重扫并核对结构及全部原始词元后一次返回.</summary>
    /// <param name="expression">完整的 M 表达式或 section 文档.</param>
    /// <param name="cancellationToken">取消扫描, 解析和布局的令牌.</param>
    /// <returns>四空格缩进, 目标宽度 120 的完整源码.</returns>
    /// <exception cref="AppException">源码残缺, 不支持或超出硬限额.</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发.</exception>
    public static string Format(string expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expression)) throw Invalid();
        try { if (Utf8.GetByteCount(expression) > MaximumInputBytes) throw Limit(); }
        catch (EncoderFallbackException) { throw Invalid(); }
        var tokens = Lex(expression, cancellationToken);
        var parser = new Parser(expression, tokens, cancellationToken);
        var root = parser.Parse();
        var formatted = Write(tokens, root, parser.ProtectedGaps, cancellationToken);
        var verified = Lex(formatted, cancellationToken);
        if (!tokens.Select(value => (value.Kind, value.Text)).SequenceEqual(verified.Select(value => (value.Kind, value.Text)))) throw Unsafe();
        var verifier = new Parser(formatted, verified, cancellationToken);
        var verifiedRoot = verifier.Parse();
        if (!SameStructure(root, verifiedRoot, cancellationToken) || !parser.Names.SequenceEqual(verifier.Names)) throw Unsafe();
        return formatted;
    }

    /// <summary>核对位置无关的语法结构; 原始词元序号包含注释, 不因新空白变化.</summary>
    private static bool SameStructure(Node first, Node second, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (first.Kind != second.Kind || first.Start != second.Start || first.End != second.End || first.Marker != second.Marker || first.Children.Count != second.Children.Count) return false;
        for (var index = 0; index < first.Children.Count; index++)
            if (!SameStructure(first.Children[index], second.Children[index], token)) return false;
        return true;
    }

    /// <summary>最长匹配扫描 M 字面量, 注释, Unicode 名称与运算符, 保留原文及源位置.</summary>
    private static List<Token> Lex(string source, CancellationToken token)
    {
        var result = new List<Token>();
        for (var position = 0; position < source.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(source[position])) { position++; continue; }
            var start = position;
            var kind = TokenKind.Symbol;
            if (source.AsSpan(position).StartsWith("//"))
            {
                kind = TokenKind.LineComment;
                position += 2;
                while (position < source.Length && !LineEnd(source[position])) { token.ThrowIfCancellationRequested(); position++; }
            }
            else if (source.AsSpan(position).StartsWith("/*"))
            {
                kind = TokenKind.BlockComment;
                position += 2;
                while (position + 1 < source.Length && !source.AsSpan(position).StartsWith("*/")) { token.ThrowIfCancellationRequested(); position++; }
                if (position + 1 >= source.Length) throw Invalid();
                position += 2;
            }
            else if (source[position] == '"' || source.AsSpan(position).StartsWith("#\"") || source.AsSpan(position).StartsWith("#!\""))
            {
                kind = source[position] == '"' ? TokenKind.Text : source.AsSpan(position).StartsWith("#!\"") ? TokenKind.Verbatim : TokenKind.QuotedName;
                position += kind == TokenKind.Text ? 1 : kind == TokenKind.QuotedName ? 2 : 3;
                var closed = false;
                while (position < source.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (source.AsSpan(position).StartsWith("#("))
                    {
                        position += 2;
                        while (true)
                        {
                            var escapeStart = position;
                            while (position < source.Length && source[position] is not (',' or ')')) { token.ThrowIfCancellationRequested(); position++; }
                            if (position == source.Length) throw Invalid();
                            var escape = source.AsSpan(escapeStart, position - escapeStart);
                            if (!(escape.SequenceEqual("cr") || escape.SequenceEqual("lf") || escape.SequenceEqual("tab") || escape.SequenceEqual("#")
                                || escape.Length is 4 or 8 && IsHex(escape))) throw Invalid();
                            if (source[position++] == ')') break;
                        }
                    }
                    else if (source[position++] == '"')
                    {
                        if (position < source.Length && source[position] == '"') { position++; continue; }
                        closed = true;
                        break;
                    }
                }
                if (!closed) throw Invalid();
            }
            else if (char.IsAsciiDigit(source[position]) || source[position] == '.' && position + 1 < source.Length && char.IsAsciiDigit(source[position + 1]))
            {
                kind = TokenKind.Number;
                if (source.AsSpan(position).StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    position += 2;
                    var digits = position;
                    while (position < source.Length && char.IsAsciiHexDigit(source[position])) { token.ThrowIfCancellationRequested(); position++; }
                    if (position == digits) throw Invalid();
                }
                else
                {
                    while (position < source.Length && char.IsAsciiDigit(source[position])) { token.ThrowIfCancellationRequested(); position++; }
                    if (position + 1 < source.Length && source[position] == '.' && char.IsAsciiDigit(source[position + 1]))
                    {
                        position++;
                        while (position < source.Length && char.IsAsciiDigit(source[position])) { token.ThrowIfCancellationRequested(); position++; }
                    }
                    if (position < source.Length && source[position] is 'e' or 'E')
                    {
                        position++;
                        if (position < source.Length && source[position] is '+' or '-') position++;
                        var digits = position;
                        while (position < source.Length && char.IsAsciiDigit(source[position])) { token.ThrowIfCancellationRequested(); position++; }
                        if (position == digits) throw Invalid();
                    }
                }
            }
            else if (source[position] == '#' || NameCharacter(source, position, true))
            {
                kind = TokenKind.Word;
                if (source[position] == '#') position++;
                if (position >= source.Length || !NameCharacter(source, position, true)) throw Invalid();
                position += Rune.GetRuneAt(source, position).Utf16SequenceLength;
                while (position < source.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (NameCharacter(source, position, false)) position += Rune.GetRuneAt(source, position).Utf16SequenceLength;
                    else if (source[position] == '.' && position + 1 < source.Length && NameCharacter(source, position + 1, false)) position++;
                    else break;
                }
                if (source[start] == '#' && !Intrinsics.Contains(source[start..position])) throw Invalid();
            }
            else
            {
                if (!"()[]{}.,;+-*/&=<>@!?".Contains(source[position], StringComparison.Ordinal)) throw Invalid();
                var width = source.AsSpan(position).StartsWith("...") ? 3 : position + 1 < source.Length && source.Substring(position, 2) is ".." or "??" or "=>" or "<=" or ">=" or "<>" ? 2 : 1;
                position += width;
            }
            if (result.Count >= MaximumTokens) throw Limit();
            var previousEnd = result.Count == 0 ? 0 : result[^1].End;
            var startsLine = result.Count == 0 || source.AsSpan(previousEnd, start - previousEnd).ContainsAny('\r', '\n', '\u0085')
                || source.AsSpan(previousEnd, start - previousEnd).ContainsAny('\u2028', '\u2029');
            result.Add(new(kind, source[start..position], start, position, startsLine));
        }
        return result;
    }

    /// <summary>按 M 的 Unicode 分类识别名称, 支持非 BMP 字符.</summary>
    private static bool NameCharacter(string source, int position, bool first)
    {
        var category = Rune.GetUnicodeCategory(Rune.GetRuneAt(source, position));
        return source[position] == '_' || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
            || !first && category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;
    }

    /// <summary>校验字符转义中的四位或八位十六进制码.</summary>
    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value) if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    /// <summary>M 行注释由所有规定的 Unicode 换行符终止.</summary>
    private static bool LineEnd(char value) => value is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';
    /// <summary>返回不包含用户源码的固定解析错误.</summary>
    private static AppException Invalid() => new("M_FORMAT_INVALID", "M 源码残缺或包含暂不支持的结构, 原内容已保留");
    /// <summary>返回独立于用户源码的硬预算错误.</summary>
    private static AppException Limit() => new("M_FORMAT_LIMIT", "M 源码或格式化结果超过字节, 词元或嵌套上限, 原内容已保留");
    /// <summary>保真复核不一致时拒绝任何结果.</summary>
    private static AppException Unsafe() => new("M_FORMAT_UNSAFE", "此 M 源码暂不支持安全格式化, 原内容已保留");
}
