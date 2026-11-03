// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxAssert.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 忽略 DAX 代码空白但保留字符串及对象标识符原文的生成器行为断言.

using System.Text.RegularExpressions;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅供生成器业务断言使用; 排版约定另以完整输出和格式化器测试验证.</summary>
internal static partial class DaxAssert
{
    /// <summary>检查生成表达式包含指定词法片段, 不受缩进和参数换行影响.</summary>
    /// <param name="expected">预期 DAX 片段, 字符串和对象名称仍严格比较.</param>
    /// <param name="actual">实际完整表达式.</param>
    internal static void Contains(string expected, string actual) => Assert.Contains(Compact(expected), Compact(actual));

    /// <summary>删除代码空白, 保留 DAX 字符串、带引号表名和方括号引用内部的原始空白.</summary>
    /// <param name="expression">被断言的生成式或片段.</param>
    /// <returns>只剔除代码空白的词法比较视图.</returns>
    private static string Compact(string expression) => Trivia().Replace(expression,
        match => char.IsWhiteSpace(match.Value[0]) ? "" : match.Value);

    /// <summary>将文本和对象标识符整体保护, 仅独立代码空白参与剔除.</summary>
    [GeneratedRegex("\"(?:[^\"]|\"\")*\"|'(?:[^']|'')*'|\\[(?:[^\\]]|\\]\\])*\\]|\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Trivia();
}
