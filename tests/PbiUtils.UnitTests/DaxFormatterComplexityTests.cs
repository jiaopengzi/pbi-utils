// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DaxFormatterComplexityTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证深层成对参数的排版容量, 防止重复递归导致极小公式长时间占用宿主.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class DaxFormatterComplexityTests
{
    /// <summary>不足600字符的28层 SWITCH 应在宽松取消预算内完成并保持幂等, 不因参数配对指数重排.</summary>
    [Theory]
    [InlineData(DaxFormattingStyle.Short)]
    [InlineData(DaxFormattingStyle.Long)]
    public void NestedPairedArgumentsFinishWithinCancellationBudget(DaxFormattingStyle style)
    {
        var source = string.Concat(Enumerable.Repeat("SWITCH(TRUE(),1=1,", 28)) + "1" + string.Concat(Enumerable.Repeat(",0)", 28));
        Assert.InRange(source.Length, 1, 600);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var formatted = DaxFormatter.FormatMeasure(source, style, cancellation.Token);
        Assert.InRange(formatted.Length, 1, 30000);
        Assert.Equal(formatted, DaxFormatter.FormatMeasure(formatted, style, cancellation.Token));
        Assert.Equal(28, formatted.Split("SWITCH (", StringSplitOptions.None).Length - 1);
    }

    /// <summary>非法样式在全部共享入口明确失败, 预取消仍优先于样式校验, 不返回部分批次.</summary>
    [Fact]
    public void RejectsUnknownStylesAndPreservesCancellationPriority()
    {
        const DaxFormattingStyle unknown = (DaxFormattingStyle)42;
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.Format("1", unknown)).Code);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasure("1", unknown)).Code);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.FormatMany(["1", "2"], unknown)).Code);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => DaxFormatter.FormatMeasures(["1", "2"], unknown)).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.Format("1", unknown, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => DaxFormatter.FormatMeasures(["1"], unknown, cancellation.Token));
    }
}
