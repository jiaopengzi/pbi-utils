// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormatterContextTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定官方 API 的上下文、宽度边界与运算符排版实证, 测试完全离线运行.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>预期值来自 2026-10-30 实际 SQLBI API 响应, 仅统一 CRLF 和移除固定定义头/末尾换行.</summary>
public sealed class DaxFormatterContextTests
{
    /// <summary>枚举关系过滤上下文、参数对、40/80字符边界与运算符的独立官方回归样例.</summary>
    /// <returns>用例名称、原表达式、官方正文及其 Short/Long 样式, 无文件或网络依赖.</returns>
    public static TheoryData<string, string, string, DaxFormattingStyle> ContextCases()
    {
        var cases = new TheoryData<string, string, string, DaxFormattingStyle>
        {
            {
                "relationship",
                """USERELATIONSHIP(T[A],S[A])""",
                """
            USERELATIONSHIP (
                T[A],
                S[A]
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "crossfilter",
                """CROSSFILTER(T[A],S[A],BOTH)""",
                """
            CROSSFILTER (
                T[A],
                S[A],
                BOTH
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "calc-relationship",
                """CALCULATE([A],USERELATIONSHIP(T[A],S[A]))""",
                """
            CALCULATE (
                [A],
                USERELATIONSHIP ( T[A], S[A] )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "calc-crossfilter",
                """CALCULATE([A],CROSSFILTER(T[A],S[A],BOTH))""",
                """
            CALCULATE (
                [A],
                CROSSFILTER ( T[A], S[A], BOTH )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "calculate-relationship",
                """CALCULATETABLE(T,USERELATIONSHIP(T[A],S[A]))""",
                """
            CALCULATETABLE (
                T,
                USERELATIONSHIP ( T[A], S[A] )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "sumx-relationship",
                """SUMX(T,USERELATIONSHIP(T[A],S[A]))""",
                """
            SUMX (
                T,
                USERELATIONSHIP (
                    T[A],
                    S[A]
                )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "cross-calculatetable",
                """CALCULATETABLE(T,CROSSFILTER(T[A],S[A],BOTH))""",
                """
            CALCULATETABLE (
                T,
                CROSSFILTER ( T[A], S[A], BOTH )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "cross-sumx",
                """SUMX(T,CROSSFILTER(T[A],S[A],BOTH))""",
                """
            SUMX (
                T,
                CROSSFILTER (
                    T[A],
                    S[A],
                    BOTH
                )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "switch-R1",
                """SWITCH(TRUE(),[A]=1,R1,0)""",
                """
            SWITCH (
                TRUE (),
                [A] = 1, R1,
                0
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "switch-R1+R2",
                """SWITCH(TRUE(),[A]=1,R1+R2,0)""",
                """
            SWITCH (
                TRUE (),
                [A] = 1,
                    R1 + R2,
                0
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "switch-SUM(T[A])",
                """SWITCH(TRUE(),[A]=1,SUM(T[A]),0)""",
                """
            SWITCH (
                TRUE (),
                [A] = 1, SUM ( T[A] ),
                0
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "add-DIVIDE(R1,R2)",
                """ADDCOLUMNS(T,"X",DIVIDE(R1,R2))""",
                """
            ADDCOLUMNS (
                T,
                "X",
                    DIVIDE (
                        R1,
                        R2
                    )
            )
            """,
                DaxFormattingStyle.Short
            },
            {
                "long-name-85-R1+R2",
                """ADDCOLUMNS(T,"XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",R1+R2,"padding",1)""",
                """
            ADDCOLUMNS (
                T,
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", R1 + R2,
                "padding", 1
            )
            """,
                DaxFormattingStyle.Long
            },
            {
                "boolean-40",
                """[AAAAAAAAAAAA]=1&&[BBBBBBBBBBBB]=2""",
                """[AAAAAAAAAAAA] = 1 && [BBBBBBBBBBBB] = 2""",
                DaxFormattingStyle.Short
            },
            {
                "boolean-41",
                """[AAAAAAAAAAAA]=1&&[BBBBBBBBBBBBB]=2""",
                """
            [AAAAAAAAAAAA] = 1
                && [BBBBBBBBBBBBB] = 2
            """,
                DaxFormattingStyle.Short
            },
            {
                "width-62-root",
                """DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] )""",
                """DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] )""",
                DaxFormattingStyle.Long
            },
            {
                "width-63-root",
                """DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] )""",
                """
            DIVIDE (
                [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX],
                [B]
            )
            """,
                DaxFormattingStyle.Long
            },
            {
                "width-62-nested",
                """IF(1,IF(1,DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] ),0),0)""",
                """
            IF (
                1,
                IF (
                    1,
                    DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] ),
                    0
                ),
                0
            )
            """,
                DaxFormattingStyle.Long
            },
            {
                "width-63-nested",
                """IF(1,IF(1,DIVIDE ( [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX], [B] ),0),0)""",
                """
            IF (
                1,
                IF (
                    1,
                    DIVIDE (
                        [XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX],
                        [B]
                    ),
                    0
                ),
                0
            )
            """,
                DaxFormattingStyle.Long
            },
            {
                "precedence-parentheses",
                """[A]+([B]*SUM(T[C]))-1""",
                """[A] + ( [B] * SUM ( T[C] ) ) - 1""",
                DaxFormattingStyle.Short
            },
            {
                "precedence-mixed",
                """[A]+SUM(T[B])*2-SUM(T[C])/3""",
                """
            [A] + SUM ( T[B] ) * 2
                - SUM ( T[C] ) / 3
            """,
                DaxFormattingStyle.Short
            },
            {
                "unary-reference",
                """-[A]""",
                """- [A]""",
                DaxFormattingStyle.Long
            },
            {
                "unary-variable",
                """-Variable""",
                """- Variable""",
                DaxFormattingStyle.Long
            },
            {
                "unary-number",
                """-1""",
                """-1""",
                DaxFormattingStyle.Long
            },
            {
                "unary-parentheses",
                """-([A]+1)""",
                """- ( [A] + 1 )""",
                DaxFormattingStyle.Long
            }
        };
        return cases;
    }

    /// <summary>官方布局必须同时满足完整定义与实际度量正文入口, 重复格式化不得改变上下文或优先级.</summary>
    /// <param name="name">报告中的边界名称, 区分同名函数所处的位置.</param>
    /// <param name="source">人工探针正文; 本组没有含内部换行的字面量或注释.</param>
    /// <param name="expected">实际 API 正文, 包含原始函数、比较和算术词元的顺序与括号.</param>
    /// <param name="style">采集 API 标准时选择的样式.</param>
    [Theory]
    [MemberData(nameof(ContextCases))]
    public void MatchesOfficialContextAndWidthBoundaries(string name, string source, string expected, DaxFormattingStyle style)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        var expectedDefinition = "Probe =\n" + expected;
        var definition = DaxFormatter.Format("Probe =\n" + source, style: style);
        Assert.Equal(expectedDefinition, definition);
        Assert.Equal(definition, DaxFormatter.Format(definition, style: style));

        // 度量正文仅增加定义头换行, 原语法缩进和宽度阈值保持.
        var expectedBody = "\n" + expected;
        var body = DaxFormatter.FormatMeasure(source, style: style);
        Assert.Equal(expectedBody, body);
        Assert.Equal(body, DaxFormatter.FormatMeasure(body, style: style));
        Assert.Equal(body, Assert.Single(DaxFormatter.FormatMeasures([source], style: style)));
    }
}
