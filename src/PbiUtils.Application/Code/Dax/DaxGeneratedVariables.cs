// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxGeneratedVariables.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为受信内置 DAX 模板分配避开模型表名的变量, 不重命名用户表达式.

using System.Globalization;
using System.Text.RegularExpressions;

namespace PbiUtils.Application.Code.Dax;

/// <summary>只在插入业务标识符之前处理工具自有模板, 不用于格式化或改写任意用户 DAX.</summary>
internal static partial class DaxGeneratedVariables
{
    /// <summary>按声明顺序分配确定的 PascalCase 数字后缀, 同时避开表名及其他原始变量名.</summary>
    /// <param name="variables">受信模板的完整变量名集合.</param>
    /// <param name="tableNames">当前及同批后续创建的全部模型表名, 使用大小写无关语义.</param>
    /// <returns>从每个原变量名到唯一可用名字的只读映射, 未冲突的变量保持原名.</returns>
    internal static IReadOnlyDictionary<string, string> Reserve(IEnumerable<string> variables, IReadOnlySet<string> tableNames)
    {
        var declared = variables.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var occupied = declared.Concat(tableNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preferred in declared)
        {
            var name = preferred;
            var suffix = 1;
            while (tableNames.Contains(name) || name != preferred && occupied.Contains(name))
                name = preferred + (suffix++).ToString(CultureInfo.InvariantCulture);
            occupied.Add(name);
            names.Add(preferred, name);
        }
        return names;
    }

    /// <summary>仅改写受信模板中 VAR 的裸词元声明和引用, 字符串、注释、表名及列引用原文保持不变.</summary>
    /// <param name="template">尚未插入用户名称的固定 DAX 模板.</param>
    /// <param name="tableNames">完整已占用表名集合.</param>
    /// <returns>变量已安全避让的模板, 插值占位符和计算操作保持原样.</returns>
    internal static string AvoidTableNames(string template, IReadOnlySet<string> tableNames)
    {
        var words = LexicalParts().Matches(template).Where(match => match.Groups["word"].Success).ToArray();
        var declarations = words.Zip(words.Skip(1))
            .Where(pair => pair.First.Value.Equals("VAR", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Second.Value);
        var names = Reserve(declarations, tableNames);
        return LexicalParts().Replace(template, match => match.Groups["word"].Success && names.TryGetValue(match.Value, out var name)
            ? name : match.Value);
    }

    /// <summary>受信生成模板的词法片段, 裸词组可重命名, 其余片段整体保护.</summary>
    [GeneratedRegex("""//[^\r\n]*|--[^\r\n]*|/\*[\s\S]*?\*/|"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|(?<word>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex LexicalParts();
}
