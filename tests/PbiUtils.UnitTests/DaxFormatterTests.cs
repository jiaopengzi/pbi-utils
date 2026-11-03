// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormatterTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 本地 DAX 格式化的词法边界、幂等、取消和输入约束测试.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Diagnostics;
using PbiUtils.Application.Code.Dax;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class DaxFormatterTests
{
    /// <summary>度量值正文显式保留定义头后的 LF 和顶格正文, 首部已有空白不累积且字符串内部不改写.</summary>
    [Fact]
    public void FormatsMeasureBodyWithLeadingLineAndPreservesMultilineLiteral()
    {
        const string source = "\r\n  VAR ConfigValue=\"first\n second\" VAR Result=ConfigValue RETURN Result";
        const string expected = "\nVAR ConfigValue = \"first\n second\" VAR Result = ConfigValue RETURN Result";
        Assert.Equal(expected, DaxFormatter.FormatMeasure(source));
        Assert.Equal(expected, DaxFormatter.FormatMeasure(expected));
        Assert.Equal("\nT[C] = 1", DaxFormatter.FormatMeasure("T[C]=1"));
        Assert.Equal("1 + 2", DaxFormatter.Format("1+2"));
        Assert.Equal("\n1 + 2", Assert.Single(DaxFormatter.FormatMeasures(["1+2"])));
    }

    /// <summary>度量值新增的首部 LF 计入单项、批次和 JSON 预算, 恰好位于边界时仍能重复调用.</summary>
    [Fact]
    public void AppliesMeasureLeadingWhitespaceToAllBudgets()
    {
        var source = new string('x', 29999);
        var single = DaxFormatter.FormatMeasure(source);
        Assert.Equal(30000, single.Length);
        Assert.Equal(single, DaxFormatter.FormatMeasure(single));
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasure(source + "x")).Code);
        var batch = DaxFormatter.FormatMeasures([new string('x', 29994)]);
        Assert.Equal(batch, DaxFormatter.FormatMeasures(batch));
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasures([new string('x', 29995)])).Code);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasures([new string('x', 14997), new string('x', 14997)])).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.FormatMeasure(source + "x", cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.FormatMeasures([source, source], cancellationToken: cancellation.Token));
    }

    /// <summary>度量定义头的正文换行不占用原有64层结构容量, 第65层仍被明确拒绝.</summary>
    [Theory]
    [InlineData(DaxFormattingStyle.Long)]
    [InlineData(DaxFormattingStyle.Short)]
    public void MeasureHeaderIndentDoesNotConsumeStructuralDepthBudget(DaxFormattingStyle style)
    {
        var source = string.Concat(Enumerable.Repeat("ABS(", 63)) + "DIVIDE(1,2)" + new string(')', 63);
        var formatted = DaxFormatter.FormatMeasure(source, style: style);
        Assert.StartsWith("\nABS (", formatted, StringComparison.Ordinal);
        Assert.InRange(formatted.Length, 1, 30000);
        Assert.Equal(formatted, DaxFormatter.FormatMeasure(formatted, style: style));
        Assert.Equal(DaxFormatter.Format(source, style: style), DaxFormatter.Format(formatted, style: style));
        var definition = DaxFormatter.Format("Amount = " + source, style: style);
        Assert.StartsWith("Amount =\nABS (", definition, StringComparison.Ordinal);
        Assert.Equal(DaxFormatter.Format(source, style: style), definition["Amount =\n".Length..]);
        Assert.Equal(definition, DaxFormatter.Format(definition, style: style));
        var tooDeep = "ABS(" + source + ")";
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasure(tooDeep, style: style)).Code);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.Format(tooDeep, style: style)).Code);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.Format("Amount = " + tooDeep, style: style)).Code);
    }

    /// <summary>SQLBI short 格式对多参数逐行缩进, 简单单参数内联且保留用户变量原名.</summary>
    [Fact]
    public void FormatsSqlbiShortCallsAndVariableBlocks()
    {
        const string source = "VAR config_table=FILTER('Config',[name]=\"version\") VAR config_value=CONCATENATEX(config_table,[value]) RETURN config_value";
        const string expected = "VAR config_table =\n    FILTER (\n        'Config',\n        [name] = \"version\"\n    )\nVAR config_value =\n    CONCATENATEX (\n        config_table,\n        [value]\n    )\nRETURN\n    config_value";
        Assert.Equal(expected, DaxFormatter.Format(source, style: DaxFormattingStyle.Short));
        Assert.Equal(expected, DaxFormatter.Format(expected, style: DaxFormattingStyle.Short));
    }

    /// <summary>度量值定义头独立成行, RETURN 表达式中的多行调用前置二元运算符.</summary>
    [Fact]
    public void SeparatesMeasureHeaderAndBreaksBeforeBinaryCall()
    {
        const string source = "Sales Amount = SUM(Sales[Amount])+DIVIDE([Profit],[Cost])";
        const string expected = "Sales Amount =\nSUM ( Sales[Amount] )\n    + DIVIDE (\n        [Profit],\n        [Cost]\n    )";
        Assert.Equal(expected, DaxFormatter.Format(source, style: DaxFormattingStyle.Short));
        Assert.Equal(expected, DaxFormatter.Format(expected, style: DaxFormattingStyle.Short));
    }

    /// <summary>Long 允许嵌套单参数调用内联, Short 分行; 两者均不改写 UDF 大小写、日期字面量或注释内空白.</summary>
    [Fact]
    public void PreservesNamesLiteralsAndCommentTokens()
    {
        const string source = "VAR lower_case=Company.MyUdf(ABS(-1),dt\"2026-10-30\",\"VAR x=1  RETURN x\") /*keep  two*/ RETURN lower_case";
        var actual = DaxFormatter.Format(source);
        Assert.Contains("VAR lower_case =\n    Company.MyUdf (", actual);
        Assert.Contains("ABS ( -1 )", actual);
        Assert.Contains("dt\"2026-10-30\"", actual);
        Assert.Contains("\"VAR x=1  RETURN x\"", actual);
        Assert.Contains("/*keep  two*/", actual);
        Assert.Equal(actual, DaxFormatter.Format(actual));
        Assert.Equal("ABS ( SUM ( T[A] ) )", DaxFormatter.Format("ABS(SUM(T[A]))"));
        Assert.Equal("ABS (\n    SUM ( T[A] )\n)", DaxFormatter.Format("ABS(SUM(T[A]))", style: DaxFormattingStyle.Short));
    }

    /// <summary>引号、注释及列/度量值引用保持原文, 格式化前后的真实依赖集合一致.</summary>
    [Fact]
    public void PreservesDependenciesAndQualifiedOperatorIndent()
    {
        const string source = "VAR lower_case=SUM('O''Brien'[A]])B]) // keep  two spaces  \nVAR Result=lower_case+Company.MixedCase(\"a  b\",[Measure]) RETURN Result";
        var formatted = DaxFormatter.Format(source, style: DaxFormattingStyle.Short);
        Assert.Contains("// keep  two spaces  \n", formatted);
        Assert.Contains("'O''Brien'[A]])B]", formatted);
        Assert.Contains("\n        + Company.MixedCase (\n            \"a  b\",\n            [Measure]\n        )", formatted);
        Assert.True(DaxFormatter.TryReadFunctionCalls(source, out var callsBefore));
        Assert.True(DaxFormatter.TryReadFunctionCalls(formatted, out var callsAfter));
        Assert.True(callsBefore.SetEquals(callsAfter));
        Assert.True(DaxFormatter.TryReadMeasureReferences(source, out var referencesBefore));
        Assert.True(DaxFormatter.TryReadMeasureReferences(formatted, out var referencesAfter));
        Assert.Equal(referencesBefore, referencesAfter);
        Assert.Equal(formatted, DaxFormatter.Format(formatted, style: DaxFormattingStyle.Short));
    }

    /// <summary>Short 展开调用后的二元调用续行以运算符起始, 末尾短标量保持与同一子表达式同行.</summary>
    [Fact]
    public void KeepsEveryBinaryContinuationAtLineStart()
    {
        const string source = "DIVIDE([Amount],[Quantity])+SUM(T[Tax])-1";
        const string expected = "DIVIDE (\n    [Amount],\n    [Quantity]\n)\n    + SUM ( T[Tax] ) - 1";
        Assert.Equal(expected, DaxFormatter.Format(source, style: DaxFormattingStyle.Short));
        Assert.Equal(expected, DaxFormatter.Format(expected, style: DaxFormattingStyle.Short));
    }

    /// <summary>前置块注释与声明分行, 尾随注释保留附着位置, 不改注释或用户变量原文.</summary>
    [Fact]
    public void KeepsBlockCommentsSeparateFromVariableAndReturnHeaders()
    {
        const string source = "/* config */ VAR Value=1 /* final */ RETURN Value";
        const string expected = "/* config */\nVAR Value = 1 /* final */\nRETURN Value";
        Assert.Equal(expected, DaxFormatter.Format(source));
        Assert.Equal(expected, DaxFormatter.Format(expected));
    }

    /// <summary>显式样式决定普通多参数调用; DATE 遵循官方内联特例, 超长实参也不自行拆行.</summary>
    [Theory]
    [InlineData(DaxFormattingStyle.Long, "DIVIDE ( 1, 2 )")]
    [InlineData(DaxFormattingStyle.Short, "DIVIDE (\n    1,\n    2\n)")]
    public void FormatsRequestedCallsAndKeepsDateArgumentsInline(DaxFormattingStyle style, string expectedDivide)
    {
        Assert.Equal(expectedDivide, DaxFormatter.Format("DIVIDE(1,2)", style: style));
        var reference = "'A very long table name used in formatting tests'[A very long date column name used in formatting tests]";
        var source = "DATE(YEAR(" + reference + "),MONTH(" + reference + "),DAY(" + reference + "))";
        var result = DaxFormatter.Format(source, style: style);
        Assert.Equal($"DATE ( YEAR ( {reference} ), MONTH ( {reference} ), DAY ( {reference} ) )", result);
        Assert.Equal(result, DaxFormatter.Format(result, style: style));
    }

    /// <summary>查询定义头按名称结构识别, 不以固定词元数量猜测等号位置.</summary>
    [Fact]
    public void FormatsQualifiedDefinitionHeadersWithoutFixedLookahead()
    {
        const string source = "DEFINE FUNCTION Company.Reporting.Finance.Net=(x: SCALAR EXPR) => x EVALUATE {Company.Reporting.Finance.Net(1)}";
        var result = DaxFormatter.Format(source, style: DaxFormattingStyle.Short);
        Assert.Contains("FUNCTION Company.Reporting.Finance.Net =", result, StringComparison.Ordinal);
        Assert.Contains("\nEVALUATE\n", result, StringComparison.Ordinal);
        Assert.Equal(result, DaxFormatter.Format(result, style: DaxFormattingStyle.Short));
    }

    /// <summary>使用实际 API 的 Short/Long 基准验证典型表达式, 仅保留原词元大小写, 第二次调用无差异.</summary>
    [Theory]
    [InlineData(DaxFormattingStyle.Long, "SUM('Sales'[Amount])", "SUM ( 'Sales'[Amount] )")]
    [InlineData(DaxFormattingStyle.Short, "SUM('Sales'[Amount])", "SUM ( 'Sales'[Amount] )")]
    [InlineData(DaxFormattingStyle.Long, "VAR x=SUM('Sales'[Amount]) RETURN DIVIDE(x,2)", "VAR x = SUM ( 'Sales'[Amount] ) RETURN DIVIDE ( x, 2 )")]
    [InlineData(DaxFormattingStyle.Short, "VAR x=SUM('Sales'[Amount]) RETURN DIVIDE(x,2)", "VAR x =\n    SUM ( 'Sales'[Amount] )\nRETURN\n    DIVIDE (\n        x,\n        2\n    )")]
    [InlineData(DaxFormattingStyle.Long, "sum(Table[Value])+1e-3", "sum ( Table[Value] ) + 1e-3")]
    [InlineData(DaxFormattingStyle.Short, "sum(Table[Value])+1e-3", "sum ( Table[Value] ) + 1e-3")]
    [InlineData(DaxFormattingStyle.Long, "{(1,2),(3,4)}", "{ ( 1, 2 ), ( 3, 4 ) }")]
    [InlineData(DaxFormattingStyle.Short, "{(1,2),(3,4)}", "{\n    ( 1, 2 ),\n    ( 3, 4 )\n}")]
    [InlineData(DaxFormattingStyle.Long, "dt\"2024-01-01\"+1", "dt\"2024-01-01\" + 1")]
    [InlineData(DaxFormattingStyle.Short, "dt\"2024-01-01\"+1", "dt\"2024-01-01\" + 1")]
    [InlineData(DaxFormattingStyle.Long, "IF([A]==1&&[B]<>2,\"a  b\",\"x\"\"y\")", "IF ( [A] == 1 && [B] <> 2, \"a  b\", \"x\"\"y\" )")]
    [InlineData(DaxFormattingStyle.Short, "IF([A]==1&&[B]<>2,\"a  b\",\"x\"\"y\")", "IF (\n    [A] == 1 && [B] <> 2,\n    \"a  b\",\n    \"x\"\"y\"\n)")]
    [InlineData(DaxFormattingStyle.Long, "'O''Brien'[a]]b]+.5", "'O''Brien'[a]]b] + .5")]
    [InlineData(DaxFormattingStyle.Short, "'O''Brien'[a]]b]+.5", "'O''Brien'[a]]b] + .5")]
    [InlineData(DaxFormattingStyle.Long, "// comma , ) \n1+2", "// comma , ) \n1 + 2")]
    [InlineData(DaxFormattingStyle.Short, "// comma , ) \n1+2", "// comma , ) \n1 + 2")]
    [InlineData(DaxFormattingStyle.Long, "1-- keep two spaces  \n+2", "1 -- keep two spaces  \n+ 2")]
    [InlineData(DaxFormattingStyle.Short, "1-- keep two spaces  \n+2", "1 -- keep two spaces  \n+ 2")]
    [InlineData(DaxFormattingStyle.Long, "1 /* ( \" */ + 2", "1 /* ( \" */\n+ 2")]
    [InlineData(DaxFormattingStyle.Short, "1 /* ( \" */ + 2", "1 /* ( \" */\n+ 2")]
    [InlineData(DaxFormattingStyle.Long, "'中文 表'[金额]+[利润]", "'中文 表'[金额] + [利润]")]
    [InlineData(DaxFormattingStyle.Short, "'中文 表'[金额]+[利润]", "'中文 表'[金额] + [利润]")]
    [InlineData(DaxFormattingStyle.Long, "Namespace.MyFunc(1,-2)", "Namespace.MyFunc ( 1, -2 )")]
    [InlineData(DaxFormattingStyle.Short, "Namespace.MyFunc(1,-2)", "Namespace.MyFunc (\n    1,\n    -2\n)")]
    [InlineData(DaxFormattingStyle.Long, "DATE(2026,10,13)", "DATE ( 2026, 10, 13 )")]
    [InlineData(DaxFormattingStyle.Short, "DATE(2026,10,13)", "DATE ( 2026, 10, 13 )")]
    [InlineData(DaxFormattingStyle.Long, "SWITCH(TRUE(),[A]>1,\"A\",[A]>0,\"B\",\"C\")", "SWITCH ( TRUE (), [A] > 1, \"A\", [A] > 0, \"B\", \"C\" )")]
    [InlineData(DaxFormattingStyle.Short, "SWITCH(TRUE(),[A]>1,\"A\",[A]>0,\"B\",\"C\")", "SWITCH (\n    TRUE (),\n    [A] > 1, \"A\",\n    [A] > 0, \"B\",\n    \"C\"\n)")]
    [InlineData(DaxFormattingStyle.Long, "ROW(\"A\",1,\"B\",2)", "ROW ( \"A\", 1, \"B\", 2 )")]
    [InlineData(DaxFormattingStyle.Short, "ROW(\"A\",1,\"B\",2)", "ROW (\n    \"A\", 1,\n    \"B\", 2\n)")]
    [InlineData(DaxFormattingStyle.Long, "ADDCOLUMNS(T,\"A\",[x],\"B\",[y])", "ADDCOLUMNS ( T, \"A\", [x], \"B\", [y] )")]
    [InlineData(DaxFormattingStyle.Short, "ADDCOLUMNS(T,\"A\",[x],\"B\",[y])", "ADDCOLUMNS (\n    T,\n    \"A\", [x],\n    \"B\", [y]\n)")]
    [InlineData(DaxFormattingStyle.Long, "SUMX(T,VAR x=1 RETURN x+2)", "SUMX ( T, VAR x = 1 RETURN x + 2 )")]
    [InlineData(DaxFormattingStyle.Short, "SUMX(T,VAR x=1 RETURN x+2)", "SUMX (\n    T,\n    VAR x = 1\n    RETURN\n        x + 2\n)")]
    [InlineData(DaxFormattingStyle.Long, "VAR a=VAR b=1 RETURN b VAR c=2 RETURN a+c", "VAR a = VAR b = 1 RETURN b VAR c = 2 RETURN a + c")]
    [InlineData(DaxFormattingStyle.Short, "VAR a=VAR b=1 RETURN b VAR c=2 RETURN a+c", "VAR a =\nVAR b = 1\nRETURN\n    b\nVAR c = 2\nRETURN\n    a + c")]
    [InlineData(DaxFormattingStyle.Long, "VAR a=1 RETURN 1+VAR b=2 RETURN b", "VAR a = 1 RETURN 1 + VAR b = 2 RETURN b")]
    [InlineData(DaxFormattingStyle.Short, "VAR a=1 RETURN 1+VAR b=2 RETURN b", "VAR a = 1\nRETURN\n    1 + VAR b = 2 RETURN b")]
    [InlineData(DaxFormattingStyle.Long, "-[A]- -[B]", "- [A] - - [B]")]
    [InlineData(DaxFormattingStyle.Short, "-[A]- -[B]", "- [A] - - [B]")]
    [InlineData(DaxFormattingStyle.Long, "- -1", "- -1")]
    [InlineData(DaxFormattingStyle.Short, "- -1", "- -1")]
    [InlineData(DaxFormattingStyle.Long, "EVALUATE FILTER(T,[x]>1) ORDER BY [x] DESC", "EVALUATE\nFILTER ( T, [x] > 1 )\nORDER BY [x] DESC")]
    [InlineData(DaxFormattingStyle.Short, "EVALUATE FILTER(T,[x]>1) ORDER BY [x] DESC", "EVALUATE\nFILTER (\n    T,\n    [x] > 1\n)\nORDER BY [x] DESC")]
    [InlineData(DaxFormattingStyle.Long, "DEFINE VAR a=1 VAR b=2 EVALUATE {a,b}", "DEFINE\n    VAR a = 1\n    VAR b = 2\n\nEVALUATE\n{ a, b }")]
    [InlineData(DaxFormattingStyle.Short, "DEFINE VAR a=1 VAR b=2 EVALUATE {a,b}", "DEFINE\n    VAR a = 1\n    VAR b = 2\n\nEVALUATE\n{\n    a,\n    b\n}")]
    [InlineData(DaxFormattingStyle.Long, "DEFINE MEASURE T[x]=VAR a=1 RETURN a MEASURE T[y]=2 EVALUATE T", "DEFINE\n    MEASURE T[x] = VAR a = 1 RETURN a\n    MEASURE T[y] = 2\n\nEVALUATE\nT")]
    [InlineData(DaxFormattingStyle.Short, "DEFINE MEASURE T[x]=VAR a=1 RETURN a MEASURE T[y]=2 EVALUATE T", "DEFINE\n    MEASURE T[x] =\n    VAR a = 1\n    RETURN\n        a\n    MEASURE T[y] = 2\n\nEVALUATE\nT")]
    [InlineData(DaxFormattingStyle.Long, "DEFINE FUNCTION F=(a: SCALAR EXPR) => VAR x=a RETURN x EVALUATE {F(1)}", "DEFINE\n    FUNCTION F = (\n            a : SCALAR EXPR\n        ) =>\n        VAR x = a RETURN x\n\nEVALUATE\n{ F ( 1 ) }")]
    [InlineData(DaxFormattingStyle.Short, "DEFINE FUNCTION F=(a: SCALAR EXPR) => VAR x=a RETURN x EVALUATE {F(1)}", "DEFINE\n    FUNCTION F = (\n            a : SCALAR EXPR\n        ) =>\n        VAR x = a\n        RETURN\n            x\n\nEVALUATE\n{\n    F ( 1 )\n}")]
    public void PreservesTokensAndIsIdempotent(DaxFormattingStyle style, string source, string expected)
    {
        var result = DaxFormatter.Format(source, style: style);
        Assert.Equal(expected, result);
        Assert.Equal(result, DaxFormatter.Format(result, style: style));
    }

    /// <summary>长参数组与嵌套 VAR 块必须缩进, 注释内的关键字和括号不参与分组.</summary>
    [Fact]
    public void IndentsNestedGroupsAndKeepsComments()
    {
        const string source = "CALCULATE(SUM('Sales'[Amount]),FILTER('Sales',VAR total=SUM('Sales'[Amount]) RETURN total>100),/* keep ) VAR */'Sales'[Status]=\"Active\")";
        var result = DaxFormatter.Format(source, style: DaxFormattingStyle.Short);
        Assert.Contains("CALCULATE (\n    SUM", result);
        Assert.Contains("\n        VAR total", result);
        Assert.Contains("/* keep ) VAR */", result);
        Assert.Equal(result, DaxFormatter.Format(result, style: DaxFormattingStyle.Short));
    }

    /// <summary>残缺词法和括号不得用猜测补齐, 全部返回稳定错误且不携带表达式.</summary>
    [Theory]
    [InlineData("SUM([x]")]
    [InlineData("SUM([x]}")]
    [InlineData("\"private unfinished")]
    [InlineData("'table")]
    [InlineData("[measure")]
    [InlineData("1 /* secret")]
    [InlineData("1e-")]
    [InlineData("1 ? 2")]
    [InlineData("VAR x=1")]
    [InlineData("VAR x=1 RETURN")]
    public void RejectsBrokenInput(string source) => Assert.Equal("DAX_FORMAT_INVALID", Assert.Throws<AppException>(() => DaxFormatter.Format(source)).Code);

    /// <summary>整个批次在本机处理; 任意失败、大小限制或取消不返回部分结果.</summary>
    [Fact]
    public void BatchLimitsAndCancellation()
    {
        Assert.Equal(["1 + 2", "SUM ( [x] )"], DaxFormatter.FormatMany(["1+2", "SUM([x])"]));
        Assert.Throws<AppException>(() => DaxFormatter.FormatMany(["1+2", "SUM("]));
        Assert.Throws<AppException>(() => DaxFormatter.FormatMany([]));
        Assert.Throws<AppException>(() => DaxFormatter.FormatMany(Enumerable.Repeat("1", 129).ToArray()));
        Assert.Throws<AppException>(() => DaxFormatter.Format(new string('(', 65) + "1" + new string(')', 65)));
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.Format(string.Concat(Enumerable.Repeat("1+VAR x=1 RETURN ", 70)) + "1")).Code);
        Assert.Throws<AppException>(() => DaxFormatter.Format(new string(' ', 30001)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.Format("1", cancellationToken: cancellation.Token));
    }

    /// <summary>合法输入若因排版扩张超过编辑/保存上限必须完整失败, 不回填无法再次格式化的文本.</summary>
    [Fact]
    public void RejectsExpandedExpressionBeyondReusableInputLimit()
    {
        // 7500 个二元加号各增加两个空格, 15001 字符输入会扩张为30001字符.
        var source = string.Concat(Enumerable.Repeat("1+", 7500)) + "1";
        Assert.Equal(15001, source.Length);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.Format(source)).Code);
    }

    /// <summary>单项均能安全格式化时, 批次输出总扩张仍不得越过下次调用的总长度边界.</summary>
    [Fact]
    public void RejectsExpandedBatchBeyondReusableInputLimit()
    {
        var source = string.Concat(Enumerable.Repeat("1+", 4000)) + "1";
        Assert.Equal(8001, source.Length);
        var single = DaxFormatter.Format(source);
        Assert.Equal(16001, single.Length);
        Assert.Equal(single, DaxFormatter.Format(single));
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMany([source, source])).Code);
    }

    /// <summary>恰好位于字符边界的表达式与批次仍可重复格式化, 预先取消优先于容量错误.</summary>
    [Fact]
    public void AcceptsExactOutputLimitAndPrioritizesCancellation()
    {
        var single = "\"" + new string('x', 29998) + "\"";
        Assert.Equal(30000, DaxFormatter.Format(single).Length);
        Assert.Equal(single, DaxFormatter.Format(DaxFormatter.Format(single)));
        var batch = DaxFormatter.FormatMany([new string('x', 14996), new string('x', 14997)]);
        Assert.Equal(29993, batch.Sum(item => item.Length));
        Assert.Equal(batch, DaxFormatter.FormatMany(batch));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.Format(new string('1', 30001), cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.FormatMany([single, single], cancellationToken: cancellation.Token));
    }

    /// <summary>字符串中的 LF 和引号在 JSON 数组中再扩张7字符, 原文29994字符虽可单项格式化, 批次传输仍须拒绝.</summary>
    [Fact]
    public void RejectsExpandedBatchJsonTransferBeyondReusableLimit()
    {
        var source = "\"" + new string('x', 29991) + "\n\"";
        var single = DaxFormatter.Format(source);
        Assert.Equal(source, single);
        Assert.Equal(29994, single.Length);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMany([source])).Code);
    }

    /// <summary>JSON 预算使用 JavaScript 的 UTF-16 长度, 中文无需额外转义, 孤立代理码位则必须转义.</summary>
    [Fact]
    public void CountsJsonBudgetForUnicodeAndRequiredEscapes()
    {
        var unicode = new string('名', 29996);
        Assert.Equal(unicode, Assert.Single(DaxFormatter.FormatMany([unicode])));
        var unpairedSurrogate = "\"" + new string('x', 29990) + '\ud800' + "\"";
        Assert.InRange(unpairedSurrogate.Length, 1, 30000);
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() => DaxFormatter.FormatMany([unpairedSurrogate])).Code);
    }

    /// <summary>通过实际桥接调用同一函数, 无需模型、账号、网络客户端或任意端点.</summary>
    [Fact]
    public async Task BridgeFormatsLocallyAndRejectsAdditionalFields()
    {
        ConcurrentQueue<string> messages = new();
        using var bridge = new BridgeSession(new OriginPolicy(false, null), new DoctorService(new RuntimeEnvironment()),
            messages.Enqueue, "test");
        await bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "format", method = "dax.format", @params = new { value = "[\"VAR Result=SUM([x]) RETURN Result\"]", style = "long" } }));
        using var response = JsonDocument.Parse(messages.Last());
        Assert.Equal("\nVAR Result = SUM ( [x] ) RETURN Result", response.RootElement.GetProperty("result").GetProperty("expressions")[0].GetString());
        await bridge.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "invalid", method = "dax.format", @params = new { value = "[\"1\"]", style = "long", endpoint = "https://invalid.example" } }));
        using var invalid = JsonDocument.Parse(messages.Last());
        Assert.Equal("VALIDATION_ERROR", invalid.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
