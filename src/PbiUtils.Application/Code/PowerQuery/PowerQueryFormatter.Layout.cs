// FilePath    : pbi-utils\src\PbiUtils.Application\Code\PowerQuery\PowerQueryFormatter.Layout.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按 M 语法节点与行宽排版, 仅写入已确认可修改的词元间隙.

using System.Text;

namespace PbiUtils.Application.Code.PowerQuery;

public static partial class PowerQueryFormatter
{
    private sealed class Layout
    {
        internal int Indent;
        internal bool NewLine;
        internal bool Join;
    }
    private static readonly HashSet<string> SpacedOpeners = new("as is type table nullable function then else in otherwise each error meta let try and or not catch".Split(' '), StringComparer.Ordinal);

    /// <summary>从语法节点生成有界布局, 保留所有不可修改片段, 返回完整源码.</summary>
    private static string Write(IReadOnlyList<Token> tokens, Node root, Dictionary<int, string> protectedGaps, CancellationToken token)
    {
        var layout = Enumerable.Range(0, tokens.Count).Select(_ => new Layout()).ToArray();
        var lengths = new long[tokens.Count + 1];
        var forced = new int[tokens.Count + 1];
        for (var index = 0; index < tokens.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            lengths[index + 1] = lengths[index] + tokens[index].Text.Length + (protectedGaps.TryGetValue(index, out var gap) ? gap.Length : NeedsSpace(tokens, index) ? 1 : 0);
            forced[index + 1] = forced[index] + (IsComment(tokens[index]) || tokens[index].Text.ContainsAny('\r', '\n') ? 1 : 0);
        }
        Visit(root, 0);
        var output = new StringBuilder();
        var byteCount = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (protectedGaps.TryGetValue(index, out var gap)) Emit(gap);
            else
            {
                if (layout[index].NewLine || IsComment(tokens[index]) && tokens[index].StartsLine) NewLine();
                if (output.Length == 0 || output[^1] == '\n') Emit(new string(' ', layout[index].Indent * 4));
                else if (!layout[index].Join && NeedsSpace(tokens, index) && !char.IsWhiteSpace(output[^1])) Emit(" ");
            }
            Emit(tokens[index].Text);
            if (tokens[index].Kind == TokenKind.LineComment) Emit("\n");
        }
        return output.ToString().TrimEnd('\n');

        /// <summary>每次写入均按 UTF8 字节累计, 不反复物化已经生成的全文.</summary>
        void Emit(string value)
        {
            token.ThrowIfCancellationRequested();
            byteCount += Utf8.GetByteCount(value);
            if (byteCount > MaximumOutputBytes) throw Limit();
            output.Append(value);
        }

        /// <summary>只追加布局换行, 不修剪任何词元内部或行注释尾部空格.</summary>
        void NewLine()
        {
            if (output.Length > 0 && output[^1] != '\n') Emit("\n");
        }

        /// <summary>前置独立注释跟随其结构节点, 行尾注释继续附着原来的前置词元.</summary>
        void Break(int index, int indent)
        {
            while (index > 0 && IsComment(tokens[index - 1]) && tokens[index - 1].StartsLine) index--;
            layout[index].NewLine = true;
            layout[index].Indent = indent;
        }

        /// <summary>容器按目标宽度和结构性换行决定内联或分行, 不拆开字符串和字段名.</summary>
        bool Multiline(Node node, int indent) => lengths[node.End] - lengths[node.Start] + indent * 4 > TargetWidth
            || forced[node.End] != forced[node.Start] || node.Children.Any(child => child.Kind is "let" or "section");

        /// <summary>仅为真实语法节点分配缩进; 子节点覆盖父节点的默认级别.</summary>
        void Visit(Node node, int indent)
        {
            token.ThrowIfCancellationRequested();
            for (var index = node.Start; index < node.End; index++) { token.ThrowIfCancellationRequested(); layout[index].Indent = indent; }
            if (node.Kind == "let")
            {
                foreach (var binding in node.Children.Take(node.Children.Count - 1))
                {
                    Visit(binding, indent + 1);
                    Break(binding.Start, indent + 1);
                }
                Break(node.Marker, indent);
                var body = node.Children[^1];
                Visit(body, indent + 1);
                Break(body.Start, indent + 1);
            }
            else if (node.Kind == "section")
            {
                foreach (var child in node.Children) { Visit(child, indent); Break(child.Start, indent); }
            }
            else if (node.Kind == "function")
            {
                foreach (var child in node.Children.Take(node.Children.Count - 1)) Visit(child, indent);
                var body = node.Children[^1];
                var broken = body.Kind == "let" || Multiline(node, indent);
                Visit(body, broken ? indent + 1 : indent);
                if (broken) Break(body.Start, indent + 1);
            }
            else if (node.Kind == "if")
            {
                var broken = Multiline(node, indent);
                Visit(node.Children[0], indent);
                Visit(node.Children[1], broken ? indent + 1 : indent);
                Visit(node.Children[2], broken && node.Children[2].Kind != "if" ? indent + 1 : indent);
                if (broken)
                {
                    Break(node.Children[1].Start, indent + 1);
                    Break(node.Marker, indent);
                    if (node.Children[2].Kind != "if") Break(node.Children[2].Start, indent + 1);
                }
            }
            else if (node.Kind is "arguments" or "parameters" or "list" or "record" or "parentheses")
            {
                var broken = node.Children.Count > 0 && Multiline(node, indent);
                foreach (var child in node.Children)
                {
                    Visit(child, broken ? indent + 1 : indent);
                    if (broken) Break(child.Start, indent + 1);
                }
                if (broken) Break(node.End - 1, indent);
            }
            else
            {
                foreach (var child in node.Children) Visit(child, indent);
                if (node.Kind == "unary" && tokens[node.Start].Text is "+" or "-" or "@")
                {
                    var next = node.Start + 1;
                    if (next < node.End && !IsComment(tokens[next]) && tokens[next].Text is not ("+" or "-")) layout[next].Join = true;
                }
            }
        }
    }

    /// <summary>只计算 M 词元间的空格; 广义名称的内部间隙由原文单独提供.</summary>
    private static bool NeedsSpace(IReadOnlyList<Token> tokens, int index)
    {
        if (index == 0) return false;
        var current = tokens[index];
        var previous = tokens[index - 1];
        if (IsComment(current) || IsComment(previous)) return true;
        if (current.Text is ")" or "]" or "}" or "," or ";" or "." or "?" || previous.Text is "(" or "[" or "{" or "." or "@") return false;
        if (current.Text is "(" or "[" or "{" && (previous.Kind == TokenKind.QuotedName || previous.Kind == TokenKind.Word && !SpacedOpeners.Contains(previous.Text)
            || previous.Text is ")" or "]" or "}" or "?")) return false;
        return true;
    }
}
