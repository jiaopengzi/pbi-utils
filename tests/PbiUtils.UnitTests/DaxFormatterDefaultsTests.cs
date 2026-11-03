// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormatterDefaultsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立固定所有共享格式化入口和枚举零值的 Long 默认行为.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>直接断言已采集的 Long 排版, 避免生成器与默认参数同时漂移时互相比对仍通过.</summary>
public sealed class DaxFormatterDefaultsTests
{
    /// <summary>省略样式的单项、批次、通用与度量正文入口均采用 Long, 保留正文定义头换行.</summary>
    [Fact]
    public void OmittedStyleUsesLongAcrossEveryEntryPoint()
    {
        const string source = "DIVIDE(1,2)";
        const string expected = "DIVIDE ( 1, 2 )";
        Assert.Equal(expected, DaxFormatter.Format(source));
        Assert.Equal(expected, Assert.Single(DaxFormatter.FormatMany([source])));
        Assert.Equal("\n" + expected, DaxFormatter.FormatMeasure(source));
        Assert.Equal("\n" + expected, Assert.Single(DaxFormatter.FormatMeasures([source])));
    }

    /// <summary>默认枚举值同样采用 Long, 避免调用方传入未初始化选项后隐式退回 Short.</summary>
    [Fact]
    public void DefaultStyleValueUsesLongAcrossEveryEntryPoint()
    {
        const string source = "DIVIDE(1,2)";
        const string expected = "DIVIDE ( 1, 2 )";
        Assert.Equal(expected, DaxFormatter.Format(source, default));
        Assert.Equal(expected, Assert.Single(DaxFormatter.FormatMany([source], default)));
        Assert.Equal("\n" + expected, DaxFormatter.FormatMeasure(source, default));
        Assert.Equal("\n" + expected, Assert.Single(DaxFormatter.FormatMeasures([source], default)));
    }

    /// <summary>后台固定查询也使用已采集的 Long 查询布局, 与交互入口共用词元保真规则.</summary>
    [Fact]
    public void GeneratedQueryUsesLong()
        => Assert.Equal("EVALUATE\nFILTER ( T, [x] > 1 )\nORDER BY [x] DESC",
            DaxFormatter.FormatGeneratedQuery("EVALUATE FILTER(T,[x]>1) ORDER BY [x] DESC"));

    /// <summary>大模型查询不继承交互容量限制, 超长多行字面量仍逐字保留, 交互入口继续拒绝超限.</summary>
    [Fact]
    public void GeneratedQueryPreservesLargeLiteralWithoutRelaxingInteractiveLimits()
    {
        var literal = "\"" + new string('x', 130001) + "\n  unchanged\"";
        var source = "EVALUATE ROW(\"Text\"," + literal + ")";
        var result = DaxFormatter.FormatGeneratedQuery(source);
        Assert.Contains(literal, result, StringComparison.Ordinal);
        Assert.Equal(result, DaxFormatter.FormatGeneratedQuery(result));
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.Format(source)).Code);
    }

    /// <summary>容量独立的后台入口仍拒绝无效结构与过深嵌套, 并在解析前优先传播取消.</summary>
    [Fact]
    public void GeneratedQueryRetainsValidationDepthAndCancellation()
    {
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.FormatGeneratedQuery(" ")).Code);
        Assert.Throws<AppException>(() => DaxFormatter.FormatGeneratedQuery("EVALUATE ROW("));
        Assert.Equal("DAX_FORMAT_LIMIT", Assert.Throws<AppException>(() =>
            DaxFormatter.FormatGeneratedQuery(new string('(', 66) + "1" + new string(')', 66))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.FormatGeneratedQuery("", cancellation.Token));
    }
}
