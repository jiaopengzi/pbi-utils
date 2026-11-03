// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxLayout.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按括号、参数及变量作用域规划 DAX 排版, 不改写任何词元.

using PbiUtils.Core;

namespace PbiUtils.Application.Code.Dax;

public static partial class DaxFormatter
{
    private sealed record Layout(int Indent, bool NewLine = false, bool BlankLine = false);
    private sealed record Group(int Start, int End, IReadOnlyList<Group>? Children = null);

    private static readonly string[] DefinitionKeywords = ["MEASURE", "COLUMN", "TABLE", "FUNCTION", "VAR"];

    /// <summary>返回二元运算符的结合优先级, 用于保持比较/乘法子表达式与外层续行的绑定.</summary>
    private static int BinaryPrecedence(string symbol) => symbol.ToUpperInvariant() switch
    {
        "||" => 1,
        "&&" => 2,
        "=" or "==" or "<>" or "<" or ">" or "<=" or ">=" or "IN" => 3,
        "&" => 4,
        "+" or "-" => 5,
        "*" or "/" => 6,
        "^" => 7,
        _ => 0
    };

    /// <summary>按所选 SQLBI 规则规划函数、定义头与变量块的空白, 不改写词元或推断模型名称.</summary>
    /// <param name="tokens">原文词法扫描得到的有序词元, 字符串与注释内容不参与结构推测.</param>
    /// <param name="measureExpression">度量值正文顶级行顶格, 并禁止将比较等号猜成完整定义头.</param>
    /// <param name="cancellationToken">结构扫描和布局期间传播的取消信号.</param>
    /// <param name="style">Short 或 Long, 由公共入口验证; 仅影响词元间空白.</param>
    /// <returns>与输入词元逐项对应的四空格缩进、换行及空行计划.</returns>
    /// <exception cref="AppException">括号或声明结构不完整, 或嵌套超过安全上限.</exception>
    /// <exception cref="OperationCanceledException">调用方取消当前批次.</exception>
    private static Layout[] CreateLayout(IReadOnlyList<Token> tokens, bool measureExpression, DaxFormattingStyle style, CancellationToken cancellationToken)
    {
        const int initialLevel = 0;
        var expressionBaseLevel = initialLevel;
        var layout = Enumerable.Range(0, tokens.Count).Select(_ => new Layout(initialLevel)).ToArray();
        var position = 0;
        var variableDepth = 0;
        var flatLengths = new int[tokens.Count + 1];
        for (var index = 0; index < tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            flatLengths[index + 1] = flatLengths[index] + tokens[index].Text.Length + (NeedsSpace(tokens, index) ? 1 : 0);
        }
        var nodes = ReadGroups(null, 0);
        var headerEnd = measureExpression ? -1 : ReadMeasureHeader(nodes);
        if (headerEnd >= 0)
        {
            var body = nodes.Skip(headerEnd + 1).ToArray();
            if (body.Length == 0) throw Invalid();
            LayoutSequence(body, 0);
            Line(nodes[headerEnd].End, 0);
        }
        else LayoutSequence(nodes, initialLevel);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == Kind.LineComment && tokens[index].StartsLine
                || tokens[index].Kind == Kind.Comment && (tokens[index].Text.Contains('\n')
                    || index == 0 || tokens[index - 1].Text is "," or ";" or "(" or "{"))
            {
                var level = index + 1 < tokens.Count ? layout[index + 1].Indent : initialLevel;
                Line(index, level);
            }
            if (index + 1 < tokens.Count && tokens[index].Kind == Kind.Comment) Line(index + 1, layout[index + 1].Indent);
        }
        return layout;

        /// <summary>按预期闭括号和当前深度读取结构, 返回子组; 残缺或超过64层明确失败, 取消直接传播.</summary>
        List<Group> ReadGroups(string? close, int depth)
        {
            if (depth > 64) throw new AppException("DAX_FORMAT_LIMIT", "DAX 嵌套层级过深");
            var children = new List<Group>();
            while (position < tokens.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = position++; var token = tokens[start];
                if (token.Kind is Kind.Comment or Kind.LineComment) continue;
                if (token.Text is ")" or "}")
                {
                    if (token.Text != close) throw Invalid();
                    return children;
                }
                if (token.Text is "(" or "{")
                {
                    var nested = ReadGroups(token.Text == "(" ? ")" : "}", depth + 1);
                    children.Add(new(start, position, nested));
                }
                else children.Add(new(start, position));
            }
            if (close is not null) throw Invalid();
            return children;
        }

        /// <summary>将有序词元组设为指定基础层级, 超出结构预算则失败, 不修改词元原文.</summary>
        void Indent(IReadOnlyList<Group> items, int level)
        {
            // 度量定义头的基础缩进只影响显示, 不占用结构嵌套预算.
            if (level - expressionBaseLevel > 64) throw new AppException("DAX_FORMAT_LIMIT", "DAX 嵌套层级过深");
            if (items.Count == 0) return;
            for (var index = items[0].Start; index < items[^1].End; index++) layout[index] = new(level);
        }

        /// <summary>为指定词元设置缩进和前置换行, 仅查询段落可以显式要求空行.</summary>
        void Line(int index, int level, bool blankLine = false)
        {
            layout[index] = new(level, true, blankLine);
        }

        // 宽度只计算表达式本身, 度量正文的显示缩进不改变 short/long 的结构选择.
        int FlatLength(int start, int end) => flatLengths[end] - flatLengths[start] - (NeedsSpace(tokens, start) ? 1 : 0);
        bool HasComments(int start, int end) => Enumerable.Range(start, end - start).Any(i => tokens[i].Kind is Kind.Comment or Kind.LineComment);
        bool Fits(int start, int end) => style == DaxFormattingStyle.Long && FlatLength(start, end) <= 80 && !HasComments(start, end);
        int ExpressionWidth() => style == DaxFormattingStyle.Long ? 80 : 40;
        bool HasCall(int start, int end) => Enumerable.Range(start, end - start).Any(i => i > start && tokens[i].Text == "("
            && tokens[i - 1].Kind == Kind.Word && !IsControlWord(tokens[i - 1]));

        /// <summary>将半开词元区间的排版设为同行, 字面量内部换行及行注释终止符仍由写入器保留.</summary>
        void Inline(int start, int end, int level)
        {
            for (var index = start; index < end; index++) layout[index] = new(level);
        }

        /// <summary>从完整输入组读取无括号的度量定义头, 返回等号组下标或-1; 度量正文入口不调用此推断.</summary>
        int ReadMeasureHeader(IReadOnlyList<Group> items)
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var value = tokens[item.Start];
                if (item.Children is not null || IsControlWord(value)) return -1;
                if (value.Text == "=") return index > 0 ? index : -1;
                if (value.Kind is not (Kind.Word or Kind.Reference) && !(value.Kind == Kind.Literal && value.Text.StartsWith('\''))) return -1;
            }
            return -1;
        }

        bool Keyword(Group node, string word) => node.Children is null && IsKeyword(tokens[node.Start], word);
        bool Flow(Group node) => Keyword(node, "VAR") || Keyword(node, "RETURN");

        /// <summary>在给定层级规划表达式/查询序列, relationshipFilter 仅标记当前直接筛选参数; 结构错误及取消传播.</summary>
        void LayoutSequence(IReadOnlyList<Group> items, int level, bool relationshipFilter = false)
        {
            Indent(items, level);
            for (var index = 0; index < items.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Keyword(items[index], "DEFINE"))
                {
                    Line(items[index++].Start, level);
                    var end = index;
                    while (end < items.Count && !Keyword(items[end], "EVALUATE")) end++;
                    LayoutDefinitions(items.Skip(index).Take(end - index).ToArray(), level + 1);
                    index = end;
                }
                else if (Keyword(items[index], "VAR"))
                {
                    var first = index;
                    index = LayoutVariables(items, index, level);
                    if (FollowsOperator(items, first)) layout[items[first].Start] = new(level);
                }
                else if (Keyword(items[index], "EVALUATE"))
                {
                    Line(items[index].Start, level, index > 0);
                    index++;
                    var end = index;
                    while (end < items.Count && !Keyword(items[end], "ORDER") && !Keyword(items[end], "EVALUATE")) end++;
                    var body = items.Skip(index).Take(end - index).ToArray();
                    LayoutSequence(body, level);
                    if (body.Length > 0) Line(body[0].Start, level);
                    index = end;
                }
                else
                {
                    if (Keyword(items[index], "ORDER") || Keyword(items[index], "START")) Line(items[index].Start, level);
                    LayoutGroup(items, index++, level, relationshipFilter);
                }
            }
            LayoutOperators(items, level);
        }

        /// <summary>规划 DEFINE 声明组与函数参数, 不要求查询 VAR 带 RETURN; 声明或签名残缺时失败.</summary>
        void LayoutDefinitions(IReadOnlyList<Group> items, int level)
        {
            Indent(items, level);
            var index = 0;
            while (index < items.Count)
            {
                if (!Declaration(items, index)) throw Invalid();
                var function = Keyword(items[index], "FUNCTION");
                Line(items[index++].Start, level);
                while (index < items.Count && tokens[items[index].Start].Text != "=") { cancellationToken.ThrowIfCancellationRequested(); index++; }
                if (++index >= items.Count) throw Invalid();
                if (function)
                {
                    // FUNCTION 的参数声明不是普通调用, 参数与函数正文始终分行.
                    var signature = items[index];
                    if (signature.Children is not { } parameters || tokens[signature.Start].Text != "("
                        || index + 2 >= items.Count || tokens[items[index + 1].Start].Text != "=>") throw Invalid();
                    LayoutSequence(parameters, level + 2);
                    if (parameters.Count > 0)
                    {
                        Line(parameters[0].Start, level + 2);
                        for (var item = 1; item < parameters.Count; item++)
                            if (tokens[parameters[item - 1].Start].Text == ",") Line(parameters[item].Start, level + 2);
                        Line(signature.End - 1, level + 1);
                    }
                    index += 2;
                    var bodyStart = index;
                    if (Keyword(items[index], "VAR")) index = LayoutVariables(items, index, level + 1);
                    else index = LayoutUntilFlow(items, index, level + 1);
                    Line(items[bodyStart].Start, level + 1);
                }
                else if (Keyword(items[index], "VAR"))
                {
                    var bodyStart = index;
                    index = LayoutVariables(items, index, level);
                    if (!Fits(items[bodyStart].Start, items[index - 1].End)) Line(items[bodyStart].Start, level);
                }
                else
                {
                    var start = index;
                    while (index < items.Count && !Declaration(items, index)) index++;
                    var body = items.Skip(start).Take(index - start).ToArray();
                    if (body.Length == 0) throw Invalid();
                    var functionValue = HasCall(body[0].Start, body[^1].End);
                    LayoutSequence(body, functionValue ? level + 1 : level);
                    if (functionValue) Line(body[0].Start, level + 1);
                }
            }
        }

        /// <summary>核对指定位置的完整声明名称与等号, 返回是否为查询定义, 不以固定前视长度猜测名称.</summary>
        bool Declaration(IReadOnlyList<Group> items, int index)
        {
            if (!DefinitionKeywords.Any(word => Keyword(items[index], word))) return false;
            var definition = tokens[items[index].Start].Text.ToUpperInvariant();
            var next = index + 1;
            if (next >= items.Count || items[next].Children is not null || !Name(tokens[items[next].Start])) return false;
            next++;
            if (definition is "MEASURE" or "COLUMN")
            {
                if (next >= items.Count || tokens[items[next].Start].Kind != Kind.Reference) return false;
                next++;
            }
            else if (definition == "FUNCTION")
                while (next < items.Count && tokens[items[next].Start].Text == ".")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++next >= items.Count || items[next].Children is not null || !Name(tokens[items[next].Start])) return false;
                    next++;
                }
            // 识别完整定义头, 不凭固定词元数前视, 也不将 TABLE(...) 等普通调用当成定义.
            return next < items.Count && items[next].Children is null && tokens[items[next].Start].Text == "=";
        }

        // 保留标识符原文和大小写; 名称合法性仍由 DAX 引擎负责.
        bool Name(Token value) => value.Kind == Kind.Word || value.Kind == Kind.Literal && value.Text.StartsWith('\'');

        bool FollowsOperator(IReadOnlyList<Group> items, int index) => index > 0 && items[index - 1].Children is null
            && (tokens[items[index - 1].Start].Text is "+" or "-" or "*" or "/" or "^" or "&" or "&&" or "||" or "=" or "==" or "<>" or "<" or ">" or "<=" or ">="
                || Keyword(items[index - 1], "NOT") || Keyword(items[index - 1], "IN"));

        /// <summary>从指定 VAR 开始规划独立作用域并返回结束下标, 残缺 RETURN 或超深嵌套失败; 外层声明不被消费.</summary>
        int LayoutVariables(IReadOnlyList<Group> items, int index, int level)
        {
            if (level - expressionBaseLevel > 64 || ++variableDepth > 64) throw new AppException("DAX_FORMAT_LIMIT", "DAX 嵌套层级过深");
            try
            {
                var blockStart = index;
                while (index < items.Count && Keyword(items[index], "VAR"))
                {
                    var declarationStart = index;
                    Line(items[index].Start, level);
                    if (index + 3 >= items.Count || tokens[items[index + 2].Start].Text != "=") throw Invalid();
                    index += 3;
                    var valueStart = index;
                    if (Keyword(items[index], "VAR")) index = LayoutVariables(items, index, level + 1);
                    else index = LayoutUntilFlow(items, index, level + 1);
                    var valueEnd = items[index - 1].End;
                    var functionValue = HasCall(items[valueStart].Start, valueEnd);
                    if (functionValue) Line(items[valueStart].Start, level + 1);
                    else
                    {
                        // 标量赋值紧随等号; 表构造或纯标量嵌套变量仍以自身结构决定后续行.
                        for (var tokenIndex = items[valueStart].Start; tokenIndex < valueEnd; tokenIndex++)
                            layout[tokenIndex] = layout[tokenIndex] with { Indent = Math.Max(level, layout[tokenIndex].Indent - 1) };
                        if (!Keyword(items[valueStart], "VAR")) layout[items[valueStart].Start] = new(level);
                        else if (Fits(items[declarationStart].Start, valueEnd)) Inline(items[valueStart].Start, valueEnd, level);
                    }
                }
                if (index >= items.Count || !Keyword(items[index], "RETURN")) throw Invalid();
                var returnIndex = index;
                Line(items[index++].Start, level);
                if (index >= items.Count) throw Invalid();
                var start = index;
                index = Keyword(items[index], "VAR") ? LayoutVariables(items, index, level + 1) : LayoutUntilFlow(items, index, level + 1);
                Line(items[start].Start, level + 1);
                if (items[returnIndex].Start > 0 && tokens[items[returnIndex].Start - 1].Kind is Kind.Comment or Kind.LineComment
                    && FlatLength(items[blockStart].Start, items[index - 1].End) <= 80
                    && Fits(items[returnIndex].Start, items[index - 1].End))
                {
                    Inline(items[returnIndex].Start, items[index - 1].End, level);
                    Line(items[returnIndex].Start, level);
                }
                if (Fits(items[blockStart].Start, items[index - 1].End)) Inline(items[blockStart].Start, items[index - 1].End, level);
                return index;
            }
            finally { variableDepth--; }
        }

        /// <summary>规划从 start 到下一个流程边界的表达式并返回下标, 二元运算中的 VAR 保留自身作用域; 空值失败.</summary>
        int LayoutUntilFlow(IReadOnlyList<Group> items, int start, int level)
        {
            var end = start;
            while (end < items.Count && !Flow(items[end]) && !Declaration(items, end) && !Keyword(items[end], "EVALUATE") && !Keyword(items[end], "ORDER")) end++;
            if (end == start) throw Invalid();
            var slice = items.Skip(start).Take(end - start).ToArray();
            Indent(slice, level);
            for (var index = 0; index < slice.Length; index++) LayoutGroup(slice, index, level);
            LayoutOperators(slice, level);
            if (end < items.Count && Keyword(items[end], "VAR") && FollowsOperator(items, end))
            {
                var nested = end;
                end = LayoutVariables(items, end, level);
                layout[items[nested].Start] = new(level);
                if (FlatLength(items[start].Start, items[end - 1].End) <= ExpressionWidth()
                    && !HasComments(items[start].Start, items[end - 1].End)) Inline(items[start].Start, items[end - 1].End, level);
            }
            return end;
        }

        /// <summary>以后序子树规划指定层级的二元表达式, 比较与尾部标量保持绑定; 显式栈避免长链递归溢出.</summary>
        void LayoutOperators(IReadOnlyList<Group> items, int level)
        {
            if (items.Count == 0 || items.Any(item => Flow(item) || Keyword(item, "DEFINE") || Keyword(item, "EVALUATE")
                || Keyword(item, "ORDER") || Keyword(item, "START"))) return;
            var expressions = new List<(int Start, int End, int Operator, int Left, int Right)>();
            var operands = new Stack<int>();
            var operators = new Stack<int>();
            var atomStart = 0;
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[index];
                var precedence = item.Children is null ? BinaryPrecedence(tokens[item.Start].Text) : 0;
                if (precedence == 0 || IsUnary(tokens, item.Start)) continue;
                if (index == atomStart) return;
                AddAtom(atomStart, index);
                while (operators.TryPeek(out var previous) && BinaryPrecedence(tokens[items[previous].Start].Text) >= precedence) Combine();
                operators.Push(index);
                atomStart = index + 1;
            }
            if (operators.Count == 0) return;
            if (atomStart >= items.Count) return;
            AddAtom(atomStart, items.Count);
            while (operators.Count > 0) Combine();
            foreach (var (start, end, operatorIndex, _, rightIndex) in expressions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operatorIndex < 0) continue;
                if (FlatLength(start, end) <= ExpressionWidth() && !HasComments(start, end))
                {
                    Inline(start, end, level);
                    continue;
                }
                var symbol = tokens[operatorIndex].Text;
                if (symbol is "=" or "==" or "<>" or "<" or ">" or "<=" or ">=" or "IN") continue;
                var (rightStart, rightEnd, _, _, _) = expressions[rightIndex];
                if (symbol is not ("&&" or "||") && !HasCall(rightStart, rightEnd)) continue;
                for (var index = operatorIndex; index < end; index++)
                    layout[index] = layout[index] with { Indent = layout[index].Indent + 1 };
                Line(operatorIndex, level + 1);
            }

            /// <summary>把组下标半开区间登记为原子, 内部调用/括号已排版, 返回值通过操作数栈交给组合步骤.</summary>
            void AddAtom(int start, int end)
            {
                operands.Push(expressions.Count);
                expressions.Add((items[start].Start, items[end - 1].End, -1, -1, -1));
            }

            /// <summary>按已核对的操作数栈组合一个二元节点, 追加到后序列表, 不递归处理长左结合链.</summary>
            void Combine()
            {
                var op = operators.Pop();
                var right = operands.Pop();
                var left = operands.Pop();
                operands.Push(expressions.Count);
                expressions.Add((expressions[left].Start, expressions[right].End, items[op].Start, left, right));
            }
        }

        /// <summary>规划兄弟组中的调用/构造器, 保留缺省参数和成对参数; 筛选标记只允许直接关系函数使用特例.</summary>
        void LayoutGroup(IReadOnlyList<Group> siblings, int index, int level, bool relationshipFilter = false)
        {
            var group = siblings[index];
            if (group.Children is not { } children) return;
            var name = index > 0 && siblings[index - 1].Children is null && tokens[siblings[index - 1].Start].Kind == Kind.Word
                ? tokens[siblings[index - 1].Start].Text.ToUpperInvariant() : "";
            var call = name.Length > 0 && name is not ("IN" or "NOT" or "RETURN");
            var arguments = new List<IReadOnlyList<Group>>();
            var start = 0;
            for (var item = 0; item < children.Count; item++)
                if (tokens[children[item].Start].Text is "," or ";")
                {
                    arguments.Add(children.Skip(start).Take(item - start).ToArray()); start = item + 1;
                }
            arguments.Add(children.Skip(start).ToArray());
            var comments = tokens.Skip(group.Start + 1).Take(group.End - group.Start - 2).Any(t => t.Kind is Kind.Comment or Kind.LineComment);
            var flatStart = group.Start;
            if (call)
            {
                flatStart--;
                while (flatStart > 1 && tokens[flatStart - 1].Text == "." && tokens[flatStart - 2].Kind == Kind.Word) flatStart -= 2;
            }
            var nestedCall = children.Where((child, childIndex) => childIndex > 0 && child.Children is not null
                && tokens[child.Start].Text == "(" && tokens[children[childIndex - 1].Start].Kind == Kind.Word).Any();
            var membership = index > 0 && Keyword(siblings[index - 1], "IN");
            var leadingComment = flatStart > 0 && tokens[flatStart - 1].Kind is Kind.Comment or Kind.LineComment;
            var inlineRelationship = relationshipFilter && name is "USERELATIONSHIP" or "CROSSFILTER";
            var multiline = name != "DATE" && !inlineRelationship && children.Count > 0 && (comments || call && leadingComment || !Fits(flatStart, group.End)
                && (tokens[group.Start].Text == "{" && !membership || children.Any(Flow)
                    || call && (arguments.Count > 1 || nestedCall)));
            var innerLevel = multiline ? level + 1 : level;
            for (var tokenIndex = group.Start + 1; tokenIndex < group.End - 1; tokenIndex++) layout[tokenIndex] = new(innerLevel);
            for (var item = 0; item < arguments.Count; item++)
            {
                var argument = arguments[item];
                LayoutSequence(argument, innerLevel, item > 0 && name is "CALCULATE" or "CALCULATETABLE");
                if (multiline && argument.Count > 0) Line(argument[0].Start, innerLevel);
            }
            if (multiline)
            {
                var pairStart = name switch { "SWITCH" or "ADDCOLUMNS" or "SELECTCOLUMNS" => 1, "ROW" => 0, _ => -1 };
                if (name is "SUMMARIZE" or "SUMMARIZECOLUMNS")
                    pairStart = arguments.FindIndex(argument => argument.Count > 0 && tokens[argument[0].Start].Kind == Kind.Literal && tokens[argument[0].Start].Text.StartsWith('"'));
                for (var item = pairStart; item >= 0 && item + 1 < arguments.Count; item += 2)
                {
                    var value = arguments[item + 1];
                    if (value.Count == 0) continue;
                    var valueMultiline = Enumerable.Range(value[0].Start + 1, value[^1].End - value[0].Start - 1).Any(i => layout[i].NewLine);
                    var binaryValue = Enumerable.Range(value[0].Start, value[^1].End - value[0].Start)
                        .Any(i => BinaryPrecedence(tokens[i].Text) > 0 && !IsUnary(tokens, i));
                    if (valueMultiline || style == DaxFormattingStyle.Short && binaryValue)
                    {
                        // 宽度判断与显示层级无关, 只提升已经完成的子树, 避免嵌套参数对重复递归排版.
                        for (var tokenIndex = value[0].Start; tokenIndex < value[^1].End; tokenIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            layout[tokenIndex] = layout[tokenIndex] with { Indent = layout[tokenIndex].Indent + 1 };
                        }
                        Line(value[0].Start, innerLevel + 1);
                    }
                    else layout[value[0].Start] = new(innerLevel);
                }
                // 缺省参数也占独立参数行, 保留原逗号和参数顺序.
                for (var item = 1; item < children.Count; item++)
                    if (tokens[children[item].Start].Text == "," && tokens[children[item - 1].Start].Text == ",") Line(children[item].Start, innerLevel);
            }
            if (multiline) Line(group.End - 1, level);
        }
    }
}
