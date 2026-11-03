// FilePath    : pbi-utils\src\PbiUtils.Application\Code\PowerQuery\PowerQueryFormatter.Parser.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 本地 M 表达式和类型结构解析, 保留广义标识符中的语义空白.

namespace PbiUtils.Application.Code.PowerQuery;

public static partial class PowerQueryFormatter
{
    /// <summary>只分析格式化所需语法; 不求值, 不验证函数或数据源是否存在.</summary>
    private sealed class Parser(string source, IReadOnlyList<Token> tokens, CancellationToken cancellationToken)
    {
        private static readonly HashSet<string> Reserved = new("and as each else error false if in is let meta not null or otherwise section shared then true try type".Split(' '), StringComparer.Ordinal);
        private int position;
        private int depth;
        internal Dictionary<int, string> ProtectedGaps { get; } = [];
        internal List<string> Names { get; } = [];

        /// <summary>完整消费表达式或 section 文档, 不接受被忽略的尾部语法.</summary>
        internal Node Parse()
        {
            Node root;
            if (At("section"))
            {
                var start = Take("section");
                Identifier();
                Take(";");
                var members = new List<Node>();
                while (!Done)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var memberStart = Current;
                    var children = new List<Node>();
                    if (At("[")) children.Add(Record());
                    _ = Match("shared");
                    Identifier();
                    Take("=");
                    children.Add(Expression());
                    Take(";");
                    members.Add(new("member", memberStart, position, children));
                }
                root = new("section", start, position, members);
            }
            else root = Expression();
            if (!Done) throw Invalid();
            return root;
        }

