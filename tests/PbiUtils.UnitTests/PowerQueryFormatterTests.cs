// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerQueryFormatterTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 本地 M 格式化的结构, 原文保真, 硬限额和取消回归.

using PbiUtils.Application.Code.PowerQuery;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>以用户可见格式化结果和拒绝边界验证本地实现.</summary>
public sealed class PowerQueryFormatterTests
{
    /// <summary>let 绑定分行, 短调用保持内联, 返回值拥有独立缩进.</summary>
    [Fact]
    public void FormatsLetBindingsAndShortCalls()
    {
        Assert.Equal("let\n    x = #date(2024, 1, 1),\n    y = Date.AddDays(x, 1)\nin\n    y", Format("let x=#date(2024,1,1),y=Date.AddDays(x,1) in y"));
    }

    /// <summary>广义标识符的内部多空格与 quoted 标识符的转义保持原文.</summary>
    [Theory]
    [InlineData("let r=[Base  Line=1,#\"A#(lf) B\"=2] in r[Base  Line]")]
    [InlineData("each [1998 Sales]+[true value]+[a.b]")]
    [InlineData("let s=\"#(#)(lf)\"\" // still text\",v=#!\"bad#(cr,lf)text\" in {s,v}")]
    [InlineData("let a=0xff,b=.5e-2,c={1..10} in if a>0 then c{0}? else null")]
    [InlineData("(x as nullable number, optional y as text) as number => if x=null then 0 else x+1")]
    [InlineData("let r=try error Error.Record(\"x\",\"y\",null) otherwise [Base Line=1] in r[[Base Line],[Other]]?")]
    [InlineData("let f=(x)=>@f(x-1) in try f(1) catch (e)=> e[Message]")]
    [InlineData("type table [optional Base  Line=nullable text,...]")]
    [InlineData("let t=type function (x as (type number meta [Documentation.FieldCaption=\"年份\"])) as table meta [Documentation.Name=\"日期\"] in Value.ReplaceType((x)=>#table({\"x\"},{{x}}),t)")]
    [InlineData("section Demo; shared One=1; shared Two=let x=One in x+1;")]
    [InlineData("let optional=1,nullable=2,catch=3 in optional+nullable+catch")]
    [InlineData("let Foo.1=1 in Foo.1")]
    public void PreservesLanguageConstructsAndIsIdempotent(string source)
    {
        var result = Format(source);
        Assert.Equal(result, Format(result));
        if (source.Contains("Base  Line", StringComparison.Ordinal)) Assert.Contains("Base  Line", result, StringComparison.Ordinal);
        if (source.Contains("#(#)(lf)", StringComparison.Ordinal)) Assert.Contains("#(#)(lf)\"\" // still text", result, StringComparison.Ordinal);
    }

