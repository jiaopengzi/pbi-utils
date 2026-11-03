// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\ScaffoldCalendarProgram.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 承载独立 Power Query 日期函数及其显式调用的不可变编译结果.

namespace PbiUtils.Application.Calendar;

/// <summary>仅含纯生成结果, 不授予函数名称或模型对象的写入权限.</summary>
/// <param name="FunctionName">确定性生成或由宿主核验的函数查询名.</param>
/// <param name="FunctionExpression">返回函数值的完整 M 查询.</param>
/// <param name="InvocationExpression">仅调用该函数的独立 M 表达式.</param>
/// <param name="Locale">注释和 Documentation 元数据语言.</param>
/// <param name="GeneratorVersion">独立函数生成器版本.</param>
/// <param name="ResourceVersion">本编译器使用的冻结农历规则版本.</param>
public sealed record ScaffoldCalendarProgram(string FunctionName, string FunctionExpression, string InvocationExpression,
    string Locale, string GeneratorVersion, string ResourceVersion);
