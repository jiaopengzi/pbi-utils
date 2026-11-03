// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ModelTableRowCountQueryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证固定计数模板的名称转义、空表0语义和批次边界, 不连接引擎或返回业务值.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed partial class ModelTableRowCountQueryTests
{
    /// <summary>默认 long 输出与真实 API 相同, 表标识符与结果字符串分别转义, 特殊引号不改变实际模型名称.</summary>
    [Fact]
    public void QuotesEscapeIndependentlyAndEmptyTablesUseZero()
    {
        var query = ModelTableRowCountQuery.Create(["导出'O\"Brien]"]);
        Assert.Equal("EVALUATE\nROW (\n    \"__Table\", \"导出'O\"\"Brien]\",\n    \"__Rows\", COALESCE ( COUNTROWS ( '导出''O\"Brien]' ), 0 )\n)", query);
        Assert.DoesNotContain("ALL (", query); Assert.DoesNotContain("UNION (", query);
    }

    /// <summary>单表短调用内联, 多表逐项按宽度布局并保留 ROW 参数对, 期望来自真实 API long 响应.</summary>
    [Fact]
    public void SingleAndMultipleTablesUseLongLayout()
    {
        Assert.Equal("EVALUATE\nROW ( \"__Table\", \"Sales\", \"__Rows\", COALESCE ( COUNTROWS ( 'Sales' ), 0 ) )", ModelTableRowCountQuery.Create(["Sales"]));
        Assert.Equal("""
            EVALUATE
            UNION (
                ROW ( "__Table", "Sales", "__Rows", COALESCE ( COUNTROWS ( 'Sales' ), 0 ) ),
                ROW (
                    "__Table", "Products",
                    "__Rows", COALESCE ( COUNTROWS ( 'Products' ), 0 )
                )
            )
            """, ModelTableRowCountQuery.Create(["Sales", "Products"]));
    }

    /// <summary>多表同批只生成一个UNION结果表, 不按每表创建查询, 上限50被固定生成器复核.</summary>
    [Fact]
    public void FiftyTablesShareOneResultAndNextTableIsRejected()
    {
        var names = Enumerable.Range(0, 50).Select(index => "T" + index).ToArray();
        var query = ModelTableRowCountQuery.Create(names);
        Assert.StartsWith("EVALUATE\nUNION (", query);
        Assert.Equal(50, MyRegex().Count(query));
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => ModelTableRowCountQuery.Create([.. names, "T50"])).Code);
    }

    /// <summary>规范布局不引入交互式格式化长度限制, 合法长表名及字面量中的换行原样保留.</summary>
    [Fact]
    public void LongBatchesAndEmbeddedLineBreaksKeepOriginalCapacityAndNames()
    {
        var names = Enumerable.Range(0, 50).Select(index => new string('名', 505) + index).ToArray();
        var query = ModelTableRowCountQuery.Create(names);
        Assert.True(query.Length > 30000);
        Assert.Equal(50, MyRegex().Count(query));
        Assert.Contains("COUNTROWS ( '首行\n次行' )", ModelTableRowCountQuery.Create(["首行\n次行"]));
    }

    /// <summary>固定模板不能接受空或重复表选择, 也不接受空字符隐藏查询内容.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("nul")]
    public void InvalidBatchesDoNotProduceQueries(string kind)
    {
        string[] names = kind switch { "empty" => [], "duplicate" => ["Sales", "Sales"], _ => ["Sales\0"] };
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => ModelTableRowCountQuery.Create(names)).Code);
    }

    /// <summary>预先取消优先于空批次验证, 保留调用方的取消令牌且不生成查询.</summary>
    [Fact]
    public void PreCancelledGenerationPreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => ModelTableRowCountQuery.Create([], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>验证表名途中取消须立即停止本地遍历, 不被后续名称错误或枚举失败覆盖.</summary>
    [Fact]
    public void CancellationDuringNameEnumerationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => ModelTableRowCountQuery.Create(new CancellingNames(cancellation), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>在首次表名枚举时确定性取消, 继续推进即失败, 不依赖并发时间窗口.</summary>
    private sealed class CancellingNames(CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => index == 0 ? "Sales" : throw new ArgumentOutOfRangeException(nameof(index));
        /// <summary>返回首个名称前取消, 后续读取抛错以验证本地生成已停止.</summary>
        public IEnumerator<string> GetEnumerator()
        {
            cancellation.Cancel();
            yield return this[0];
            throw new InvalidOperationException("取消后不应继续读取模型表名");
        }
        /// <summary>非泛型枚举复用同一取消与失败行为.</summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>统计查询保持每表一个计数调用, 允许规范括号空格.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("COUNTROWS \\(")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