    /// <summary>注释原文及行结束边界不被布局吞并, 关键字与括号不进入结构解析.</summary>
    [Fact]
    public void PreservesCommentsAndMultilineLiteralContents()
    {
        const string source = "// let )  \r\nlet x=\"first\r\n  second\",/* keep\r\n  in [ */ y=x // end  \r\nin y";
        var result = Format(source);
        Assert.Contains("// let )  \n", result, StringComparison.Ordinal);
        Assert.Contains("\"first\r\n  second\"", result, StringComparison.Ordinal);
        Assert.Contains("/* keep\r\n  in [ */", result, StringComparison.Ordinal);
        Assert.Contains("// end  \n", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    /// <summary>长记录和调用按宽度断行, 不能切开不可拆字符串.</summary>
    [Fact]
    public void WrapsLongStructuredValuesWithoutSplittingLiterals()
    {
        var literal = new string('a', 90);
        var result = Format("Some.Function(\"" + literal + "\", [One=1,Two=2,Three=3])");
        Assert.Contains("Some.Function(\n", result, StringComparison.Ordinal);
        Assert.Contains("\"" + literal + "\"", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    /// <summary>残缺语法和未知字符整次拒绝, 诊断不回显用户源码.</summary>
    [Theory]
    [InlineData("let x=1")]
    [InlineData("let x= in x")]
    [InlineData("if true then 1")]
    [InlineData("[A=1,B=]")]
    [InlineData("{1,}")]
    [InlineData("(x as number)=>")]
    [InlineData("\"private unfinished")]
    [InlineData("1 /* private")]
    [InlineData("1e-")]
    [InlineData("1 $")]
    public void RejectsBrokenOrUnsupportedSourceWithoutLeakingIt(string source)
    {
        var error = Assert.Throws<AppException>(() => Format(source));
        Assert.Equal("M_FORMAT_INVALID", error.Code);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }

    /// <summary>按 UTF8 字节拒绝超长 Unicode, 结构和词元也独立受限.</summary>
    [Fact]
    public void EnforcesIndependentByteDepthAndTokenBounds()
    {
        Assert.Equal("M_FORMAT_LIMIT", Assert.Throws<AppException>(() => Format("\"" + new string('中', 1024 * 1024) + "\"")).Code);
        Assert.Equal("M_FORMAT_LIMIT", Assert.Throws<AppException>(() => Format(new string('(', 129) + "1" + new string(')', 129))).Code);
        Assert.Equal("M_FORMAT_LIMIT", Assert.Throws<AppException>(() => Format("{" + string.Join(',', Enumerable.Repeat("1", 131073)) + "}")).Code);
    }

    /// <summary>取消立即传播, 有界大覆盖不受 DAX 的 30000 字符限制影响.</summary>
    [Fact]
    public void CancelsAndSupportsTenThousandOverrideRecords()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Format("1", cancellation.Token));
        var source = "{" + string.Join(',', Enumerable.Range(0, 10000).Select(i => "[Date=#date(2024,1,1),IsWorkday=false,Name=\"\"]")) + "}";
        var result = Format(source);
        Assert.True(result.Length > 30000);
        Assert.Equal(result, Format(result));
    }

    /// <summary>正在处理的大批词元可取消, 长运算符链不依赖调用栈深度.</summary>
    [Fact]
    public async Task CancelsInFlightFormattingAndHandlesLongFlatChains()
    {
        var source = "{" + string.Join(',', Enumerable.Repeat("[Date=#date(2024,1,1),IsWorkday=false,Name=\"\"]", 10000)) + "}";
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var pending = Task.Run(() => { started.Set(); return Format(source, cancellation.Token); }, CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(5, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var chain = string.Join('+', Enumerable.Repeat("1", 10000));
        var formatted = Format(chain);
        Assert.Equal(formatted, Format(formatted));
        Assert.Equal(9999, formatted.Count(character => character == '+'));
    }

    /// <summary>不支持的 intrinsic 与不完整广义字段名必须拒绝, 不能误报格式化成功.</summary>
    [Theory]
    [InlineData("#private")]
    [InlineData("[A. = 1]")]
    [InlineData("[A..B = 1]")]
    [InlineData("Foo . Bar")]
    [InlineData("\"a\".B")]
    public void RejectsUnknownIntrinsicsAndBrokenGeneralizedNames(string source)
    {
        Assert.Equal("M_FORMAT_INVALID", Assert.Throws<AppException>(() => Format(source)).Code);
    }

    /// <summary>保留完整 128 层合法括号, 同时拒绝输出排版造成的字节膨胀.</summary>
    [Fact]
    public void AcceptsMaximumParenthesisDepthAndRejectsOversizedLayout()
    {
        var nested = new string('(', 128) + "1" + new string(')', 128);
        var result = Format(nested);
        Assert.Equal(128, result.Count(character => character == '('));
        Assert.Equal(128, result.Count(character => character == ')'));
        Assert.Equal(result, Format(result));
        var nearLimitLiteral = "\"" + new string('a', 3 * 1024 * 1024 - 14) + "\"";
        Assert.Equal("M_FORMAT_LIMIT", Assert.Throws<AppException>(() => Format("let x=" + nearLimitLiteral + " in x")).Code);
    }

    /// <summary>所有场景直接调用与 CLI 和宿主共用的 Application 入口.</summary>
    private static string Format(string expression, CancellationToken cancellationToken = default) => PowerQueryFormatter.Format(expression, cancellationToken);
}