        /// <summary>Pratt 解析保留原始运算符次序; 同优先级链平铺以避免长链递归耗尽栈.</summary>
        private Node Expression(int minimumPrecedence = 0)
        {
            Enter();
            try
            {
                var left = Prefix();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var precedence = Precedence(Text);
                    if (precedence < minimumPrecedence) break;
                    var operation = Text;
                    Advance();
                    var right = operation is "as" or "is" ? Type() : Expression(precedence + 1);
                    var kind = "binary" + precedence;
                    if (left.Kind == kind && left.Children is List<Node> chain)
                    {
                        chain.Add(right);
                        left = left with { End = right.End };
                    }
                    else left = new(kind, left.Start, right.End, new List<Node> { left, right });
                }
                return left;
            }
            finally { depth--; }
        }

        /// <summary>解析流程表达式, 前缀与原子; 后缀访问交给统一路径处理.</summary>
        private Node Prefix()
        {
            var start = Current;
            if (Match("let"))
            {
                var bindings = new List<Node>();
                do
                {
                    var bindingStart = Current;
                    Identifier();
                    Take("=");
                    var value = Expression();
                    bindings.Add(new("binding", bindingStart, value.End, [value]));
                } while (Match(","));
                var separator = Take("in");
                bindings.Add(Expression());
                return new("let", start, position, bindings, separator);
            }
            if (Match("if"))
            {
                var condition = Expression();
                Take("then");
                var whenTrue = Expression();
                var separator = Take("else");
                var whenFalse = Expression();
                return new("if", start, position, [condition, whenTrue, whenFalse], separator);
            }
            if (Match("each"))
            {
                var child = Expression();
                return new("each", start, child.End, [child]);
            }
            if (Match("try"))
            {
                var values = new List<Node> { Expression() };
                if (Match("otherwise") || Match("catch")) values.Add(Expression());
                return new("try", start, position, values);
            }
            if (At("+") || At("-") || At("not") || At("@") || At("error"))
            {
                Advance();
                var value = Expression(10);
                return new("unary", start, value.End, [value]);
            }
            if (Match("type"))
            {
                var type = Type();
                return Postfix(new("type", start, type.End, [type]));
            }
            if (At("("))
            {
                var function = IsFunction();
                Advance();
                if (function)
                {
                    var parameters = new List<Node>();
                    if (!At(")"))
                        do
                        {
                            var parameterStart = Current;
                            _ = Match("optional");
                            Identifier();
                            var type = Match("as") ? Type() : null;
                            parameters.Add(new("parameter", parameterStart, position, type is null ? [] : [type]));
                        } while (Match(","));
                    Take(")");
                    var arguments = new Node("parameters", start, position, parameters);
                    var children = new List<Node> { arguments };
                    if (Match("as")) children.Add(Type());
                    var arrow = Take("=>");
                    children.Add(Expression());
                    return new("function", start, position, children, arrow);
                }
                var nested = Expression();
                Take(")");
                return Postfix(new("parentheses", start, position, [nested]));
            }
            if (At("[")) return Postfix(Record());
            if (Match("{")) return Postfix(Sequence("list", start, "}", false));
            if (Current < tokens.Count && (tokens[Current].Kind is TokenKind.Number or TokenKind.Text or TokenKind.Verbatim or TokenKind.QuotedName
                || tokens[Current].Kind == TokenKind.Word && (!Reserved.Contains(Text) || Text is "true" or "false" or "null")))
            {
                Advance();
                return Postfix(new("atom", start, position, []));
            }
            throw Invalid();
        }

        /// <summary>按闭括号后的结构确定函数头, 不将括号表达式或类型断言误识别为函数.</summary>
        private bool IsFunction()
        {
            var nesting = 0;
            for (var index = Current; index < tokens.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsComment(tokens[index])) continue;
                if (tokens[index].Text == "(") nesting++;
                else if (tokens[index].Text == ")" && --nesting == 0)
                {
                    var next = Next(index + 1);
                    if (next < tokens.Count && tokens[next].Text == "=>") return true;
                    if (next >= tokens.Count || tokens[next].Text != "as") return false;
                    // 返回类型可能携带括号和 metadata; 嵌套内的箭头不算当前函数头.
                    var saved = position;
                    try
                    {
                        position = next + 1;
                        _ = Type();
                        return At("=>");
                    }
                    finally { position = saved; }
                }
            }
            return false;
        }

        /// <summary>解析调用, 项目访问和字段选择/投影, 后缀问号不改变节点边界.</summary>
        private Node Postfix(Node value)
        {
            while (!Done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 普通限定名称已作为一个原始词元扫描; 点号不能跨空白或接到字符串上.
                if (At("("))
                {
                    var start = Take("(");
                    var arguments = Sequence("arguments", start, ")", false);
                    value = new("call", value.Start, position, [value, arguments]);
                }
                else if (At("["))
                {
                    var fields = Selector();
                    value = new("access", value.Start, position, [value, fields]);
                }
                else if (Match("{"))
                {
                    var item = Expression();
                    Take("}");
                    _ = Match("?");
                    value = new("item", value.Start, position, [value, item]);
                }
                else break;
            }
            return value;
        }

        /// <summary>解析逗号分隔的列表/调用参数, 明确拒绝残缺尾项.</summary>
        private Node Sequence(string kind, int start, string closing, bool types)
        {
            var children = new List<Node>();
            if (!At(closing)) do { children.Add(types ? Type() : Expression()); } while (Match(","));
            Take(closing);
            return new(kind, start, position, children);
        }

        /// <summary>记录字段和隐式字段选择共用方括号, 由等号明确区分.</summary>
        private Node Record()
        {
            var start = Take("[");
            if (Match("]")) return new("record", start, position, []);
            if (At("["))
            {
                position = start;
                return Selector();
            }
            var name = FieldName();
            if (!Match("="))
            {
                Take("]");
                _ = Match("?");
                return new("selector", start, position, [name]);
            }
            var children = new List<Node>();
            while (true)
            {
                var value = Expression();
                children.Add(new("field", name.Start, value.End, [name, value]));
                if (!Match(",")) break;
                name = FieldName();
                Take("=");
            }
            Take("]");
            return new("record", start, position, children);
        }

        /// <summary>显式字段访问只允许名称, 投影中的每个名称拥有独立方括号.</summary>
        private Node Selector()
        {
            var start = Take("[");
            var fields = new List<Node>();
            if (At("["))
            {
                do
                {
                    var fieldStart = Take("[");
                    var name = FieldName();
                    Take("]");
                    fields.Add(new("selector", fieldStart, position, [name]));
                } while (Match(","));
            }
            else fields.Add(FieldName());
            Take("]");
            _ = Match("?");
            return new("selector", start, position, fields);
        }

        /// <summary>广义字段名称的整个源片段保持原样, 内部空白不交给布局引擎.</summary>
        private Node FieldName()
        {
            var start = Current;
            var end = start;
            while (!Done && (tokens[Current].Kind is TokenKind.Word or TokenKind.QuotedName or TokenKind.Number || At(".")))
            {
                end = Current;
                Advance();
            }
            if (end == start && position <= start) throw Invalid();
            if (start >= tokens.Count || tokens[start].Kind is not (TokenKind.Word or TokenKind.QuotedName or TokenKind.Number)) throw Invalid();
            if (tokens[end].Text == ".") throw Invalid();
            var raw = source[tokens[start].Start..tokens[end].End];
            if (raw.Any(LineEnd)) throw Invalid();
            Names.Add(raw);
            for (var index = start + 1; index <= end; index++) ProtectedGaps[index] = source[tokens[index - 1].End..tokens[index].Start];
            return new("name", start, position, []);
        }

        /// <summary>支持 nullable, 记录/列表/表/函数类型及带 metadata 的括号类型表达式.</summary>
        private Node Type()
        {
            Enter();
            try
            {
                var start = Current;
                if (Match("nullable"))
                {
                    var child = Type();
                    return new("nullable", start, position, [child]);
                }
                if (Match("("))
                {
                    var expression = Expression();
                    Take(")");
                    return new("parentheses", start, position, [expression]);
                }
                if (At("[") || At("table") && Next(Current + 1) < tokens.Count && tokens[Next(Current + 1)].Text == "[")
                {
                    _ = Match("table");
                    var recordStart = Take("[");
                    var fields = new List<Node>();
                    if (!At("]"))
                        do
                        {
                            var fieldStart = Current;
                            if (Match("...")) { fields.Add(new("atom", fieldStart, position, [])); break; }
                            _ = Match("optional");
                            var name = FieldName();
                            Take("=");
                            var fieldType = Type();
                            fields.Add(new("typeField", fieldStart, position, [name, fieldType]));
                        } while (Match(","));
                    Take("]");
                    var record = new Node("record", recordStart, position, fields);
                    return start == recordStart ? record : new("tableType", start, position, [record]);
                }
                if (Match("{")) return Sequence("list", start, "}", true);
                if (Match("function"))
                {
                    var parameterStart = Take("(");
                    var parameters = new List<Node>();
                    if (!At(")"))
                        do
                        {
                            var itemStart = Current;
                            _ = Match("optional");
                            Identifier();
                            Take("as");
                            var parameterType = Type();
                            parameters.Add(new("parameter", itemStart, position, [parameterType]));
                        } while (Match(","));
                    Take(")");
                    var arguments = new Node("parameters", parameterStart, position, parameters);
                    Take("as");
                    var result = Type();
                    return new("functionType", start, position, [arguments, result]);
                }
                if (Done || tokens[Current].Kind != TokenKind.Word || Reserved.Contains(Text) && Text is not ("type")) throw Invalid();
                Advance();
                return new("typeAtom", start, position, []);
            }
            finally { depth--; }
        }

        /// <summary>裸名称必须不是保留关键字; quoted 名称保留原文.</summary>
        private void Identifier()
        {
            if (Done || tokens[Current].Kind != TokenKind.QuotedName && (tokens[Current].Kind != TokenKind.Word || Reserved.Contains(Text))) throw Invalid();
            Advance();
        }

        /// <summary>为每次递归显式计数, 顶层表达式本身不计入 128 层嵌套.</summary>
        private void Enter()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++depth > MaximumDepth + 1) throw Limit();
        }

        /// <summary>显式列出支持的二元运算符, 不猜测未知语法.</summary>
        private static int Precedence(string value) => value switch
        {
            "meta" => 0,
            "??" => 1,
            "or" => 2,
            "and" => 3,
            "as" or "is" => 4,
            "=" or "<>" => 5,
            "<" or ">" or "<=" or ">=" => 6,
            "+" or "-" or "&" => 7,
            "*" or "/" => 8,
            ".." => 9,
            _ => -1
        };

        /// <summary>注释不参与语法, 其源顺序由最终词元写入器保留.</summary>
        private int Next(int index)
        {
            while (index < tokens.Count && IsComment(tokens[index])) { cancellationToken.ThrowIfCancellationRequested(); index++; }
            return index;
        }
        private int Current => Next(position);
        private string Text => Current < tokens.Count ? tokens[Current].Text : "";
        private bool Done => Current >= tokens.Count;
        /// <summary>精确匹配大小写敏感的 M 关键字或符号.</summary>
        private bool At(string value) => Text == value;
        /// <summary>消费一个已确认的有效词元.</summary>
        private void Advance() { cancellationToken.ThrowIfCancellationRequested(); position = Current + 1; }
        /// <summary>可选消费关键字或分隔符.</summary>
        private bool Match(string value) { if (!At(value)) return false; Advance(); return true; }
        /// <summary>必须消费指定结构符, 残缺输入不补齐.</summary>
        private int Take(string value) { var index = Current; if (!Match(value)) throw Invalid(); return index; }
    }

    /// <summary>块注释和行注释均保留为独立原始词元.</summary>
    private static bool IsComment(Token value) => value.Kind is TokenKind.BlockComment or TokenKind.LineComment;
}
