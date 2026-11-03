// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerQueryQueryReferenceTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证查询重命名前的 M 词法依赖证明与非法字符保守边界.

using PbiUtils.Application.Code.PowerQuery;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class PowerQueryQueryReferenceTests
{
    /// <summary>查询名按 M 字符转义单次解码, 注释和字符串保持非引用; 不接受无法证明的动态或非法源码.</summary>
    [Theory]
    [InlineData("Query()", true)]
    [InlineData("#\"Q#(0075)ery\"()", true)]
    [InlineData("#\"Q#(00000075)ery\"()", true)]
    [InlineData("#\"Q#(0075,0065)ry\"()", true)]
    [InlineData("#\"Q#(#)(0075)ery\"()", false)]
    [InlineData("#\"Q\"\"uery\"()", false)]
    [InlineData("\"Query #shared\" // Query\n", false)]
    [InlineData("OtherQuery()", false)]
    [InlineData("#\"#(FFFFFFFF)\"", true)]
    [InlineData("#\"#(80000000)\"", true)]
    [InlineData("#\"#(00110000)\"", true)]
    [InlineData("#\"unterminated", true)]
    [InlineData("#shared", true)]
    [InlineData("#sections", true)]
    [InlineData("#\"Expression.Eval#(0075)ate\"(\"1\")", true)]
    [InlineData("Expression /* comment */ . Evaluate(\"1\")", true)]
    public void ConservativeQueryReferenceProof(string expression, bool expected)
    {
        Assert.Equal(expected, PowerQueryFormatter.MayReferenceQuery(expression, "Query"));
    }

    /// <summary>有界词法遵守调用方取消, 不把取消吞成名称依赖.</summary>
    [Fact]
    public void CanceledReferenceProofPropagatesCancellation()
    {
        Assert.Throws<OperationCanceledException>(() => PowerQueryFormatter.MayReferenceQuery("Query", "Query", new(true)));
    }
}
