// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendarM.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 编译可复制且无外部依赖的确定性Power Query M日期函数.

using PbiUtils.Core;

namespace PbiUtils.Application.Calendar;

/// <summary>生成原生date和固定标签的M分区, 仅携带315字节农历年规则与显式参数.</summary>
public static class ScaffoldCalendarM
{
    /// <summary>当前独立命名日期函数的生成版本.</summary>
    public const string FunctionGeneratorVersion = "calendar-m-v5";

    /// <summary>同一模型中的独立日期函数固定名称, 不随表名、字段或语言变化.</summary>
    public const string FunctionName = "PbiUtilsCalendar";

    /// <summary>按宿主核定的最终映射编译独立函数和四个显式参数的调用.</summary>
    /// <param name="options">完整日期, 工作周与覆盖选项.</param>
    /// <param name="columns">实际源列与字段语义, 允许同语义的多个已有别名.</param>
    /// <param name="locale">关键注释和函数说明的语言, 仅支持 zh-cn 与 en-us.</param>
    /// <param name="functionName">精确函数名称; 产品生成使用缺省固定名称.</param>
    /// <param name="cancellationToken">取消纯编译和格式化的令牌.</param>
    /// <returns>函数查询定义, 单独调用及其生成版本.</returns>
    /// <exception cref="AppException">参数, 映射, 语言或输出边界无效.</exception>
    /// <exception cref="OperationCanceledException">编译已取消.</exception>
    public static ScaffoldCalendarProgram Compile(ScaffoldCalendarOptions options, IReadOnlyList<ScaffoldCalendarColumn> columns,
        string locale = "zh-cn", string? functionName = null, CancellationToken cancellationToken = default)
        => ScaffoldCalendarProgramCompiler.Compile(options, columns, locale, functionName, cancellationToken);

}
